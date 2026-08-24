using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// System.Management.Automation defines its own ErrorCategory. Aliasing rather than
// importing the Models namespace keeps every reference below unambiguous about which one is
// meant — these values are written to the inventory database and must not drift.
using ErrorCategory = HybridAgentDeploy.Core.Models.ErrorCategory;
using OperatorCredential = HybridAgentDeploy.Core.Models.OperatorCredential;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// The production transport: SMB for staging and retrieval, PowerShell Remoting over WinRM
/// for execution (PRD 5.3, Phase 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Local staging is the point.</b> The MSI is copied to the target's own disk and msiexec
/// runs entirely locally on that host. Running <c>msiexec /i \\server\share\agent.msi</c>
/// inside a remoting session fails in most environments because the session cannot delegate
/// the operator's Kerberos ticket to a third host — the double-hop problem. Staging sidesteps
/// delegation altogether: no CredSSP, no resource-based constrained delegation, and no
/// configuration change on a Tier 0 host. PRD 5.4 calls this the single most important design
/// decision in the tool.
/// </para>
/// <para>
/// SEC3 and SEC4: the authentication mechanism is hardcoded to Negotiate (Kerberos, falling
/// back to NTLM) and <see cref="TransportOptions"/> deliberately exposes no way to change it.
/// CredSSP is never offered and must never be added — local staging makes it unnecessary, and
/// enabling it against a domain controller is a security regression. Basic is likewise absent,
/// so credentials never go on the wire in a recoverable form.
/// </para>
/// <para>
/// SEC6: the SHA-256 of the staged copy is computed <em>on the target</em> and compared with
/// the source before this method reports success. Hashing it locally would only prove the
/// source file is what we already read; hashing it remotely proves what is actually on the
/// domain controller's disk is what will be installed.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinRmSmbTransport : ITargetTransport
{
    private readonly TransportOptions _options;
    private readonly ILogger<WinRmSmbTransport> _log;

    public WinRmSmbTransport(TransportOptions options, ILogger<WinRmSmbTransport>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullLogger<WinRmSmbTransport>.Instance;
    }

    /// <summary>
    /// PRD 5.4 step 1: confirm the DC resolves, is reachable on TCP 445 and the WinRM port,
    /// and that its administrative share is accessible.
    /// </summary>
    /// <remarks>
    /// Each check fails with its own category and its own next step, because "pre-flight
    /// failed" tells an operator holding sixty DCs nothing (PRD 10.4). Reachability is
    /// checked per target as it is reached rather than for all targets up front: probing
    /// sixty DCs serially before starting would be slow, and the answer can change before the
    /// target is reached (PRD 11).
    /// </remarks>
    public async Task<PreflightResult> PreflightAsync(string targetHost, CancellationToken ct)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(targetHost, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return PreflightResult.Failed(
                    ErrorCategory.Connectivity,
                    $"{targetHost} did not resolve to any address. Confirm the name is correct " +
                    "and that this workstation uses a DNS server authoritative for the domain.");
            }
        }
        catch (SocketException ex)
        {
            return PreflightResult.Failed(
                ErrorCategory.Connectivity,
                $"{targetHost} did not resolve in DNS ({ex.SocketErrorCode}). Confirm the name " +
                "is spelled correctly and that this workstation can reach a DNS server " +
                "authoritative for the domain.");
        }

        if (!await CanConnectAsync(targetHost, 445, ct).ConfigureAwait(false))
        {
            return PreflightResult.Failed(
                ErrorCategory.Connectivity,
                $"{targetHost} is not reachable on TCP 445 (SMB), which is needed to stage the " +
                "installer. Confirm the host is running and that no firewall between this " +
                "workstation and the domain controller is blocking SMB.");
        }

        var winRmPort = _options.EffectivePort;
        if (!await CanConnectAsync(targetHost, winRmPort, ct).ConfigureAwait(false))
        {
            return PreflightResult.Failed(
                ErrorCategory.Connectivity,
                $"{targetHost} is not reachable on TCP {winRmPort} (WinRM), which is needed to " +
                "run the installer. WinRM is enabled by default on Windows Server 2012 and " +
                $"later; confirm the WinRM service is running and listening on {winRmPort}, and " +
                "that no firewall is blocking it.");
        }

        // The share is probed rather than assumed: reaching 445 proves SMB answers, not that
        // this account may write to C$.
        try
        {
            using var connection = SmbConnection.Connect(targetHost, _options.Credential);

            var stagingRoot = TransportOptions.ToAdminSharePath(targetHost, _options.StagingRoot);
            var systemTemp = Path.GetDirectoryName(stagingRoot)!;

            if (!Directory.Exists(systemTemp))
            {
                return PreflightResult.Failed(
                    ErrorCategory.Staging,
                    $"{systemTemp} is not accessible on {targetHost}. Confirm the C$ " +
                    "administrative share is enabled and that the running account holds local " +
                    "administrator rights on this domain controller.");
            }
        }
        catch (SmbConnectionException ex)
        {
            return PreflightResult.Failed(
                IsAuthenticationFailure(ex) ? ErrorCategory.Authentication : ErrorCategory.Staging,
                ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return PreflightResult.Failed(
                ErrorCategory.Authentication,
                $"Access was denied to {TransportOptions.AdminShare(targetHost)}. Confirm the " +
                "running account holds local administrator rights on this domain controller.");
        }
        catch (IOException ex)
        {
            return PreflightResult.Failed(
                ErrorCategory.Connectivity,
                $"{TransportOptions.AdminShare(targetHost)} could not be reached: {ex.Message}");
        }

        return PreflightResult.Success();
    }

    /// <summary>
    /// PRD 5.4 step 2: create the staging directory over SMB, copy the MSI, and verify its
    /// SHA-256 on the target.
    /// </summary>
    public async Task<StagingResult> StageFileAsync(
        string targetHost,
        string localPath,
        string remoteDirectory,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(localPath);
        var stagedLocalPath = Path.Combine(remoteDirectory, fileName);

        try
        {
            using var connection = SmbConnection.Connect(targetHost, _options.Credential);

            var uncDirectory = TransportOptions.ToAdminSharePath(targetHost, remoteDirectory);
            var uncFile = Path.Combine(uncDirectory, fileName);

            Directory.CreateDirectory(uncDirectory);

            // Streamed rather than File.Copy so cancellation is honoured part way through a
            // 67 MB transfer instead of after it.
            await using (var source = new FileStream(
                localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            await using (var destination = new FileStream(
                uncFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            _log.LogDebug("Staged {File} to {Path} on {Dc}.", fileName, stagedLocalPath, targetHost);

            // SEC6: hashed on the target, in a session on the target, so what is verified is
            // the bytes that msiexec will read rather than the bytes we sent.
            var stagedHash = await ComputeRemoteHashAsync(targetHost, stagedLocalPath, ct)
                .ConfigureAwait(false);

            return StagingResult.Success(stagedLocalPath, stagedHash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SmbConnectionException ex)
        {
            return StagingResult.Failed(
                IsAuthenticationFailure(ex) ? ErrorCategory.Authentication : ErrorCategory.Staging,
                ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return StagingResult.Failed(
                ErrorCategory.Staging,
                $"Staging failed on {targetHost}: access denied writing to " +
                $"{TransportOptions.ToAdminSharePath(targetHost, remoteDirectory)} — confirm the " +
                "running account holds local administrator rights on this DC.");
        }
        catch (IOException ex)
        {
            return StagingResult.Failed(
                ErrorCategory.Staging,
                $"Staging failed on {targetHost}: {ex.Message} Confirm the domain controller has " +
                "free space on its system drive and that SMB is not being interrupted.");
        }
        catch (RuntimeException ex)
        {
            return StagingResult.Failed(
                ErrorCategory.Connectivity,
                $"The staged file on {targetHost} could not be verified because a WinRM session " +
                $"could not be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// PRD 5.4 step 3: run the command on the target and capture its process exit code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command line is built here rather than in the remote script, and the arguments
    /// reach the session as <em>parameters</em> — never interpolated into script text, so no
    /// PowerShell parser ever reads operator input. Inside the session the process is started
    /// through <c>System.Diagnostics.Process</c> with <c>UseShellExecute = false</c>, so no
    /// shell reads it either.
    /// </para>
    /// <para>
    /// <c>ProcessStartInfo.ArgumentList</c> would be preferable but does not exist: remote
    /// sessions are Windows PowerShell 5.1 on .NET Framework 4.x on every supported domain
    /// controller OS. See <see cref="ArgumentQuoting"/> for why that is a smaller loss than it
    /// appears.
    /// </para>
    /// <para>
    /// The exit code comes from the process, not from the remoting call succeeding. A
    /// successful session that ran a failing msiexec is a failed deployment.
    /// </para>
    /// </remarks>
    public async Task<ExecutionResult> ExecuteAsync(
        string targetHost,
        RemoteCommand command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        const string Script = """
            param([string]$FileName, [string]$Arguments, [int]$TimeoutSeconds)

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $FileName
            $psi.Arguments = $Arguments
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true

            $process = [System.Diagnostics.Process]::Start($psi)

            # Read both streams before waiting: a child that fills a redirected pipe blocks
            # forever if nobody is draining it.
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()

            if ($process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.WaitForExit()
                [pscustomobject]@{
                    TimedOut       = $false
                    ExitCode       = $process.ExitCode
                    StandardOutput = $stdout.Result
                    StandardError  = $stderr.Result
                }
            } else {
                try { $process.Kill() } catch { }
                [pscustomobject]@{
                    TimedOut       = $true
                    ExitCode       = $null
                    StandardOutput = ''
                    StandardError  = ''
                }
            }
            """;

        try
        {
            using var runspace = CreateRunspace(targetHost);
            await OpenAsync(runspace, ct).ConfigureAwait(false);

            using var shell = PowerShell.Create();
            shell.Runspace = runspace;
            shell.AddScript(Script)
                .AddParameter("FileName", command.Executable)
                .AddParameter("Arguments", ArgumentQuoting.Join(command.Arguments))
                .AddParameter("TimeoutSeconds", (int)Math.Ceiling(timeout.TotalSeconds));

            var results = await shell.InvokeAsync().WaitAsync(ct).ConfigureAwait(false);

            if (shell.HadErrors && results.Count == 0)
            {
                var detail = string.Join("; ", shell.Streams.Error.Select(e => e.ToString()));
                return ExecutionResult.FailedToLaunch(
                    ErrorCategory.InstallFailure,
                    $"msiexec could not be started on {targetHost}: {detail}");
            }

            var record = results.FirstOrDefault();
            if (record is null)
            {
                return ExecutionResult.FailedToLaunch(
                    ErrorCategory.Internal,
                    $"The installer on {targetHost} returned no result. This is unexpected; the " +
                    "run log holds the session detail.");
            }

            if (GetProperty<bool>(record, "TimedOut"))
            {
                return ExecutionResult.Timeout(
                    $"msiexec on {targetHost} did not return within {timeout.TotalMinutes:0} " +
                    "minutes and was terminated. Cleanup was still attempted. Confirm the domain " +
                    "controller is responsive, then consider raising the per-target timeout.");
            }

            var exitCode = GetProperty<int>(record, "ExitCode");

            return ExecutionResult.Completed(
                exitCode,
                GetProperty<string>(record, "StandardOutput"),
                GetProperty<string>(record, "StandardError"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PSRemotingTransportException ex)
        {
            return ExecutionResult.FailedToLaunch(
                CategoriseRemotingFailure(ex),
                DescribeRemotingFailure(targetHost, ex));
        }
        catch (RuntimeException ex)
        {
            return ExecutionResult.FailedToLaunch(
                ErrorCategory.Connectivity,
                $"A PowerShell session to {targetHost} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// PRD 5.4 step 4: copy a file back from the target over SMB.
    /// </summary>
    /// <remarks>
    /// Called whether the install succeeded or failed. The verbose log is most valuable on
    /// failure, which is exactly when it is most tempting to skip retrieving it.
    /// </remarks>
    public async Task<RetrievalResult> RetrieveFileAsync(
        string targetHost,
        string remotePath,
        string localDestination,
        CancellationToken ct)
    {
        try
        {
            using var connection = SmbConnection.Connect(targetHost, _options.Credential);

            var uncPath = TransportOptions.ToAdminSharePath(targetHost, remotePath);

            if (!File.Exists(uncPath))
            {
                return RetrievalResult.Failed(
                    ErrorCategory.Staging,
                    $"The installer log '{remotePath}' was not found on {targetHost}. msiexec may " +
                    "have failed before it could create one — the exit code is the better " +
                    "diagnostic in that case.");
            }

            var directory = Path.GetDirectoryName(localDestination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var source = new FileStream(
                uncPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, useAsync: true))
            await using (var destination = new FileStream(
                localDestination, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            return RetrievalResult.Success(localDestination);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SmbConnectionException ex)
        {
            return RetrievalResult.Failed(ErrorCategory.Staging, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RetrievalResult.Failed(
                ErrorCategory.Staging,
                $"The installer log could not be retrieved from {targetHost}: {ex.Message}");
        }
    }

    /// <summary>
    /// PRD 5.4 step 5 and SEC8: remove the staging directory from the target.
    /// </summary>
    /// <remarks>
    /// Throws on failure so the orchestrator can record a warning against the target and leave
    /// the path marked as an orphan (NFR7). It does not fail the deployment — the agent is
    /// installed either way.
    /// </remarks>
    public async Task CleanupAsync(string targetHost, string remoteDirectory, CancellationToken ct)
    {
        using var connection = SmbConnection.Connect(targetHost, _options.Credential);

        var uncDirectory = TransportOptions.ToAdminSharePath(targetHost, remoteDirectory);

        if (!Directory.Exists(uncDirectory))
        {
            return;
        }

        // Removal over SMB is not the simple operation it looks like. Deleting the contents
        // and deleting the directory are separate server-side operations, and the second can
        // fail while the first is still settling or while any handle remains open on the
        // path — msiexec releasing its log, an administrator with the folder open, or the
        // client's own connection. Windows surfaces that as a sharing violation, which
        // arrives as UnauthorizedAccessException rather than IOException; catching only the
        // latter meant a single transient conflict abandoned the directory on a domain
        // controller. Observed in the lab, which is the only place it shows up.
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }

            try
            {
                // Files first, clearing any read-only attribute, then the directory itself.
                // Directory.Delete(recursive) does both, but doing them separately means a
                // failure on the directory does not leave the payload behind: an MSI removed
                // from a DC with an empty folder left over is a far better outcome than the
                // reverse (SEC8).
                foreach (var file in Directory.EnumerateFiles(uncDirectory, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if (info.IsReadOnly)
                    {
                        info.IsReadOnly = false;
                    }

                    info.Delete();
                }

                Directory.Delete(uncDirectory, recursive: true);

                if (!Directory.Exists(uncDirectory))
                {
                    return;
                }

                lastFailure = new IOException(
                    "The directory still exists after being deleted, which usually means the " +
                    "removal is pending on a handle that has not yet closed.");
            }
            catch (DirectoryNotFoundException)
            {
                // Someone else removed it, or a previous attempt succeeded after reporting a
                // conflict. Either way the requirement is met.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }
        }

        throw new IOException(
            $"The staging directory '{remoteDirectory}' on {targetHost} could not be removed " +
            $"after four attempts: {lastFailure?.Message} Confirm no process on that domain " +
            "controller holds a handle to it. The path is recorded in the inventory database " +
            "and can be cleaned up later.",
            lastFailure);
    }

    /// <summary>
    /// Hashes the staged file on the target (SEC6).
    /// </summary>
    /// <remarks>
    /// <c>Get-FileHash</c> is not used: it is unavailable on the oldest supported DC OS and
    /// its output shape has varied. The .NET call below behaves identically everywhere Windows
    /// PowerShell runs. The hash is computed on the target's own disk rather than read back
    /// over SMB, which would re-transfer 67 MB and only prove the wire was consistent.
    /// </remarks>
    private async Task<string> ComputeRemoteHashAsync(string targetHost, string remotePath, CancellationToken ct)
    {
        const string Script = """
            param([string]$Path)

            $stream = [System.IO.File]::OpenRead($Path)
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    ([System.BitConverter]::ToString($sha.ComputeHash($stream))) -replace '-',''
                } finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
            """;

        using var runspace = CreateRunspace(targetHost);
        await OpenAsync(runspace, ct).ConfigureAwait(false);

        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.AddScript(Script).AddParameter("Path", remotePath);

        var results = await shell.InvokeAsync().WaitAsync(ct).ConfigureAwait(false);
        var hash = results.FirstOrDefault()?.BaseObject as string;

        if (string.IsNullOrWhiteSpace(hash))
        {
            var detail = string.Join("; ", shell.Streams.Error.Select(e => e.ToString()));
            throw new IOException(
                $"The SHA-256 of the staged file could not be computed on {targetHost}: {detail}");
        }

        return hash.Trim().ToUpperInvariant();
    }

    /// <summary>Computes the SHA-256 of a local file, in the same format as the remote hash.</summary>
    public static async Task<string> ComputeLocalHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private Runspace CreateRunspace(string targetHost)
    {
        var connectionInfo = new WSManConnectionInfo
        {
            ComputerName = targetHost,
            Port = _options.EffectivePort,
            Scheme = _options.UseHttps ? WSManConnectionInfo.HttpsScheme : WSManConnectionInfo.HttpScheme,

            // SEC3 and SEC4. Both branches below are Kerberos/Negotiate; neither is Basic and
            // neither is CredSSP, and there is deliberately no option to select anything else.
            //
            // The split is not cosmetic. Naming Negotiate explicitly while supplying no
            // credential makes the WinRM client refuse the connection outright — "Default
            // credentials with Negotiate over HTTP can be used only if the target machine is
            // part of the TrustedHosts list" — because SPNEGO with implicit credentials is
            // gated behind client configuration. Requiring a customer to add domain
            // controllers to TrustedHosts on a privileged workstation is a configuration
            // change this tool exists to avoid, so the integrated path asks for Kerberos
            // directly instead. Targets are FQDNs read from Active Directory, so their SPNs
            // resolve; Kerberos also means no NTLM fallback when talking to Tier 0.
            AuthenticationMechanism = _options.Credential is null
                ? AuthenticationMechanism.Kerberos
                : AuthenticationMechanism.Negotiate,

            OpenTimeout = (int)_options.SessionOpenTimeout.TotalMilliseconds,
        };

        if (_options.Credential is { } credential)
        {
            connectionInfo.Credential = credential.UseSecurePassword(
                secure => new PSCredential(credential.AccountName, secure));
        }

        return RunspaceFactory.CreateRunspace(connectionInfo);
    }

    private static async Task OpenAsync(Runspace runspace, CancellationToken ct)
    {
        // Runspace.Open blocks; the async overload does not honour cancellation, so it is
        // pushed to the thread pool to keep the I/O path non-blocking (NFR2).
        await Task.Run(runspace.Open, ct).ConfigureAwait(false);
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsAuthenticationFailure(SmbConnectionException ex) =>
        ex.WindowsErrorCode is 1326 or 5 or 1219;

    private static ErrorCategory CategoriseRemotingFailure(PSRemotingTransportException ex) =>
        // WinRM reports an access-denied on the endpoint the same way whether the account
        // lacks rights or the credentials were wrong; both are operator-side, and both should
        // count toward the circuit breaker.
        ex.ErrorCode is 5 or 1326 or unchecked((int)0x8009030E)
            ? ErrorCategory.Authentication
            : ErrorCategory.Connectivity;

    private static string DescribeRemotingFailure(string targetHost, PSRemotingTransportException ex) =>
        CategoriseRemotingFailure(ex) == ErrorCategory.Authentication
            ? $"WinRM on {targetHost} rejected the connection: {ex.Message} Confirm the account " +
              "holds local administrator rights on this domain controller and is a member of " +
              "the Remote Management Users group or equivalent."
            : $"A WinRM session to {targetHost} could not be opened: {ex.Message} Confirm the " +
              "WinRM service is running and that the host's WinRM listener accepts connections " +
              $"on port {ex.Data["Port"] ?? "the configured port"}.";

    private static T? GetProperty<T>(PSObject record, string name)
    {
        var value = record.Properties[name]?.Value;
        return value is null ? default : (T)Convert.ChangeType(value, typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
