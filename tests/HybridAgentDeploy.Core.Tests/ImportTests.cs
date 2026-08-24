using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: import parsing — comments, blanks, duplicates, unmatched hosts, mixed
/// FQDN/NetBIOS.
/// </summary>
/// <remarks>
/// Import is a selection mechanism: it applies tags to domain controllers already discovered
/// from Active Directory and never adds new ones. See the remarks on
/// <see cref="FileImportService"/> for why this departs from the literal wording of PRD 6.2.
/// </remarks>
public sealed class ImportTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Blank_lines_and_comments_are_ignored()
    {
        var result = HostListParser.ParseText(
            """
            # Pilot ring, agreed with the AD team 2026-02-14
            dc01.corp.local

               # indented comment

            dc02.corp.local
            """);

        Assert.Equal(
            ["dc01.corp.local", "dc02.corp.local"],
            result.Entries.Select(e => e.RawValue));
    }

    [Fact]
    public void A_trailing_comment_on_a_host_line_is_stripped()
    {
        var result = HostListParser.ParseText("dc01.corp.local   # decommissioning in March");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("dc01.corp.local", entry.RawValue);
    }

    [Fact]
    public void Line_numbers_survive_blanks_and_comments()
    {
        var result = HostListParser.ParseText(
            """
            # header

            dc01.corp.local
            dc02.corp.local
            """);

        // The operator's editor shows line 3 and line 4; the report must agree with it.
        Assert.Equal([3, 4], result.Entries.Select(e => e.LineNumber));
    }

    [Fact]
    public void Duplicates_are_collapsed_and_reported_rather_than_dropped_silently()
    {
        var result = HostListParser.ParseText(
            """
            dc01.corp.local
            dc02.corp.local
            DC01.CORP.LOCAL
            """);

        Assert.Equal(2, result.Entries.Count);

        var duplicate = Assert.Single(result.Duplicates);
        Assert.Equal(3, duplicate.LineNumber);
        Assert.Equal("DC01.CORP.LOCAL", duplicate.RawValue);
    }

    [Theory]
    [InlineData("dc01.corp.local", HostEntryKind.Fqdn)]
    [InlineData("DC01", HostEntryKind.NetBiosName)]
    [InlineData("10.20.30.40", HostEntryKind.IpAddress)]
    [InlineData("fe80::1", HostEntryKind.IpAddress)]
    public void Entries_are_classified_by_how_they_were_written(string value, HostEntryKind expected)
    {
        var entry = Assert.Single(HostListParser.ParseText(value).Entries);
        Assert.Equal(expected, entry.Kind);
    }

    /// <summary>
    /// The primary use case (PRD 6.2, G3): a text file names a pilot ring, and the tag makes
    /// that same subset re-selectable weeks later for the upgrade.
    /// </summary>
    [Fact]
    public async Task Import_tags_the_controllers_the_file_names()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local", "dc02.corp.local", "dc03.corp.local");

        var service = NewService(inventory, new StubHostResolver());

        var report = await service.ImportAsync(
            ["dc01.corp.local", "dc02.corp.local"], ["pilot"], dryRun: false, Ct);

        Assert.Equal(2, report.Matched.Count);
        Assert.Empty(report.Unmatched);

        var tagged = await inventory.Tags.GetDcIdsWithTagAsync("pilot", Ct);
        Assert.Equal(2, tagged.Count);
    }

    [Fact]
    public async Task Import_applies_every_requested_tag_in_one_operation()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local", "dc02.corp.local");

        var service = NewService(inventory, new StubHostResolver());

        await service.ImportAsync(
            ["dc01.corp.local", "dc02.corp.local"], ["pilot", "london"], dryRun: false, Ct);

        Assert.Equal(2, (await inventory.Tags.GetDcIdsWithTagAsync("pilot", Ct)).Count);
        Assert.Equal(2, (await inventory.Tags.GetDcIdsWithTagAsync("london", Ct)).Count);
    }

    /// <summary>
    /// Matching a name already in the inventory must not depend on a working resolver, so
    /// FQDN and NetBIOS matches are tried before DNS.
    /// </summary>
    [Fact]
    public async Task A_netbios_name_matches_without_consulting_dns()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        // A resolver that fails everything: if this test passes, DNS was not needed.
        var service = NewService(inventory, new StubHostResolver());

        var report = await service.ImportAsync(["DC01"], ["pilot"], dryRun: false, Ct);

        var matched = Assert.Single(report.Matched);
        Assert.Equal("dc01.corp.local", matched.ResolvedFqdn);
    }

    [Fact]
    public async Task An_fqdn_matches_case_insensitively()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        var service = NewService(inventory, new StubHostResolver());

        var report = await service.ImportAsync(["DC01.CORP.LOCAL"], [], dryRun: false, Ct);

        Assert.Single(report.Matched);
        Assert.Empty(report.Unmatched);
    }

    /// <summary>An address is resolved and then matched against the inventory.</summary>
    [Fact]
    public async Task An_ip_address_is_resolved_and_then_matched()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc03.corp.local");

        var resolver = new StubHostResolver().Resolves("10.20.30.40", "dc03.corp.local");
        var service = NewService(inventory, resolver);

        var report = await service.ImportAsync(["10.20.30.40"], ["pilot"], dryRun: false, Ct);

        var matched = Assert.Single(report.Matched);
        Assert.Equal("dc03.corp.local", matched.ResolvedFqdn);
    }

    /// <summary>
    /// The behaviour that replaces host creation. An entry naming something that is not a
    /// known domain controller is reported, with its line number and a next step, and nothing
    /// is added to the inventory.
    /// </summary>
    [Fact]
    public async Task An_entry_that_is_not_a_known_domain_controller_is_reported_and_not_created()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        var resolver = new StubHostResolver().ResolvesToItself("fileserver.corp.local");
        var service = NewService(inventory, resolver);

        var report = await service.ImportAsync(
            ["dc01.corp.local", "fileserver.corp.local"], ["pilot"], dryRun: false, Ct);

        Assert.Single(report.Matched);

        var unmatched = Assert.Single(report.Unmatched);
        Assert.Equal(2, unmatched.LineNumber);
        Assert.Equal("fileserver.corp.local", unmatched.RawValue);
        Assert.Contains("not a domain controller in the inventory", unmatched.Reason, StringComparison.Ordinal);
        Assert.Contains("re-enumerate", unmatched.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.HasProblems);

        // The decisive assertion: the inventory is untouched.
        var stored = await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct);
        Assert.Equal(["dc01.corp.local"], stored.Select(d => d.Fqdn));
    }

    [Fact]
    public async Task An_entry_that_does_not_resolve_is_reported_with_the_dns_failure()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        var resolver = new StubHostResolver()
            .FailsToResolve("dc99.corp.local", "'dc99.corp.local' did not resolve in DNS (HostNotFound).");
        var service = NewService(inventory, resolver);

        var report = await service.ImportAsync(["dc99.corp.local"], [], dryRun: false, Ct);

        var unmatched = Assert.Single(report.Unmatched);
        Assert.Contains("did not resolve in DNS", unmatched.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Import never writes to the domain_controller table, so last_seen_utc keeps meaning
    /// "last seen in Active Directory" rather than "last named in a text file".
    /// </summary>
    [Fact]
    public async Task Import_does_not_modify_the_domain_controller_row()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord
            {
                Fqdn = "dc01.corp.local",
                NetbiosName = "DC01",
                SiteName = "London",
                Source = DiscoverySource.AdEnumeration,
                FirstSeenUtc = "2026-01-01T00:00:00.0000000Z",
                LastSeenUtc = "2026-01-01T00:00:00.0000000Z",
            },
            Ct);

        var service = NewService(inventory, new StubHostResolver());
        await service.ImportAsync(["dc01.corp.local"], ["pilot"], dryRun: false, Ct);

        var stored = await inventory.DomainControllers.GetByFqdnAsync("dc01.corp.local", Ct);

        Assert.NotNull(stored);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", stored.LastSeenUtc);
        Assert.Equal("London", stored.SiteName);
        Assert.Equal(DiscoverySource.AdEnumeration, stored.Source);
    }

    /// <summary>
    /// Re-importing adds tags without duplicating anything (PRD 6.2), which now falls out of
    /// import touching only the association table.
    /// </summary>
    [Fact]
    public async Task Re_importing_adds_tags_without_duplicating_anything()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        var service = NewService(inventory, new StubHostResolver());

        await service.ImportAsync(["dc01.corp.local"], ["pilot"], dryRun: false, Ct);
        await service.ImportAsync(["dc01.corp.local"], ["london"], dryRun: false, Ct);

        Assert.Single(await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct));

        var dcId = (await inventory.DomainControllers.GetByFqdnAsync("dc01.corp.local", Ct))!.Id;
        Assert.Equal(["london", "pilot"], (await inventory.Tags.GetTagsByDcAsync(Ct))[dcId]);
    }

    /// <summary>
    /// Importing against an inventory that has never been enumerated blocks with an
    /// explanation. Reporting every line as unmatched would be accurate and useless.
    /// </summary>
    [Fact]
    public async Task Importing_before_enumerating_explains_what_to_do_first()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var service = NewService(inventory, new StubHostResolver());

        var ex = await Assert.ThrowsAsync<InventoryNotEnumeratedException>(
            () => service.ImportAsync(["dc01.corp.local"], ["pilot"], dryRun: false, Ct));

        Assert.Contains("Active Directory enumeration", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not add new ones", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run must report exactly what a real run would do, or the preview is not worth
    /// having (PRD 9, <c>import --dry-run</c>).
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_the_same_outcome_but_writes_nothing()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedEnumeratedAsync(inventory, "dc01.corp.local");

        var resolver = new StubHostResolver().ResolvesToItself("nope.corp.local");
        var service = NewService(inventory, resolver);

        string[] lines = ["dc01.corp.local", "nope.corp.local"];

        var dry = await service.ImportAsync(lines, ["pilot"], dryRun: true, Ct);

        Assert.True(dry.DryRun);
        Assert.Single(dry.Matched);
        Assert.Single(dry.Unmatched);
        Assert.Equal(["pilot"], dry.TagsApplied);

        // No tag was created, let alone applied.
        Assert.Empty(await inventory.Tags.GetAllAsync(Ct));

        var real = await service.ImportAsync(lines, ["pilot"], dryRun: false, Ct);

        Assert.Equal(dry.Matched.Count, real.Matched.Count);
        Assert.Equal(dry.Unmatched.Count, real.Unmatched.Count);
        Assert.Single(await inventory.Tags.GetAllAsync(Ct));
    }

    [Fact]
    public async Task Importing_a_missing_file_names_the_path_it_could_not_find()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var service = NewService(inventory, new StubHostResolver());

        var missing = Path.Combine(Path.GetTempPath(), $"had-missing-{Guid.NewGuid():N}.txt");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ImportFileAsync(missing, [], dryRun: true, Ct));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    private static FileImportService NewService(TemporaryInventory inventory, IHostResolver resolver) =>
        new(inventory.DomainControllers, inventory.Tags, resolver);

    /// <summary>Seeds DCs as Active Directory enumeration would have.</summary>
    private static async Task SeedEnumeratedAsync(TemporaryInventory inventory, params string[] fqdns)
    {
        foreach (var fqdn in fqdns)
        {
            await inventory.DomainControllers.UpsertAsync(
                new DomainControllerRecord
                {
                    Fqdn = fqdn,
                    NetbiosName = fqdn.Split('.')[0].ToUpperInvariant(),
                    Domain = "corp.local",
                    SiteName = "London",
                    Source = DiscoverySource.AdEnumeration,
                },
                Ct);
        }
    }
}

