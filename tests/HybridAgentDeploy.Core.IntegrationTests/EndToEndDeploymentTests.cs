using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;

namespace HybridAgentDeploy.Core.IntegrationTests;

/// <summary>
/// The Phase 3 acceptance criterion: a deployment to a single lab DC completes, the agent is
/// installed, the verbose log is retrieved locally, and the staging directory is gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test installs software on a domain controller.</b> It is the only test in the
/// repository that does, and it is gated on <c>HAD_ALLOW_INSTALL=yes</c> in addition to every
/// other variable, so neither a default run nor a normal lab run can trigger it by accident.
/// Anyone enabling it is stating that the named DC may have the agent installed on it.
/// </para>
/// <para>
/// Required environment:
/// <c>HAD_ALLOW_INSTALL=yes</c>, <c>HAD_TEST_DC</c>, <c>HAD_TEST_MSI_PATH</c>,
/// <c>HAD_TEST_ORG_ID</c>. The Org ID is read from the environment rather than committed —
/// it identifies a real tenant and does not belong in source control.
/// </para>
/// <para>
/// It runs the whole stack: <see cref="DeploymentOrchestrator"/> driving the real
/// <see cref="WinRmSmbTransport"/>, writing to a real inventory database and a real run log.
/// Nothing is simulated.
/// </para>
/// </remarks>
public sealed class EndToEndDeploymentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_deployment_to_one_domain_controller_installs_the_agent_and_cleans_up()
    {
        var allow = Environment.GetEnvironmentVariable("HAD_ALLOW_INSTALL");
        Skip.If(
            !string.Equals(allow, "yes", StringComparison.OrdinalIgnoreCase),
            "This test INSTALLS THE AGENT on a domain controller. Set HAD_ALLOW_INSTALL=yes, " +
            "along with HAD_TEST_DC, HAD_TEST_MSI_PATH and HAD_TEST_ORG_ID, only against a lab " +
            "DC that may have software installed on it.");

        var target = Require("HAD_TEST_DC");
        var msiPath = Require("HAD_TEST_MSI_PATH");
        var orgId = Require("HAD_TEST_ORG_ID");

        var workspace = Path.Combine(Path.GetTempPath(), "had-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            // A real inventory database, migrated from empty exactly as a first run would.
            var connections = new SqliteConnectionFactory(Path.Combine(workspace, "inventory.db"), pooling: false);
            await new SchemaMigrator(connections).MigrateAsync(Ct);

            var domainControllers = new DomainControllerRepository(connections);
            var deployments = new DeploymentRepository(connections);

            var dcId = await domainControllers.UpsertAsync(
                new DomainControllerRecord
                {
                    Fqdn = target,
                    Source = DiscoverySource.AdEnumeration,
                    SiteName = "lab",
                },
                Ct);

            var msi = await new MsiInspector().InspectAsync(msiPath, Ct);

            var validation = OrgId.Validate(orgId);
            Assert.True(validation.IsValid, validation.Error);

            var logDirectory = Path.Combine(workspace, "logs");
            Directory.CreateDirectory(logDirectory);

            var request = DeploymentRequest.Create(
                msi: msi,
                orgId: validation.Value!,
                targets: [new DeploymentTarget(dcId, target, "lab")],
                operatorAccount: OperatorCredential.CurrentWindowsAccountName(),
                logDirectory: logDirectory,
                cloudMode: true,
                maxParallel: 1,
                timeoutMinutes: 30,
                activeDcsPerSite: new Dictionary<string, int> { ["lab"] = 1 });

            var orchestrator = new DeploymentOrchestrator(
                new WinRmSmbTransport(new TransportOptions()), deployments);

            var summary = await orchestrator.DeployAsync(request, null, Ct);

            var outcome = Assert.Single(summary.Outcomes);

            // The agent installed. 3010 is equally a success (PRD 10.1) — this agent does not
            // require a reboot, so a pending one came from something else on the host.
            Assert.True(
                outcome.IsSuccess,
                $"Deployment failed: outcome={outcome.Outcome}, exit={outcome.ExitCode}, " +
                $"category={outcome.ErrorCategory}, detail={outcome.ErrorDetail}");

            Assert.Contains(outcome.ExitCode, new int?[] { 0, 3010 });
            Assert.Equal(1, summary.SuccessCount);
            Assert.False(summary.WasHalted);

            // The verbose log came back, and is a real msiexec log rather than an empty file.
            Assert.NotNull(outcome.MsiLogPath);
            Assert.True(File.Exists(outcome.MsiLogPath), $"No log at {outcome.MsiLogPath}.");
            Assert.True(new FileInfo(outcome.MsiLogPath!).Length > 0, "The retrieved log is empty.");

            // The staging directory is gone from the domain controller (SEC8), verified
            // against the DC rather than trusted from the result.
            var staging = Assert.Single(await deployments.GetResultsForRunAsync(summary.RunId, Ct)).StagingPath;
            Assert.NotNull(staging);
            Assert.False(
                Directory.Exists(TransportOptions.ToAdminSharePath(target, staging!)),
                $"The staging directory '{staging}' was left on {target}.");

            // NFR7: nothing is left recorded as an orphan.
            Assert.Empty(await deployments.GetOrphanedStagingDirectoriesAsync(Ct));

            // The run log records what ran, for the audit trail (PRD 12.2).
            var runLog = await File.ReadAllTextAsync(Path.Combine(logDirectory, "run.log"), Ct);
            Assert.Contains("INSTALLATION_NAME=", runLog, StringComparison.Ordinal);
            Assert.Contains("SG=1", runLog, StringComparison.Ordinal);
        }
        finally
        {
            // Nothing to release: the factory above does not pool, so closing a connection
            // frees the file. ClearAllPools would be process-wide and is not this test's to
            // call.
        }
    }

    private static string Require(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable)?.Trim().Trim('"');
        Skip.If(string.IsNullOrWhiteSpace(value), $"Set {variable} to run this test.");
        return value!;
    }
}
