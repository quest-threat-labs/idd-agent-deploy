using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;
using Microsoft.Extensions.Time.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// A migrated inventory, a <see cref="SimulatedTransport"/>, and an orchestrator wired
/// together, with the seeded DC rows the orchestrator's foreign keys require.
/// </summary>
/// <remarks>
/// Everything here runs against the simulated transport. Per the Phase 2 acceptance
/// criteria, the orchestrator has never touched a real domain controller and does not know
/// whether one exists.
/// </remarks>
internal sealed class OrchestratorHarness : IAsyncDisposable
{
    private readonly TemporaryInventory _inventory;
    private readonly string _logDirectory;

    private OrchestratorHarness(TemporaryInventory inventory, string logDirectory)
    {
        _inventory = inventory;
        _logDirectory = logDirectory;
        Transport = new SimulatedTransport();
        Orchestrator = new DeploymentOrchestrator(Transport, inventory.Deployments);
    }

    public SimulatedTransport Transport { get; private set; }

    public DeploymentOrchestrator Orchestrator { get; private set; }

    public TemporaryInventory Inventory => _inventory;

    public string LogDirectory => _logDirectory;

    public static async Task<OrchestratorHarness> CreateAsync()
    {
        var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);
        var logDirectory = Path.Combine(
            Path.GetTempPath(), "had-tests", "logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        return new OrchestratorHarness(inventory, logDirectory);
    }

    /// <summary>
    /// Rebuilds the orchestrator on a fake clock, so the 1618 backoff and the per-target
    /// timeout can be asserted without a test waiting minutes for them.
    /// </summary>
    public FakeTimeProvider UseFakeClock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T10:00:00Z"));
        Orchestrator = new DeploymentOrchestrator(Transport, _inventory.Deployments, null, clock);
        return clock;
    }

    /// <summary>Seeds DC rows and returns them as deployment targets.</summary>
    public async Task<IReadOnlyList<DeploymentTarget>> SeedTargetsAsync(
        params (string Fqdn, string? Site)[] hosts)
    {
        var targets = new List<DeploymentTarget>(hosts.Length);

        foreach (var (fqdn, site) in hosts)
        {
            var id = await _inventory.DomainControllers.UpsertAsync(
                new DomainControllerRecord
                {
                    Fqdn = fqdn,
                    SiteName = site,
                    Source = DiscoverySource.AdEnumeration,
                },
                CancellationToken.None);

            targets.Add(new DeploymentTarget(id, fqdn, site));
        }

        return targets;
    }

    /// <summary>Seeds hosts in a single named site, the common case for these tests.</summary>
    public Task<IReadOnlyList<DeploymentTarget>> SeedTargetsAsync(string? site, int count) =>
        SeedTargetsAsync(
            [.. Enumerable.Range(1, count).Select(i => ($"dc{i:00}.corp.local", site))]);

    public DeploymentRequest Request(
        IReadOnlyList<DeploymentTarget> targets,
        int maxParallel = DeploymentLimits.MaxConcurrencyCeiling,
        int timeoutMinutes = DeploymentLimits.DefaultTimeoutMinutes,
        bool cloudMode = true,
        string orgId = "org-abc-123",
        IReadOnlyDictionary<string, int>? activeDcsPerSite = null) =>
        DeploymentRequest.Create(
            msi: TestMsi,
            orgId: orgId,
            targets: targets,
            operatorAccount: @"CORP\admin",
            logDirectory: _logDirectory,
            cloudMode: cloudMode,
            maxParallel: maxParallel,
            timeoutMinutes: timeoutMinutes,
            activeDcsPerSite: activeDcsPerSite);

    /// <summary>
    /// The SHA-256 the simulated transport reports for a staged file by default, so staging
    /// verification (SEC6) passes unless a test deliberately makes it fail.
    /// </summary>
    public const string StagedSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    public static MsiPackageInfo TestMsi { get; } = new()
    {
        FilePath = @"C:\packages\agent.msi",
        FileName = "agent.msi",
        FileSizeBytes = 42_000_000,
        Sha256 = StagedSha256,
        ProductName = "Quest Change Auditor 7.7.0 Agent",
        ProductVersion = "7.7.34005.0",
        ProductCode = "{0F4BE828-CE3B-4DC1-9C3A-714952CE6A0A}",
        UpgradeCode = "{1E1215C8-39D0-4B21-B1B1-75FF307FB47D}",
        LooksLikeExpectedProduct = true,
    };

    public string ReadRunLog() => File.ReadAllText(Path.Combine(_logDirectory, "run.log"));

    public string ReadResultsCsv() => File.ReadAllText(Path.Combine(_logDirectory, "results.csv"));

    public async ValueTask DisposeAsync()
    {
        await _inventory.DisposeAsync();

        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
