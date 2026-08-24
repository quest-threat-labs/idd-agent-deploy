using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: import parsing — comments, blanks, duplicates, unresolvable hosts, mixed
/// FQDN/NetBIOS.
/// </summary>
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

    [Fact]
    public async Task A_mixed_file_of_fqdns_netbios_names_and_addresses_all_resolve_to_fqdns()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver()
            .ResolvesToItself("dc01.corp.local")
            .Resolves("DC02", "dc02.corp.local")
            .Resolves("10.20.30.40", "dc03.corp.local");

        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        var report = await service.ImportAsync(
            ["dc01.corp.local", "DC02", "10.20.30.40"],
            [],
            dryRun: false,
            Ct);

        Assert.Equal(3, report.Imported.Count);
        Assert.Empty(report.Unresolved);

        var stored = await inventory.DomainControllers.GetAllAsync(includeInactive: false, Ct);
        Assert.Equal(
            ["dc01.corp.local", "dc02.corp.local", "dc03.corp.local"],
            stored.Select(d => d.Fqdn));
        Assert.All(stored, d => Assert.Equal(DiscoverySource.FileImport, d.Source));
    }

    /// <summary>
    /// PRD 6.2: unresolvable entries are reported to the operator rather than silently
    /// discarded — and no inventory row is created for them, because a host that cannot be
    /// resolved cannot be deployed to.
    /// </summary>
    [Fact]
    public async Task Unresolvable_entries_are_reported_with_their_line_number_and_not_imported()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver()
            .ResolvesToItself("dc01.corp.local")
            .FailsToResolve("dc99.corp.local", "'dc99.corp.local' did not resolve in DNS (HostNotFound).");

        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        var report = await service.ImportAsync(
            ["dc01.corp.local", "dc99.corp.local"],
            [],
            dryRun: false,
            Ct);

        var unresolved = Assert.Single(report.Unresolved);
        Assert.Equal(2, unresolved.LineNumber);
        Assert.Equal("dc99.corp.local", unresolved.RawValue);
        Assert.Contains("did not resolve in DNS", unresolved.Reason, StringComparison.Ordinal);
        Assert.True(report.HasProblems);

        var stored = await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct);
        Assert.Equal(["dc01.corp.local"], stored.Select(d => d.Fqdn));
    }

    /// <summary>
    /// PRD 6.2: applying tags to every host in a file, in one operation, is the primary
    /// import use case.
    /// </summary>
    [Fact]
    public async Task Import_applies_every_requested_tag_to_every_resolved_host()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver()
            .ResolvesToItself("dc01.corp.local", "dc02.corp.local");

        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        await service.ImportAsync(
            ["dc01.corp.local", "dc02.corp.local"],
            ["pilot", "london"],
            dryRun: false,
            Ct);

        Assert.Equal(2, (await inventory.Tags.GetDcIdsWithTagAsync("pilot", Ct)).Count);
        Assert.Equal(2, (await inventory.Tags.GetDcIdsWithTagAsync("london", Ct)).Count);
    }

    /// <summary>
    /// PRD 6.2: re-importing a file containing a known host updates it and adds tags without
    /// duplicating the row.
    /// </summary>
    [Fact]
    public async Task Re_importing_adds_tags_without_duplicating_the_host()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver().ResolvesToItself("dc01.corp.local");
        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        await service.ImportAsync(["dc01.corp.local"], ["pilot"], dryRun: false, Ct);
        await service.ImportAsync(["dc01.corp.local"], ["london"], dryRun: false, Ct);

        Assert.Single(await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct));

        var dcId = (await inventory.DomainControllers.GetByFqdnAsync("dc01.corp.local", Ct))!.Id;
        Assert.Equal(["london", "pilot"], (await inventory.Tags.GetTagsByDcAsync(Ct))[dcId]);
    }

    /// <summary>
    /// A dry run must report exactly what a real run would do, or the preview is not worth
    /// having (PRD 9, <c>import --dry-run</c>).
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_the_same_outcome_but_writes_nothing()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver()
            .ResolvesToItself("dc01.corp.local")
            .FailsToResolve("nope.corp.local", "not found");

        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        var dry = await service.ImportAsync(
            ["dc01.corp.local", "nope.corp.local"], ["pilot"], dryRun: true, Ct);

        Assert.True(dry.DryRun);
        Assert.Single(dry.Imported);
        Assert.Single(dry.Unresolved);
        Assert.Equal(["pilot"], dry.TagsApplied);

        Assert.Empty(await inventory.DomainControllers.GetAllAsync(includeInactive: true, Ct));
        Assert.Empty(await inventory.Tags.GetAllAsync(Ct));

        var real = await service.ImportAsync(
            ["dc01.corp.local", "nope.corp.local"], ["pilot"], dryRun: false, Ct);

        Assert.Equal(dry.Imported.Count, real.Imported.Count);
        Assert.Equal(dry.Unresolved.Count, real.Unresolved.Count);
    }

    [Fact]
    public async Task Import_derives_the_netbios_name_and_domain_from_the_resolved_fqdn()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var resolver = new StubHostResolver().Resolves("DC01", "dc01.corp.local");
        var service = new FileImportService(inventory.DomainControllers, inventory.Tags, resolver);

        await service.ImportAsync(["DC01"], [], dryRun: false, Ct);

        var stored = await inventory.DomainControllers.GetByFqdnAsync("dc01.corp.local", Ct);

        Assert.NotNull(stored);
        Assert.Equal("DC01", stored.NetbiosName);
        Assert.Equal("corp.local", stored.Domain);
    }

    [Fact]
    public async Task Importing_a_missing_file_names_the_path_it_could_not_find()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);

        var service = new FileImportService(
            inventory.DomainControllers, inventory.Tags, new StubHostResolver());

        var missing = Path.Combine(Path.GetTempPath(), $"had-missing-{Guid.NewGuid():N}.txt");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ImportFileAsync(missing, [], dryRun: true, Ct));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }
}
