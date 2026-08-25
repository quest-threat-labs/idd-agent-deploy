using System.Security.Cryptography;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Confirms one domain controller is ready to receive the agent, without installing it
/// (PRD 11).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately exercises the same path a deployment takes — pre-flight, stage, execute,
/// clean up — rather than a cheaper approximation of it. Reaching TCP 5985 proves a port is
/// open, not that the account can open a session; a readable C$ proves nothing about writing
/// to it. Both pass happily on a domain controller that then fails at staging, which is the
/// failure this exists to prevent.
/// </para>
/// <para>
/// What it does not do is run msiexec. The probe written to the target is a few bytes and is
/// removed again, and the command executed is one that does nothing.
/// </para>
/// </remarks>
public sealed class TargetValidator
{
    /// <summary>
    /// Reads what the target already has: the installed agent, its connection mode, and the
    /// operating system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One round trip for three facts, because each is a registry read and opening three
    /// sessions to a domain controller to learn them separately would be three times the cost
    /// for no gain.
    /// </para>
    /// <para>
    /// Deliberately contains no operator input — it is a constant authored here — so passing it
    /// to a shell raises none of the concerns SEC10 exists for. Written without double quotes
    /// so that quoting it as a single argument cannot alter it.
    /// </para>
    /// <para>
    /// Both registry views are read for the agent: a 32-bit agent on a 64-bit controller lives
    /// under WOW6432Node, and missing it would report "no agent" on a host that has one. The
    /// connection mode is read only from the <c>Quest</c> path, which is the single path the
    /// installer itself reads it from.
    /// </para>
    /// <para>
    /// Each fact is emitted on its own labelled line rather than as one delimited record, so a
    /// remoting session that prepends a warning or a banner cannot shift the fields.
    /// </para>
    /// </remarks>
    private const string TargetStateScript =
        "$ErrorActionPreference='SilentlyContinue'; " +
        "$k=@('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'," +
        "'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'); " +
        "$p=Get-ItemProperty $k | Where-Object { $_.DisplayName -like '*Change Auditor*Agent*' } | " +
        "Select-Object -First 1; " +
        "if ($p) { 'AGENT|' + $p.DisplayName + '|' + $p.DisplayVersion + '|' + $p.PSChildName } " +
        "else { 'AGENT|none' }; " +
        "$a=Get-ItemProperty 'HKLM:\\SOFTWARE\\Quest\\ChangeAuditor\\Agent'; " +
        "if ($a -ne $null -and $a.PSObject.Properties.Name -contains 'SgConnectionMode') " +
        "{ 'MODE|' + $a.SgConnectionMode } else { 'MODE|none' }; " +
        "$o=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion'; " +
        "'OS|' + $o.CurrentBuildNumber + '|' + $o.ProductName";

    private readonly ITargetTransport _transport;

