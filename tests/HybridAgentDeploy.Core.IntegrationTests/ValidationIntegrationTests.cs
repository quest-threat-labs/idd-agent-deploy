using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;

namespace HybridAgentDeploy.Core.IntegrationTests;

/// <summary>
/// Validation against a real domain controller (PRD 11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here installs anything.</b> That is the point of the feature: this is the whole
/// per-target sequence a deployment would run, minus the one step that changes the machine.
/// The only thing written to the target is a 4 KB file of random bytes, which is read back and
/// then deleted.
/// </para>
/// <para>
/// Requires a lab forest and an account with local administrator rights on the target. Reads
/// <c>HAD_TEST_DC</c> and <c>HAD_TEST_MSI_PATH</c>, and skips with a message naming them when
/// either is unset.
/// </para>
/// </remarks>
public sealed class ValidationIntegrationTests
{
    private const string TargetVariable = "HAD_TEST_DC";
    private const string MsiVariable = "HAD_TEST_MSI_PATH";

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// The complete checklist against a live domain controller.
    /// </summary>
    /// <remarks>
    /// Asserted here rather than only against the simulator because the three checks are about
    /// things a simulator cannot have an opinion on: whether the account can actually write to
    /// <c>C$\Windows\Temp</c>, whether WinRM will actually open a session for it, and whether
    /// the registry query returns what the parser expects on a real Windows install.
    /// </remarks>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_real_domain_controller_is_validated_without_installing_anything()
    {
        var target = RequireTarget();
        var msi = await RequireMsiAsync();

        var transport = new WinRmSmbTransport(new TransportOptions());
        var directory = Path.Combine(Path.GetTempPath(), $"had-validate-itest-{Guid.NewGuid():N}");

        try
        {
            var request = ValidationRequest.Create(
                msi,
                [new DeploymentTarget(1, target, "IntegrationTest")],
                operatorAccount: OperatorCredential.CurrentWindowsAccountName(),
                logDirectory: directory);

            var summary = await new ValidationRunner(transport).ValidateAsync(request, null, Ct);

            var outcome = summary.Outcomes.Single();

            Assert.True(outcome.Passed, string.Join(
                Environment.NewLine,
                outcome.Failures.Select(f => $"{f.Name}: {f.Detail}")));

            Assert.Contains(outcome.Checks, c => c.Name == "Reachable" && c.Passed);
            Assert.Contains(outcome.Checks, c => c.Name == "Can stage files" && c.Passed);
            Assert.Contains(outcome.Checks, c => c.Name == "Can run commands" && c.Passed);
            Assert.Contains(outcome.Checks, c => c.Name == "Installed agent");
            Assert.Contains(outcome.Checks, c => c.Name == "Connection mode");

            // Whatever the DC has, the prediction must be a real one rather than a shrug.
            Assert.NotEqual(PredictedAction.Unknown, outcome.Predicted);

            // Read live from the target rather than from the inventory. Every supported DC is
            // Server 2016 or later, so this also confirms the build number parsed.
            Assert.NotNull(outcome.OperatingSystem);
            Assert.True(
                outcome.OperatingSystem!.IsSupported,
                $"{target} reports {outcome.OperatingSystem.Describe()}, below the agent's minimum.");

            var log = await File.ReadAllTextAsync(Path.Combine(directory, "validation.log"), Ct);
            Assert.Contains("NOTHING WAS INSTALLED BY THIS RUN", log, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// The connection mode is read from a real domain controller, and running the same target
    /// under both settings reports a change under exactly one of them — the correct one for
    /// the mode that target is actually in.
    /// </summary>
    /// <remarks>
    /// The point of the test is that the answer flips, and flips the right way. Asserting a
    /// fixed outcome would only hold against a lab whose agents happen to be installed the way
    /// the test was written, and would pass just as happily against a validator returning a
    /// constant.
    /// </remarks>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task The_connection_mode_is_read_from_the_target_and_drives_the_right_verdict()
    {
        var target = RequireTarget();
        var msi = await RequireMsiAsync();

        var cloud = await ValidateOnceAsync(target, msi, cloudMode: true);
        var onPremises = await ValidateOnceAsync(target, msi, cloudMode: false);

        Skip.If(
            cloud.InstalledAgent?.Mode is null,
            $"{target} has no agent installed, or its connection mode could not be read, so " +
            "there is no mode to change from. Install an agent on it to run this test.");

        // The installed mode does not change between the two runs; the requested one does.
        Assert.Equal(cloud.InstalledAgent!.Mode, onPremises.InstalledAgent!.Mode);

        if (cloud.InstalledAgent.Mode == AgentMode.ChangeAuditor)
        {
            // Cloud mode migrates it; on-premises mode leaves it exactly where it is.
            Assert.Equal(ModeChange.MigratesToCloud, cloud.ModeChange);
            Assert.Equal(ModeChange.None, onPremises.ModeChange);
            Assert.Contains("MIGRATES", cloud.Summary, StringComparison.Ordinal);
        }
        else
        {
            // Already on the cloud product: cloud mode is a no-op, and the reverse is refused.
            Assert.Equal(ModeChange.None, cloud.ModeChange);
            Assert.Equal(ModeChange.NotSupported, onPremises.ModeChange);
            Assert.Contains("does not move back", onPremises.Summary, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// SEC8: nothing this tool writes to a domain controller stays there.
    /// </summary>
    /// <remarks>
    /// Checked over the admin share directly rather than trusting the run to report its own
    /// cleanup. A green result that left a directory behind on a Tier 0 host is the failure
    /// mode this assertion exists for, and it is one an in-process assertion cannot see.
    /// </remarks>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task The_probe_directory_does_not_survive_the_run()
    {
        var target = RequireTarget();
        var msi = await RequireMsiAsync();

        var transport = new WinRmSmbTransport(new TransportOptions());
        var directory = Path.Combine(Path.GetTempPath(), $"had-validate-itest-{Guid.NewGuid():N}");

        try
        {
            var request = ValidationRequest.Create(
                msi,
                [new DeploymentTarget(1, target, "IntegrationTest")],
                operatorAccount: OperatorCredential.CurrentWindowsAccountName(),
                logDirectory: directory);

            var summary = await new ValidationRunner(transport).ValidateAsync(request, null, Ct);

            var probeRoot = TransportOptions.ToAdminSharePath(target, StagingPaths.Root);
            var leftovers = Directory.Exists(probeRoot)
                ? Directory.GetDirectories(probeRoot, $"validate-{summary.RunGuid:D}")
                : [];

            Assert.Empty(leftovers);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>Validates one target once, in the given mode, and cleans up its log directory.</summary>
    private static async Task<ValidationOutcome> ValidateOnceAsync(
        string target,
        MsiPackageInfo msi,
        bool cloudMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"had-validate-itest-{Guid.NewGuid():N}");

        try
        {
            var request = ValidationRequest.Create(
                msi,
                [new DeploymentTarget(1, target, "IntegrationTest")],
                operatorAccount: OperatorCredential.CurrentWindowsAccountName(),
                logDirectory: directory,
                cloudMode: cloudMode);

            var summary = await new ValidationRunner(new WinRmSmbTransport(new TransportOptions()))
                .ValidateAsync(request, null, Ct);

            return summary.Outcomes.Single();
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string RequireTarget()
    {
        var target = Trimmed(Environment.GetEnvironmentVariable(TargetVariable));
        Skip.If(
            target is null,
            $"Set {TargetVariable} to the FQDN of a lab domain controller to run this test. " +
            "The account running the tests needs local administrator rights on it. Nothing is " +
            "installed; the test writes a small file and removes it.");
        return target!;
    }

    private static async Task<MsiPackageInfo> RequireMsiAsync()
    {
        var path = Trimmed(Environment.GetEnvironmentVariable(MsiVariable));
        Skip.If(
            path is null || !File.Exists(path),
            $"Set {MsiVariable} to the path of an agent MSI to run this test. Validation " +
            "compares the installed agent against it; the file is read locally and is never " +
            "copied to the domain controller.");

        return await new MsiInspector().InspectAsync(path!, Ct);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
