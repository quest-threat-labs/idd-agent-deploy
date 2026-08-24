using HybridAgentDeploy.Core;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: repository round-trips and the <c>dc_last_deployment</c> view against seeded
/// data.
/// </summary>
public sealed class RepositoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_domain_controller_round_trips_with_every_field_intact()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var id = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord
            {
                Fqdn = "DC01.corp.local",
                NetbiosName = "DC01",
                Domain = "corp.local",
                SiteName = "London",
                OsVersion = "Windows Server 2022 Standard",
                IsReadOnly = true,
                IsGlobalCatalog = true,
                Source = DiscoverySource.AdEnumeration,
            },
            Ct);

        var loaded = await inventory.DomainControllers.GetByIdAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal("DC01.corp.local", loaded.Fqdn);
        Assert.Equal("DC01", loaded.NetbiosName);
        Assert.Equal("corp.local", loaded.Domain);
        Assert.Equal("London", loaded.SiteName);
        Assert.Equal("Windows Server 2022 Standard", loaded.OsVersion);
        Assert.True(loaded.IsReadOnly);
        Assert.True(loaded.IsGlobalCatalog);
        Assert.Equal(DiscoverySource.AdEnumeration, loaded.Source);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task A_domain_controller_is_looked_up_case_insensitively()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual },
            Ct);

        Assert.NotNull(await inventory.DomainControllers.GetByFqdnAsync("dc01.CORP.local", Ct));
    }

    /// <summary>
    /// PRD 6.2: re-importing a known host updates last_seen_utc and does not duplicate the
    /// row.
    /// </summary>
    [Fact]
    public async Task Re_upserting_a_known_host_updates_it_rather_than_duplicating_it()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var first = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord
            {
                Fqdn = "DC01.corp.local",
                SiteName = "London",
                Source = DiscoverySource.FileImport,
                FirstSeenUtc = "2026-01-01T00:00:00.0000000Z",
                LastSeenUtc = "2026-01-01T00:00:00.0000000Z",
            },
            Ct);

        var second = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord
            {
                Fqdn = "dc01.CORP.LOCAL",
                SiteName = "Manchester",
                Source = DiscoverySource.AdEnumeration,
                FirstSeenUtc = "2026-06-01T00:00:00.0000000Z",
                LastSeenUtc = "2026-06-01T00:00:00.0000000Z",
            },
            Ct);

        Assert.Equal(first, second);

        var all = await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct);
        Assert.Single(all);

        var loaded = all[0];
        Assert.Equal("2026-06-01T00:00:00.0000000Z", loaded.LastSeenUtc);
        Assert.Equal("Manchester", loaded.SiteName);

        // How a DC first entered the inventory is a fact about the past; re-seeing it in a
        // different way does not rewrite that.
        Assert.Equal("2026-01-01T00:00:00.0000000Z", loaded.FirstSeenUtc);
        Assert.Equal(DiscoverySource.FileImport, loaded.Source);
    }

    /// <summary>
    /// PRD 6.2: a DC that disappears from AD is deactivated, not deleted — its row, tags,
    /// and history survive.
    /// </summary>
    [Fact]
    public async Task A_controller_absent_from_enumeration_is_deactivated_not_deleted()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.AdEnumeration },
            Ct);
        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC02.corp.local", Source = DiscoverySource.AdEnumeration },
            Ct);

        var deactivated = await inventory.DomainControllers.DeactivateEnumeratedExceptAsync(
            ["DC01.corp.local"],
            Ct);

        Assert.Equal(["DC02.corp.local"], deactivated);

        var active = await inventory.DomainControllers.GetAllAsync(includeInactive: false, Ct);
        Assert.Single(active);
        Assert.Equal("DC01.corp.local", active[0].Fqdn);

        var all = await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct);
        Assert.Equal(2, all.Count);
    }

    /// <summary>
    /// Deactivation is scoped to AD-sourced rows. An imported host is not in AD by
    /// definition, and deactivating it because an enumeration did not mention it would
    /// silently empty an operator's hand-built target list.
    /// </summary>
    [Fact]
    public async Task Enumeration_does_not_deactivate_imported_hosts()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC09.corp.local", Source = DiscoverySource.FileImport },
            Ct);

        var deactivated = await inventory.DomainControllers.DeactivateEnumeratedExceptAsync([], Ct);

        Assert.Empty(deactivated);
        Assert.Single(await inventory.DomainControllers.GetAllAsync(includeInactive: false, Ct));
    }

    [Fact]
    public async Task A_tag_round_trips_and_is_matched_case_insensitively()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var created = await inventory.Tags.GetOrCreateAsync("Pilot", "First wave", Ct);
        var again = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);

        Assert.Equal(created.Id, again.Id);
        Assert.Single(await inventory.Tags.GetAllAsync(Ct));
        Assert.Equal("First wave", again.Description);
    }

    [Fact]
    public async Task Tags_apply_to_many_controllers_and_are_queryable_by_name()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc1 = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);
        var dc2 = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC02.corp.local", Source = DiscoverySource.Manual }, Ct);
        var dc3 = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC03.corp.local", Source = DiscoverySource.Manual }, Ct);

        var pilot = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);
        await inventory.Tags.ApplyTagAsync(pilot.Id, [dc1, dc2], Ct);

        // Re-applying an existing association is a no-op, not an error: import with --tag
        // is expected to be run repeatedly.
        await inventory.Tags.ApplyTagAsync(pilot.Id, [dc1], Ct);

        var tagged = await inventory.Tags.GetDcIdsWithTagAsync("PILOT", Ct);

        Assert.Equal(2, tagged.Count);
        Assert.Contains(dc1, tagged);
        Assert.Contains(dc2, tagged);
        Assert.DoesNotContain(dc3, tagged);
    }

    [Fact]
    public async Task A_controller_can_carry_several_tags()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);

        foreach (var name in new[] { "pilot", "london", "rodc" })
        {
            var tag = await inventory.Tags.GetOrCreateAsync(name, null, Ct);
            await inventory.Tags.ApplyTagAsync(tag.Id, [dc], Ct);
        }

        var byDc = await inventory.Tags.GetTagsByDcAsync(Ct);

        Assert.Equal(["london", "pilot", "rodc"], byDc[dc]);
    }

    /// <summary>
    /// PRD 8.2: deleting a tag removes the association only. It never deletes DC rows or
    /// history — a tag is a saved selection, not an owner of the hosts in it.
    /// </summary>
    [Fact]
    public async Task Deleting_a_tag_leaves_the_controllers_and_their_history_alone()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);
        var tag = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);
        await inventory.Tags.ApplyTagAsync(tag.Id, [dc], Ct);

        var runId = await SeedRunAsync(inventory, "1.2.3");
        await SeedResultAsync(inventory, runId, dc, DeploymentOutcome.Success, "2026-03-01T10:00:00.0000000Z");

        await inventory.Tags.DeleteAsync(tag.Id, Ct);

        Assert.Empty(await inventory.Tags.GetAllAsync(Ct));
        Assert.NotNull(await inventory.DomainControllers.GetByIdAsync(dc, Ct));
        Assert.Single(await inventory.Deployments.GetResultsForRunAsync(runId, Ct));
    }

    [Fact]
    public async Task A_deployment_run_and_its_results_round_trip()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);

        var run = new DeploymentRunRecord
        {
            RunGuid = Guid.NewGuid(),
            OperatorAccount = @"CORP\admin",
            MsiPath = @"C:\packages\agent.msi",
            MsiFileName = "agent.msi",
            MsiSha256 = new string('A', 64),
            MsiProductVersion = "7.4.0.1234",
            MsiProductName = "Quest Change Auditor Agent",
            MsiProductCode = "{11111111-2222-3333-4444-555555555555}",
            OrgId = "org-abc-123",
            CloudMode = true,
            MaxParallel = 5,
            TargetCount = 1,
            LogDirectory = @"C:\logs\20260301-100000-abcd1234",
        };

        var runId = await inventory.Deployments.CreateRunAsync(run, Ct);

        var result = new DeploymentResultRecord
        {
            RunId = runId,
            DcId = dc,
            StartedUtc = "2026-03-01T10:00:00.0000000Z",
            CompletedUtc = "2026-03-01T10:04:00.0000000Z",
            Outcome = DeploymentOutcome.SuccessRebootRequired,
            Stage = DeploymentStage.Execute,
            ExitCode = 3010,
            AttemptCount = 1,
            MsiLogPath = @"C:\logs\DC01.corp.local_install.log",
        };
        await inventory.Deployments.CreateResultAsync(result, Ct);

        run.SuccessCount = 1;
        run.CompletedUtc = "2026-03-01T10:04:00.0000000Z";
        await inventory.Deployments.CompleteRunAsync(run, Ct);

        var runs = await inventory.Deployments.GetRunsAsync(10, Ct);
        Assert.Single(runs);
        Assert.Equal(run.RunGuid, runs[0].RunGuid);
        Assert.Equal("org-abc-123", runs[0].OrgId);
        Assert.True(runs[0].CloudMode);
        Assert.Equal(1, runs[0].SuccessCount);

        var results = await inventory.Deployments.GetResultsForRunAsync(runId, Ct);
        Assert.Single(results);
        Assert.Equal(DeploymentOutcome.SuccessRebootRequired, results[0].Outcome);
        Assert.Equal(DeploymentStage.Execute, results[0].Stage);
        Assert.Equal(3010, results[0].ExitCode);
    }

    /// <summary>
    /// PRD R7.1: no path may raise concurrency above the ceiling. Storage is the last of
    /// those paths — history that claims a run used eight slots would be a lie about what
    /// happened on the customer's domain controllers.
    /// </summary>
    [Fact]
    public async Task A_run_recorded_above_the_concurrency_ceiling_is_clamped_in_storage()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var runId = await inventory.Deployments.CreateRunAsync(
            NewRun(maxParallel: 50),
            Ct);

        var runs = await inventory.Deployments.GetRunsAsync(10, Ct);

        Assert.Equal(
            HybridAgentDeploy.Core.Deployment.DeploymentLimits.MaxConcurrencyCeiling,
            runs.Single(r => r.Id == runId).MaxParallel);
    }

    /// <summary>
    /// PRD 6.1: the view returns the most recent completed deployment for each DC.
    /// </summary>
    [Fact]
    public async Task The_last_deployment_view_returns_the_most_recent_result_per_controller()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc1 = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);
        var dc2 = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC02.corp.local", Source = DiscoverySource.Manual }, Ct);

        var older = await SeedRunAsync(inventory, "7.3.0.1");
        await SeedResultAsync(inventory, older, dc1, DeploymentOutcome.Success, "2026-01-01T09:00:00.0000000Z");
        await SeedResultAsync(inventory, older, dc2, DeploymentOutcome.Success, "2026-01-01T09:00:00.0000000Z");

        var newer = await SeedRunAsync(inventory, "7.4.0.2");
        await SeedResultAsync(inventory, newer, dc1, DeploymentOutcome.Failure, "2026-06-01T09:00:00.0000000Z");

        var last = await inventory.DomainControllers.GetLastDeploymentsAsync(Ct);

        Assert.Equal(2, last.Count);

        Assert.Equal("7.4.0.2", last[dc1].MsiProductVersion);
        Assert.Equal(DeploymentOutcome.Failure, last[dc1].Outcome);

        // DC02 was not in the second run, so its last known state is still the first.
        Assert.Equal("7.3.0.1", last[dc2].MsiProductVersion);
        Assert.Equal(DeploymentOutcome.Success, last[dc2].Outcome);
    }

    /// <summary>
    /// The correction to the PRD 6.1 view. Two results for one DC sharing a completed_utc —
    /// a redeploy inside the same second, or a coarse clock — must still yield exactly one
    /// row, or a grid billed as "last known state per DC" shows the same DC twice with
    /// conflicting answers.
    /// </summary>
    [Fact]
    public async Task The_last_deployment_view_returns_one_row_per_controller_even_on_a_timestamp_tie()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);

        const string SameInstant = "2026-04-01T12:00:00.0000000Z";

        var first = await SeedRunAsync(inventory, "7.3.0.1");
        await SeedResultAsync(inventory, first, dc, DeploymentOutcome.Failure, SameInstant);

        var second = await SeedRunAsync(inventory, "7.4.0.2");
        await SeedResultAsync(inventory, second, dc, DeploymentOutcome.Success, SameInstant);

        var last = await inventory.DomainControllers.GetLastDeploymentsAsync(Ct);

        Assert.Single(last);

        // The tie breaks toward the later row, which is the one written second.
        Assert.Equal("7.4.0.2", last[dc].MsiProductVersion);
        Assert.Equal(DeploymentOutcome.Success, last[dc].Outcome);
    }

    /// <summary>
    /// A run still in flight has no completed_utc. It must not displace the last completed
    /// deployment, or starting a run would blank the inventory grid it is being launched
    /// from.
    /// </summary>
    [Fact]
    public async Task An_incomplete_result_does_not_appear_as_the_last_deployment()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);

        var completed = await SeedRunAsync(inventory, "7.3.0.1");
        await SeedResultAsync(inventory, completed, dc, DeploymentOutcome.Success, "2026-01-01T09:00:00.0000000Z");

        var inFlight = await SeedRunAsync(inventory, "7.4.0.2");
        await inventory.Deployments.CreateResultAsync(
            new DeploymentResultRecord
            {
                RunId = inFlight,
                DcId = dc,
                StartedUtc = "2026-06-01T09:00:00.0000000Z",
                CompletedUtc = null,
                Outcome = DeploymentOutcome.Skipped,
                Stage = DeploymentStage.Execute,
            },
            Ct);

        var last = await inventory.DomainControllers.GetLastDeploymentsAsync(Ct);

        Assert.Equal("7.3.0.1", last[dc].MsiProductVersion);
    }

    [Fact]
    public async Task A_controller_never_deployed_to_is_absent_from_the_view()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);

        Assert.DoesNotContain(dc, (await inventory.DomainControllers.GetLastDeploymentsAsync(Ct)).Keys);
    }

    /// <summary>
    /// NFR7: an interrupted run must leave no staging directory the tool cannot subsequently
    /// identify. This is the query that makes that recoverable.
    /// </summary>
    [Fact]
    public async Task A_staging_directory_recorded_but_never_cleaned_is_reported_as_an_orphan()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var dc = await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord { Fqdn = "DC01.corp.local", Source = DiscoverySource.Manual }, Ct);
        var runId = await SeedRunAsync(inventory, "7.4.0.2");

        var result = new DeploymentResultRecord
        {
            RunId = runId,
            DcId = dc,
            Outcome = DeploymentOutcome.Skipped,
            Stage = DeploymentStage.Stage,
        };
        var resultId = await inventory.Deployments.CreateResultAsync(result, Ct);

        const string StagingPath = @"C:\Windows\Temp\HybridAgentDeploy\abcd1234";
        await inventory.Deployments.RecordStagingPathAsync(resultId, StagingPath, Ct);

        var orphans = await inventory.Deployments.GetOrphanedStagingDirectoriesAsync(Ct);
        var orphan = Assert.Single(orphans);
        Assert.Equal("DC01.corp.local", orphan.TargetFqdn);
        Assert.Equal(StagingPath, orphan.StagingPath);

        await inventory.Deployments.MarkStagingCleanedAsync(resultId, Ct);

        Assert.Empty(await inventory.Deployments.GetOrphanedStagingDirectoriesAsync(Ct));
    }

    private static DeploymentRunRecord NewRun(string productVersion = "7.4.0.2", int maxParallel = 5) => new()
    {
        RunGuid = Guid.NewGuid(),
        OperatorAccount = @"CORP\admin",
        MsiPath = @"C:\packages\agent.msi",
        MsiFileName = "agent.msi",
        MsiSha256 = new string('A', 64),
        MsiProductVersion = productVersion,
        OrgId = "org-abc-123",
        MaxParallel = maxParallel,
        TargetCount = 1,
        LogDirectory = @"C:\logs\run",
    };

    private static Task<long> SeedRunAsync(TemporaryInventory inventory, string productVersion) =>
        inventory.Deployments.CreateRunAsync(NewRun(productVersion), Ct);

    private static Task<long> SeedResultAsync(
        TemporaryInventory inventory,
        long runId,
        long dcId,
        DeploymentOutcome outcome,
        string completedUtc) =>
        inventory.Deployments.CreateResultAsync(
            new DeploymentResultRecord
            {
                RunId = runId,
                DcId = dcId,
                StartedUtc = completedUtc,
                CompletedUtc = completedUtc,
                Outcome = outcome,
                Stage = DeploymentStage.Cleanup,
                ExitCode = outcome == DeploymentOutcome.Success ? 0 : 1603,
            },
            Ct);
}
