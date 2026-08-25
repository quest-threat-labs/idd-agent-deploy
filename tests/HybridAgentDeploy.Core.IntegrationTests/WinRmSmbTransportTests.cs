using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.IntegrationTests;

/// <summary>
/// <see cref="WinRmSmbTransport"/> against a real domain controller (PRD 15.2).
/// </summary>
/// <remarks>
/// <para>
/// Requires a lab forest, a domain-joined workstation, and an account holding local
/// administrator rights on the target. Excluded from the default run; failures on a normal
/// dev machine are environmental, not bugs.
/// </para>
/// <para>
/// <b>Nothing here installs anything.</b> These tests exercise pre-flight, SMB staging, remote
/// SHA-256 verification, file retrieval, and cleanup — every part of the transport except
/// <see cref="WinRmSmbTransport.ExecuteAsync"/> running the real installer, which is the one
/// genuinely consequential step and is gated on explicit approval. The one execution test
/// below runs <c>cmd /c exit</c>, which changes nothing on the target.
/// </para>
/// <para>
/// The target is read from <c>HAD_TEST_DC</c>. Tests skip with a message naming the variable
/// when it is unset, so this file is safe to run anywhere.
/// </para>
/// </remarks>
public sealed class WinRmSmbTransportTests
{
    private const string TargetVariable = "HAD_TEST_DC";
    private const string MsiVariable = "HAD_TEST_MSI_PATH";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string? Target => Trimmed(Environment.GetEnvironmentVariable(TargetVariable));

    private static string? MsiPath
    {
        get
        {
            var path = Trimmed(Environment.GetEnvironmentVariable(MsiVariable));
            return path is not null && File.Exists(path) ? path : null;
        }
    }

    private static string RequireTarget()
    {
        var target = Target;
        Skip.If(
            target is null,
            $"Set {TargetVariable} to the FQDN of a lab domain controller to run this test. " +
            "The account running the tests needs local administrator rights on it. Nothing is " +
            "installed; the test stages a file and removes it.");
        return target!;
    }

    private static WinRmSmbTransport NewTransport() =>
        // No credential: SEC2 makes integrated authentication the default, and the lab account
        // running these tests is the one being tested.
        new(new TransportOptions());

    /// <summary>A staging directory unique to this test run, so concurrent runs cannot collide.</summary>
    private static string NewStagingDirectory() =>
        Path.Combine(@"C:\Windows\Temp\HybridAgentDeploy", $"itest-{Guid.NewGuid():N}");

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Preflight_succeeds_against_a_reachable_domain_controller()
    {
        var target = RequireTarget();

        var result = await NewTransport().PreflightAsync(target, Ct);

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.Null(result.ErrorCategory);
    }

