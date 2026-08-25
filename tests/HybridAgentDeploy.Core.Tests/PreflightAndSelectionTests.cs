using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 11: the blocking checklist run before any deployment starts. Its failure is what
/// produces CLI exit code 3, so it must catch a bad invocation before anything reaches a
/// domain controller.
/// </summary>
public sealed class PreflightValidatorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_valid_request_passes_every_check()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync(
            "valid.msi", "org-abc-123", [Target("dc01.corp.local")], workspace.Path, Ct);

        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => f.Detail)));
        Assert.NotNull(report.Msi);
        Assert.Equal("org-abc-123", report.OrgId);
    }

    /// <summary>
    /// Every check runs even after one fails, so an operator correcting a scripted invocation
    /// sees everything wrong with it at once rather than one problem per run.
    /// </summary>
    [Fact]
    public async Task Every_check_is_evaluated_even_when_several_fail()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync(
            "broken.msi", orgId: "  ", targets: [], logDirectory: workspace.Path, Ct);

        Assert.False(report.Passed);

        // The MSI, the Org ID and the empty selection are all reported together.
        Assert.True(report.Failures.Count >= 3);
        Assert.Contains(report.Failures, f => f.Name == "Installer package");
        Assert.Contains(report.Failures, f => f.Name == "Org ID");
        Assert.Contains(report.Failures, f => f.Name == "Targets");
    }

    [Fact]
    public async Task An_unreadable_msi_fails_the_package_check()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync(
            "broken.msi", "org", [Target("dc01.corp.local")], workspace.Path, Ct);

        Assert.False(report.Passed);
        Assert.Null(report.Msi);
        Assert.Contains(report.Failures, f => f.Name == "Installer package");
    }

    [Fact]
    public async Task An_empty_target_list_fails_with_a_next_step()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync("valid.msi", "org", [], workspace.Path, Ct);

        var failure = Assert.Single(report.Failures, f => f.Name == "Targets");
        Assert.Contains("--all", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target that does not resolve cannot be deployed to, and finding that out now beats
    /// finding it out per target during the run.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_target_fails_the_dns_check_and_is_named()
    {
        using var workspace = new TempWorkspace();

        var resolver = new StubHostResolver()
            .ResolvesToItself("dc01.corp.local")
            .FailsToResolve("dc99.corp.local", "not found");

        var report = await new PreflightValidator(new StubMsiInspector(), resolver).ValidateAsync(
            "valid.msi", "org", [Target("dc01.corp.local"), Target("dc99.corp.local")], workspace.Path, Ct);

        var failure = Assert.Single(report.Failures, f => f.Name == "DNS resolution");
        Assert.Contains("dc99.corp.local", failure.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("dc01.corp.local", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The log directory is checked by writing to it. An ACL that looks correct and a volume
    /// that is full look identical until you try.
    /// </summary>
    [Fact]
    public async Task An_unwritable_log_directory_fails_with_a_next_step()
    {
        // A path under a file rather than a directory: reliably unusable on every machine,
        // unlike a permissions-based fixture.
        using var workspace = new TempWorkspace();
        var filePath = Path.Combine(workspace.Path, "not-a-directory.txt");
        await File.WriteAllTextAsync(filePath, "x", Ct);

        var report = await Validator().ValidateAsync(
            "valid.msi", "org", [Target("dc01.corp.local")], Path.Combine(filePath, "logs"), Ct);

        var failure = Assert.Single(report.Failures, f => f.Name == "Run log directory");
        Assert.Contains("--log-dir", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>PRD 5.5: an unexpected product name warns; it never blocks.</summary>
    [Fact]
    public async Task An_unexpected_product_name_warns_without_failing()
    {
        using var workspace = new TempWorkspace();

        var inspector = new StubMsiInspector { ProductName = "Notepad++", LooksExpected = false };

        var report = await new PreflightValidator(inspector, Resolver()).ValidateAsync(
            "valid.msi", "org", [Target("dc01.corp.local")], workspace.Path, Ct);

        Assert.True(report.Passed);
        Assert.Contains(report.Warnings, w => w.Contains("does not look like", StringComparison.Ordinal));
    }

    /// <summary>
    /// The double quote is the one Org ID character that is rejected rather than warned about,
    /// so it must block the checklist too.
    /// </summary>
    [Fact]
    public async Task An_org_id_containing_a_quote_fails_the_checklist()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync(
            "valid.msi", "org\"id", [Target("dc01.corp.local")], workspace.Path, Ct);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Name == "Org ID");
    }

    /// <summary>
    /// A refused deploy — most commonly one missing <c>--confirm</c> — must not leave an empty
    /// directory behind. Each invocation names a fresh timestamped directory, so creating it
    /// during validation would orphan one on every refusal, and the operator's log root would
    /// slowly fill with them.
    /// </summary>
    [Fact]
    public async Task Validating_does_not_create_the_run_log_directory()
    {
        using var workspace = new TempWorkspace();
        var runDirectory = Path.Combine(workspace.Path, "20260825-030011-3b454eec");

        var report = await Validator().ValidateAsync(
            "valid.msi", "org", [Target("dc01.corp.local")], runDirectory, Ct);

        Assert.True(report.Passed);
        Assert.False(
            Directory.Exists(runDirectory),
            "Validation created the run directory; a refused deploy would orphan it.");
    }

    [Fact]
    public async Task A_writable_parent_satisfies_the_check_for_a_directory_not_yet_created()
    {
        using var workspace = new TempWorkspace();

        var report = await Validator().ValidateAsync(
            "valid.msi",
            "org",
            [Target("dc01.corp.local")],
            Path.Combine(workspace.Path, "nested", "deeper", "run"),
            Ct);

        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => f.Detail)));
    }

    private static PreflightValidator Validator() => new(new StubMsiInspector(), Resolver());

    private static StubHostResolver Resolver() =>
        new StubHostResolver().ResolvesToItself("dc01.corp.local", "dc02.corp.local");

    private static DeploymentTarget Target(string fqdn) => new(1, fqdn, "London");

    /// <summary>An inspector that succeeds for "valid.msi" and fails for anything else.</summary>
    private sealed class StubMsiInspector : IMsiInspector
    {
        public string ProductName { get; init; } = "Quest Change Auditor 7.7.0 Agent";

        public bool LooksExpected { get; init; } = true;

        public Task<MsiPackageInfo> InspectAsync(string msiPath, CancellationToken ct)
        {
            if (!msiPath.Equals("valid.msi", StringComparison.OrdinalIgnoreCase))
            {
                throw new MsiInspectionException($"'{msiPath}' could not be opened as an MSI.");
            }

            return Task.FromResult(new MsiPackageInfo
            {
                FilePath = msiPath,
                FileName = msiPath,
                FileSizeBytes = 1024,
                Sha256 = new string('A', 64),
                ProductName = ProductName,
                ProductVersion = "7.7.34005.0",
                ProductCode = "{0F4BE828-CE3B-4DC1-9C3A-714952CE6A0A}",
                LooksLikeExpectedProduct = LooksExpected,
            });
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// Turning an operator's selection into targets. Shared by the CLI and, later, the GUI — a tag
/// that resolved differently depending on which interface asked would be a nasty defect.
/// </summary>
public sealed class TargetSelectorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Selecting_by_tag_returns_only_the_tagged_controllers()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var ids = await SeedAsync(inventory, ("dc01", "London"), ("dc02", "London"), ("dc03", "Belfast"));

        var pilot = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);
        await inventory.Tags.ApplyTagAsync(pilot.Id, [ids["dc01"], ids["dc03"]], Ct);

        var selection = await Selector(inventory).ResolveAsync(["pilot"], [], all: false, Ct);

        Assert.Equal(
            ["dc01.corp.local", "dc03.corp.local"],
            selection.Targets.Select(t => t.Fqdn));
    }

    [Fact]
    public async Task Selecting_by_host_accepts_an_fqdn_or_a_netbios_name()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedAsync(inventory, ("dc01", "London"), ("dc02", "London"));

        var selection = await Selector(inventory).ResolveAsync([], ["dc01.corp.local", "DC02"], all: false, Ct);

        Assert.Equal(["dc01.corp.local", "dc02.corp.local"], selection.Targets.Select(t => t.Fqdn));
    }

    [Fact]
    public async Task Selecting_all_returns_every_active_controller()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedAsync(inventory, ("dc01", "London"), ("dc02", "Belfast"));

        var selection = await Selector(inventory).ResolveAsync([], [], all: true, Ct);

        Assert.Equal(2, selection.Targets.Count);
    }

    /// <summary>
    /// Overlapping selections must not deploy to the same DC twice — which, with the
    /// concurrency guards, would mean two msiexec runs racing on one domain controller.
    /// </summary>
    [Fact]
    public async Task Overlapping_tags_and_hosts_select_each_controller_once()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var ids = await SeedAsync(inventory, ("dc01", "London"), ("dc02", "London"));

        var pilot = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);
        await inventory.Tags.ApplyTagAsync(pilot.Id, [ids["dc01"], ids["dc02"]], Ct);

        var selection = await Selector(inventory)
            .ResolveAsync(["pilot"], ["dc01.corp.local"], all: true, Ct);

        Assert.Equal(2, selection.Targets.Count);
        Assert.Equal(2, selection.Targets.Select(t => t.DcId).Distinct().Count());
    }

    /// <summary>
    /// A mistyped tag in a scheduled script would otherwise resolve silently to nothing, and
    /// the run would report a cheerful success having deployed to no one.
    /// </summary>
    [Fact]
    public async Task An_unknown_tag_is_reported_rather_than_selecting_nothing_quietly()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedAsync(inventory, ("dc01", "London"));

        var selection = await Selector(inventory).ResolveAsync(["typo"], [], all: false, Ct);

        Assert.Empty(selection.Targets);
        Assert.Equal(["typo"], selection.UnknownTags);
    }

    [Fact]
    public async Task An_unknown_host_is_reported()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedAsync(inventory, ("dc01", "London"));

        var selection = await Selector(inventory).ResolveAsync([], ["nope.corp.local"], all: false, Ct);

        Assert.Empty(selection.Targets);
        Assert.Equal(["nope.corp.local"], selection.UnknownHosts);
    }

    /// <summary>
    /// The R7.2 denominator counts the inventory, not the selection: the guard protects a
    /// site's surviving capacity, which does not depend on how many DCs were ticked.
    /// </summary>
    [Fact]
    public async Task Site_counts_reflect_the_whole_inventory_not_the_selection()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var ids = await SeedAsync(
            inventory, ("dc01", "London"), ("dc02", "London"), ("dc03", "London"), ("dc04", "Belfast"));

        var pilot = await inventory.Tags.GetOrCreateAsync("pilot", null, Ct);
        await inventory.Tags.ApplyTagAsync(pilot.Id, [ids["dc01"]], Ct);

        var selection = await Selector(inventory).ResolveAsync(["pilot"], [], all: false, Ct);

        Assert.Single(selection.Targets);
        Assert.Equal(3, selection.ActiveDcsPerSite["London"]);
        Assert.Equal(1, selection.ActiveDcsPerSite["Belfast"]);
    }

    /// <summary>A DC that has left the forest keeps its history but is never a target.</summary>
    [Fact]
    public async Task An_inactive_controller_is_never_selected()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        await SeedAsync(inventory, ("dc01", "London"), ("dc02", "London"));

        await inventory.DomainControllers.DeactivateEnumeratedExceptAsync(["dc01.corp.local"], Ct);

        var selection = await Selector(inventory).ResolveAsync([], [], all: true, Ct);

        Assert.Equal(["dc01.corp.local"], selection.Targets.Select(t => t.Fqdn));
    }

    private static TargetSelector Selector(TemporaryInventory inventory) =>
        new(inventory.DomainControllers, inventory.Tags);

    private static async Task<Dictionary<string, long>> SeedAsync(
        TemporaryInventory inventory,
        params (string Name, string Site)[] hosts)
    {
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, site) in hosts)
        {
            ids[name] = await inventory.DomainControllers.UpsertAsync(
                new DomainControllerRecord
                {
                    Fqdn = $"{name}.corp.local",
                    NetbiosName = name.ToUpperInvariant(),
                    SiteName = site,
                    Source = DiscoverySource.AdEnumeration,
                },
                Ct);
        }

        return ids;
    }
}
