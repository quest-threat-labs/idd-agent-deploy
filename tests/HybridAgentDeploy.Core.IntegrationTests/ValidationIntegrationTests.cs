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

            // Whatever the DC has, the prediction must be a real one rather than a shrug.
            Assert.NotEqual(PredictedAction.Unknown, outcome.Predicted);

            var log = await File.ReadAllTextAsync(Path.Combine(directory, "validation.log"), Ct);
            Assert.Contains("NOTHING WAS INSTALLED BY THIS RUN", log, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
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