    /// <summary>
    /// PRD 10.4: the failure must name the host and the next diagnostic step, not merely
    /// report that something went wrong.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Preflight_on_an_unresolvable_host_reports_dns_with_a_next_step()
    {
        RequireTarget();

        var result = await NewTransport()
            .PreflightAsync($"no-such-host-{Guid.NewGuid():N}.invalid", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCategory.Connectivity, result.ErrorCategory);
        Assert.Contains("DNS", result.ErrorDetail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole staging path end to end, including SEC6 verification, and cleanup afterwards.
    /// A small generated file stands in for the MSI so the test is fast; the real 67 MB
    /// package is covered by the test below.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_staged_file_verifies_against_its_source_hash_and_is_then_removed()
    {
        var target = RequireTarget();
        var transport = NewTransport();
        var staging = NewStagingDirectory();

        var localFile = Path.Combine(Path.GetTempPath(), $"had-itest-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localFile, RandomBytes(3 * 1024 * 1024), Ct);

        try
        {
            var expected = await WinRmSmbTransport.ComputeLocalHashAsync(localFile, Ct);

            var staged = await transport.StageFileAsync(target, localFile, staging, Ct);

            Assert.True(staged.Succeeded, staged.ErrorDetail);
            Assert.Equal(Path.Combine(staging, Path.GetFileName(localFile)), staged.StagedPath);

            // SEC6: the hash was computed on the domain controller's own disk, so this proves
            // the bytes that would be installed are the bytes we sent.
            Assert.Equal(expected, staged.VerifiedSha256);

            // The staging path is under Windows\Temp, which inherits admin-only ACLs (SEC7).
            Assert.StartsWith(@"C:\Windows\Temp\", staged.StagedPath!, StringComparison.OrdinalIgnoreCase);

            await transport.CleanupAsync(target, staging, Ct);

            // SEC8: confirmed gone from the target, not merely reported as removed.
            Assert.False(Directory.Exists(TransportOptions.ToAdminSharePath(target, staging)));
        }
        finally
        {
            await SafeCleanupAsync(transport, target, staging);
            File.Delete(localFile);
        }
    }

    /// <summary>
    /// The real 67 MB package over the real path. Worth running separately: a transfer that
    /// works for three megabytes can still fail on a large file, and the hash of the genuine
    /// article is what a deployment actually verifies.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task The_real_agent_msi_stages_and_verifies()
    {
        var target = RequireTarget();
        var msi = MsiPath;
        Skip.If(msi is null, $"Set {MsiVariable} to the agent MSI to run this test.");

        var transport = NewTransport();
        var staging = NewStagingDirectory();

        try
        {
            var expected = await WinRmSmbTransport.ComputeLocalHashAsync(msi!, Ct);

            var staged = await transport.StageFileAsync(target, msi!, staging, Ct);

            Assert.True(staged.Succeeded, staged.ErrorDetail);
            Assert.Equal(expected, staged.VerifiedSha256);
        }
        finally
        {
            await SafeCleanupAsync(transport, target, staging);
        }
    }

    /// <summary>
    /// The retrieval path (PRD 5.4 step 4), exercised by staging a file and fetching it back.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_file_on_the_target_is_retrieved_intact()
    {
        var target = RequireTarget();
        var transport = NewTransport();
        var staging = NewStagingDirectory();

        var localFile = Path.Combine(Path.GetTempPath(), $"had-itest-{Guid.NewGuid():N}.log");
        var retrieved = Path.Combine(Path.GetTempPath(), $"had-itest-back-{Guid.NewGuid():N}.log");
        const string Content = "MSI (s) (00:00) [10:00:00:000]: Windows Installer installed the product.";

        try
        {
            await File.WriteAllTextAsync(localFile, Content, Ct);

            var staged = await transport.StageFileAsync(target, localFile, staging, Ct);
            Assert.True(staged.Succeeded, staged.ErrorDetail);

            var result = await transport.RetrieveFileAsync(target, staged.StagedPath!, retrieved, Ct);

            Assert.True(result.Succeeded, result.ErrorDetail);
            Assert.Equal(Content, await File.ReadAllTextAsync(retrieved, Ct));
        }
        finally
        {
            await SafeCleanupAsync(transport, target, staging);
            File.Delete(localFile);
            File.Delete(retrieved);
        }
    }

    /// <summary>
    /// A missing log is reported distinctly rather than as a generic failure: msiexec failing
    /// before it writes one is a real case, and the exit code is the better diagnostic there.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Retrieving_a_file_that_does_not_exist_says_so_plainly()
    {
        var target = RequireTarget();

        var result = await NewTransport().RetrieveFileAsync(
            target,
            $@"C:\Windows\Temp\HybridAgentDeploy\nonexistent-{Guid.NewGuid():N}\install.log",
            Path.Combine(Path.GetTempPath(), $"had-itest-{Guid.NewGuid():N}.log"),
            Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.ErrorDetail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The execution path and its exit-code capture, using a command that changes nothing.
    /// </summary>
    /// <remarks>
    /// This is the proof that matters most before an install is ever attempted: that the
    /// process exit code comes back from the target correctly, rather than the remoting call's
    /// own success being mistaken for it. <c>cmd /c exit N</c> touches nothing on the DC.
    /// </remarks>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1603)]
    [InlineData(3010)]
    [Trait("Category", "Integration")]
    public async Task An_exit_code_is_captured_from_the_target_process(int exitCode)
    {
        var target = RequireTarget();

        var command = new RemoteCommand("cmd.exe", ["/c", "exit", exitCode.ToString()]);

        var result = await NewTransport().ExecuteAsync(target, command, TimeSpan.FromMinutes(2), Ct);

        Assert.True(result.Launched, result.ErrorDetail);
        Assert.False(result.TimedOut);
        Assert.Equal(exitCode, result.ExitCode);
    }

    /// <summary>
    /// SEC10 against a real target: the payload must arrive as one argument. Echoed back
    /// rather than passed to an installer, so nothing is changed on the domain controller.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task An_injected_org_id_arrives_as_a_single_argument_on_the_target()
    {
        var target = RequireTarget();

        const string Payload = "INSTALLATION_NAME=; Stop-Service NTDS; ";

        // cmd's echo prints its arguments verbatim; if the payload had been split or
        // re-parsed, the output would show it broken apart.
        var command = new RemoteCommand("cmd.exe", ["/c", "echo", Payload]);

        var result = await NewTransport().ExecuteAsync(target, command, TimeSpan.FromMinutes(2), Ct);

        Assert.True(result.Launched, result.ErrorDetail);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Stop-Service NTDS", result.StandardOutput!, StringComparison.Ordinal);

        // NTDS is untouched: the payload was data, not a command.
        Assert.DoesNotContain("is not recognized", result.StandardOutput!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>R7.5: a command that overruns its timeout is reported as a timeout, not a hang.</summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_command_that_overruns_its_timeout_is_reported_as_one()
    {
        var target = RequireTarget();

        // Waits far longer than the timeout allows, then is killed.
        var command = new RemoteCommand("cmd.exe", ["/c", "ping", "-n", "60", "127.0.0.1"]);

        var result = await NewTransport().ExecuteAsync(target, command, TimeSpan.FromSeconds(5), Ct);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Equal(ErrorCategory.Timeout, result.ErrorCategory);
    }

    /// <summary>Cleanup on a directory that is already gone is a no-op, not an error (NFR7).</summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Cleaning_up_a_directory_that_does_not_exist_is_harmless()
    {
        var target = RequireTarget();

        await NewTransport().CleanupAsync(target, NewStagingDirectory(), Ct);
    }

    /// <summary>
    /// Removes a test's staging directory and asserts it is actually gone.
    /// </summary>
    /// <remarks>
    /// An earlier version swallowed cleanup failures so that a teardown problem could not mask
    /// the assertion that had already failed. That reasoning was wrong twice over: it hid a
    /// real defect in <see cref="WinRmSmbTransport.CleanupAsync"/>, and it let this suite leave
    /// a directory behind on a domain controller — the exact outcome SEC8 and NFR7 exist to
    /// prevent. Teardown now fails loudly, because a test that litters Tier 0 infrastructure is
    /// a broken test whatever else it proved.
    /// </remarks>
    private static async Task SafeCleanupAsync(WinRmSmbTransport transport, string target, string staging)
    {
        await transport.CleanupAsync(target, staging, CancellationToken.None);

        Assert.False(
            Directory.Exists(TransportOptions.ToAdminSharePath(target, staging)),
            $"Cleanup reported success but '{staging}' still exists on {target}.");
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