/// <summary>
/// Inventory staleness. Enumeration is an explicit operator action, so a deployment can run
/// against an older picture of the forest — the tool's job is to make sure nobody does that
/// without knowing.
/// </summary>
public sealed class InventoryStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-01T12:00:00Z");

    [Fact]
    public async Task A_fresh_inventory_reports_never_enumerated()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var status = await inventory.DomainControllers.GetStatusAsync(Ct);

        Assert.True(status.HasNeverEnumerated);
        Assert.True(status.IsEmpty);
        Assert.True(status.IsStale(Now));
        Assert.Contains("never been enumerated", status.StalenessWarning(Now)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_enumeration_time_is_derived_from_the_domain_controller_rows()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        await inventory.DomainControllers.UpsertAsync(
            new DomainControllerRecord
            {
                Fqdn = "dc01.corp.local",
                Source = DiscoverySource.AdEnumeration,
                FirstSeenUtc = "2026-02-20T09:00:00.0000000Z",
                LastSeenUtc = "2026-02-28T09:00:00.0000000Z",
            },
            Ct);

        var status = await inventory.DomainControllers.GetStatusAsync(Ct);

        Assert.Equal(1, status.ActiveDomainControllers);
        Assert.Equal(
            DateTimeOffset.Parse("2026-02-28T09:00:00Z"),
            status.LastEnumerationUtc!.Value);
    }

    [Fact]
    public void A_recent_enumeration_produces_no_warning()
    {
        var status = new InventoryStatus(12, Now.AddDays(-2));

        Assert.False(status.IsStale(Now));
        Assert.Null(status.StalenessWarning(Now));
    }

    /// <summary>
    /// Stale is a warning, never a block. An administrator who knows their forest has not
    /// changed should not be stopped, and a hard expiry would be worked around rather than
    /// heeded.
    /// </summary>
    [Fact]
    public void An_old_enumeration_warns_with_its_age_and_a_next_step()
    {
        var status = new InventoryStatus(12, Now.AddDays(-30));

        Assert.True(status.IsStale(Now));

        var warning = status.StalenessWarning(Now)!;
        Assert.Contains("30 days ago", warning, StringComparison.Ordinal);
        Assert.Contains("promoted, demoted, or moved between sites", warning, StringComparison.Ordinal);
        Assert.Contains("Re-enumerate", warning, StringComparison.Ordinal);
    }
}