    public TargetValidator(ITargetTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Validates one target.</summary>
    /// <param name="msi">The package the installed agent is compared against.</param>
    /// <param name="requestedMode">
    /// The product this run would configure the agent for, from the cloud-mode setting. Used
    /// to detect the mode change the installer refuses.
    /// </param>
    /// <param name="stagingDirectory">Where the probe is written, and removed from.</param>
    public async Task<ValidationOutcome> ValidateAsync(
        DeploymentTarget target,
        MsiPackageInfo msi,
        AgentMode requestedMode,
        string stagingDirectory,
        TimeSpan timeout,
        DateTimeOffset startedUtc,
        CancellationToken ct)
    {
        var checks = new List<ValidationCheck>();
        ErrorCategory? category = null;
        InstalledAgent? installed = null;
        TargetOperatingSystem? operatingSystem = null;

        // Set before the copy is attempted, not after it succeeds. A copy that failed
        // part-way still leaves something behind, and that is precisely the case where
        // skipping cleanup would leave it there (SEC8).
        var stagingAttempted = false;

        try
        {
            // 1. Reachability, exactly as a deployment's first step (PRD 5.4 step 1).
            var preflight = await _transport.PreflightAsync(target.Fqdn, ct).ConfigureAwait(false);
            checks.Add(new ValidationCheck(
                "Reachable",
                preflight.Succeeded,
                preflight.Succeeded
                    ? "DNS resolves, and TCP 445 and WinRM are answering"
                    : preflight.ErrorDetail ?? "The target could not be reached."));

            if (!preflight.Succeeded)
            {
                category = preflight.ErrorCategory;
                return Finish(target, checks, installed, operatingSystem, requestedMode, msi, category, startedUtc);
            }

            // 2. Write access, proved by writing. A readable share says nothing about this, and
            //    it is the most common reason a deployment dies at the staging step.
            var probe = await CreateProbeAsync(ct).ConfigureAwait(false);
            try
            {
                stagingAttempted = true;

                var stageResult = await _transport
                    .StageFileAsync(target.Fqdn, probe.Path, stagingDirectory, ct)
                    .ConfigureAwait(false);

                var hashMatches = stageResult.Succeeded &&
                    string.Equals(stageResult.VerifiedSha256, probe.Sha256, StringComparison.OrdinalIgnoreCase);

                checks.Add(new ValidationCheck(
                    "Can stage files",
                    hashMatches,
                    hashMatches
                        ? $"wrote and verified a test file in {stagingDirectory}"
                        : stageResult.ErrorDetail ??
                          "A test file was written but its hash did not match, so staging cannot " +
                          "be trusted on this host."));

                if (!hashMatches)
                {
                    category = stageResult.ErrorCategory ?? Models.ErrorCategory.Staging;
                    return Finish(target, checks, installed, operatingSystem, requestedMode, msi, category, startedUtc);
                }
            }
            finally
            {
                probe.Delete();
            }

            // 3. A session can be opened and a process started. Reaching the port does not
            //    prove the account may do either.
            var execution = await _transport
                .ExecuteAsync(target.Fqdn, new RemoteCommand("cmd.exe", ["/c", "exit", "0"]), timeout, ct)
                .ConfigureAwait(false);

            var canExecute = execution.Launched && execution.ExitCode == 0;

            checks.Add(new ValidationCheck(
                "Can run commands",
                canExecute,
                canExecute
                    ? "opened a remoting session and ran a command that changed nothing"
                    : execution.ErrorDetail ??
                      $"A command was started but returned {execution.ExitCode}, which is " +
                      "unexpected for a command that does nothing."));

            if (!canExecute)
            {
                category = execution.ErrorCategory ?? Models.ErrorCategory.Connectivity;
                return Finish(target, checks, installed, operatingSystem, requestedMode, msi, category, startedUtc);
            }

            // 4. What the target already has, so the operator learns before the run whether
            //    this is an install, an upgrade, a reinstall, or something the installer will
            //    refuse — and whether the host can run the agent at all.
            var state = await ReadTargetStateAsync(target, timeout, ct).ConfigureAwait(false);
            installed = state.Agent;
            operatingSystem = state.OperatingSystem;

            // The MSI refuses anything below Server 2016 through a launch condition, so this is
            // a property of the host rather than of the package: no version of this agent can
            // be installed here. Reported as a failure for that reason.
            checks.Add(new ValidationCheck(
                "Supported operating system",
                operatingSystem?.IsSupported ?? true,
                operatingSystem is null
                    ? "the operating system version could not be read; the installer will check " +
                      "it itself and refuse anything below Windows Server 2016"
                    : operatingSystem.IsSupported
                        ? operatingSystem.Describe()
                        : $"{target.Fqdn} runs {operatingSystem.Describe()}, and the agent " +
                          "requires Windows Server 2016 or later. The installer refuses older " +
                          "versions outright, so this domain controller cannot receive this " +
                          "package until it is upgraded."));

            checks.Add(new ValidationCheck(
                "Installed agent",
                true,
                installed is null
                    ? "no Change Auditor agent is installed"
                    : $"{installed.DisplayName} {installed.Version}"));

            // Reported whether or not it matches: an operator confirming a run against sixty
            // controllers wants to see which product each one currently reports to, not only
            // the ones that disagree.
            checks.Add(new ValidationCheck(
                "Connection mode",
                true,
                installed is null
                    ? $"nothing installed; this run would configure the agent for " +
                      $"{ValidationOutcome.Describe(requestedMode)}"
                    : installed.Mode is { } mode
                        ? $"installed for {ValidationOutcome.Describe(mode)}; this run would " +
                          $"use {ValidationOutcome.Describe(requestedMode)}"
                        : "an agent is installed but its connection mode could not be read, so " +
                          "whether this run would change modes cannot be determined"));

            return Finish(target, checks, installed, operatingSystem, requestedMode, msi, category, startedUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            checks.Add(new ValidationCheck(
                "Validation",
                false,
                $"Validation of {target.Fqdn} failed unexpectedly: {ex.Message}"));

            return Finish(target, checks, installed, operatingSystem, requestedMode, msi, Models.ErrorCategory.Internal, startedUtc);
        }
        finally
        {
            if (stagingAttempted)
            {
                // The probe is removed on every path, for the same reason a deployment always
                // cleans up: nothing this tool writes to a domain controller stays there. Runs
                // on a fresh token so it is still attempted when the target's own timeout has
                // already fired (SEC8).
                try
                {
                    await _transport.CleanupAsync(target.Fqdn, stagingDirectory, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reported through the checks rather than thrown: a probe left behind is
                    // untidy, not a reason to call the target invalid.
                }
            }
        }
    }

    private async Task<TargetState> ReadTargetStateAsync(
        DeploymentTarget target,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var command = new RemoteCommand(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", TargetStateScript]);

        var result = await _transport.ExecuteAsync(target.Fqdn, command, timeout, ct).ConfigureAwait(false);

        return ParseTargetState(result.StandardOutput);
    }

    /// <summary>What the query script reported about a target.</summary>
    internal sealed record TargetState(InstalledAgent? Agent, TargetOperatingSystem? OperatingSystem);

    /// <summary>Parses the labelled lines the query script emits.</summary>
    /// <remarks>
    /// Each line is looked up by its label rather than by position, so unexpected output — a
    /// profile banner, a module's warning — cannot silently shift a field and turn a
    /// 7.6 agent into a 7.7 one.
    /// </remarks>
    internal static TargetState ParseTargetState(string? output)
    {
        var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        return new TargetState(
            ParseAgent(Field(lines, "AGENT"), Field(lines, "MODE")),
            ParseOperatingSystem(Field(lines, "OS")));
    }

    /// <summary>The last line carrying a label, split into its fields.</summary>
    /// <remarks>
    /// The last rather than the first: if anything upstream echoed the script itself, the real
    /// output is what came after it.
    /// </remarks>
    private static string[]? Field(string[] lines, string label)
    {
        var prefix = label + "|";

        var line = lines.LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

        return line?[prefix.Length..].Split('|');
    }

    private static InstalledAgent? ParseAgent(string[]? agent, string[]? mode)
    {
        if (agent is null || agent.Length < 3 || string.IsNullOrWhiteSpace(agent[1]))
        {
            return null;
        }

        if (agent[0].Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new InstalledAgent(
            agent[0].Trim(), agent[1].Trim(), agent[2].Trim(), ParseMode(mode));
    }

    /// <summary>
    /// Maps <c>SgConnectionMode</c> to a product.
    /// </summary>
    /// <remarks>
    /// Anything other than 0 or 1 is reported as unrecognised rather than folded into either
    /// mode. A wrong answer here would tell the operator a deployment is safe when the
    /// installer is about to refuse it.
    /// </remarks>
    internal static AgentMode? ParseMode(string[]? mode)
    {
        var value = mode?.FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value switch
        {
            "0" => AgentMode.ChangeAuditor,
            "1" => AgentMode.IdentityDefense,
            _ => AgentMode.Unrecognised,
        };
    }

    internal static TargetOperatingSystem? ParseOperatingSystem(string[]? os)
    {
        if (os is null || os.Length == 0 ||
            !int.TryParse(os[0].Trim(), out var build) || build <= 0)
        {
            return null;
        }

        return new TargetOperatingSystem(build, os.Length > 1 ? os[1].Trim() : null);
    }

    /// <summary>
    /// Works out what deploying the package would do to a target in this state.
    /// </summary>
    /// <remarks>
    /// Compares the four-part product versions. Anything that will not parse is reported as
    /// unknown rather than guessed at — a wrong prediction is worse than none, because the
    /// operator would act on it.
    /// </remarks>
    internal static PredictedAction Predict(InstalledAgent? installed, MsiPackageInfo msi)
    {
        ArgumentNullException.ThrowIfNull(msi);

        if (installed is null)
        {
            return PredictedAction.FreshInstall;
        }

        if (!Version.TryParse(installed.Version, out var present) ||
            !Version.TryParse(msi.ProductVersion, out var candidate))
        {
            return PredictedAction.Unknown;
        }

        return candidate.CompareTo(present) switch
        {
            0 => PredictedAction.Reinstall,
            > 0 => PredictedAction.Upgrade,
            _ => PredictedAction.DowngradeBlocked,
        };
    }

    private static ValidationOutcome Finish(
        DeploymentTarget target,
        List<ValidationCheck> checks,
        InstalledAgent? installed,
        TargetOperatingSystem? operatingSystem,
        AgentMode requestedMode,
        MsiPackageInfo msi,
        ErrorCategory? category,
        DateTimeOffset startedUtc) => new()
        {
            Target = target,
            Checks = checks,
            InstalledAgent = installed,
            OperatingSystem = operatingSystem,
            RequestedMode = requestedMode,
            Predicted = Predict(installed, msi),
            ErrorCategory = category,
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// A small local file to copy to the target and read back.
    /// </summary>
    /// <remarks>
    /// Random content, so a stale file from an earlier run cannot make a failed copy look
    /// successful.
    /// </remarks>
    private static async Task<ProbeFile> CreateProbeAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"had-validate-{Guid.NewGuid():N}.probe");
        var content = new byte[4096];
        RandomNumberGenerator.Fill(content);

        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);

        return new ProbeFile(path, Convert.ToHexString(SHA256.HashData(content)));
    }

    private sealed record ProbeFile(string Path, string Sha256)
    {
        public void Delete()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
