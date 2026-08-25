using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Tests;

/// <summary>
/// Inventory filtering (PRD 8.1).
/// </summary>
/// <remarks>
/// The filters decide which domain controllers an operator can see, and therefore which ones
/// they can select for a deployment. A filter that silently omits a DC would remove it from
/// the selection with nothing downstream noticing it was never offered.
/// </remarks>
public sealed class InventoryFilterTests
{
    private static readonly IReadOnlyList<InventoryRow> Rows =
    [
        Row("dc01.corp.local", "London", ["pilot"], DeploymentOutcome.Success),
        Row("dc02.corp.local", "London", ["pilot", "gc"], DeploymentOutcome.Failure),
        Row("dc03.corp.local", "Belfast", [], DeploymentOutcome.SuccessRebootRequired),
        Row("dc04.corp.local", "Belfast", ["rodc"], null),
        Row("branch-dc.corp.local", "Reading", ["pilot"], DeploymentOutcome.Timeout),
    ];

    [Fact]
    public void No_criteria_returns_everything()
    {
        Assert.Equal(Rows.Count, InventoryFilter.Apply(Rows, InventoryFilterCriteria.None).Count);
        Assert.False(InventoryFilterCriteria.None.IsFiltering);
    }

    [Fact]
    public void Free_text_matches_any_part_of_the_fqdn_case_insensitively()
    {
        var result = InventoryFilter.Apply(Rows, new InventoryFilterCriteria { FqdnContains = "BRANCH" });

        Assert.Equal(["branch-dc.corp.local"], result.Select(r => r.Fqdn));
    }

    [Fact]
    public void Filtering_by_tag_matches_any_of_a_controllers_tags()
    {
        var result = InventoryFilter.Apply(Rows, new InventoryFilterCriteria { Tag = "gc" });

        Assert.Equal(["dc02.corp.local"], result.Select(r => r.Fqdn));
    }

    [Fact]
    public void Filtering_by_site_is_an_exact_case_insensitive_match()
    {
        var result = InventoryFilter.Apply(Rows, new InventoryFilterCriteria { Site = "belfast" });

        Assert.Equal(["dc03.corp.local", "dc04.corp.local"], result.Select(r => r.Fqdn));
    }

    [Theory]
    [InlineData(OutcomeFilter.Success, "dc01.corp.local")]
    [InlineData(OutcomeFilter.RebootRequired, "dc03.corp.local")]
    [InlineData(OutcomeFilter.NeverDeployed, "dc04.corp.local")]
    public void Each_outcome_filter_selects_its_own_state(OutcomeFilter filter, string expected)
    {
        var result = InventoryFilter.Apply(Rows, new InventoryFilterCriteria { Outcome = filter });

        Assert.Equal([expected], result.Select(r => r.Fqdn));
    }

    /// <summary>
    /// A timeout is not a success and is not "never deployed"; an operator scanning for things
    /// to look at needs it under the failure filter rather than hidden in a category of its own.
    /// </summary>
    [Fact]
    public void The_failure_filter_includes_timeouts_and_excludes_never_deployed()
    {
        var result = InventoryFilter.Apply(Rows, new InventoryFilterCriteria { Outcome = OutcomeFilter.Failure });

        Assert.Equal(["dc02.corp.local", "branch-dc.corp.local"], result.Select(r => r.Fqdn));
    }

    [Fact]
    public void Criteria_combine_with_and()
    {
        var result = InventoryFilter.Apply(
            Rows,
            new InventoryFilterCriteria { Tag = "pilot", Site = "London", Outcome = OutcomeFilter.Failure });

        Assert.Equal(["dc02.corp.local"], result.Select(r => r.Fqdn));
    }

    [Fact]
    public void Whitespace_only_criteria_do_not_filter()
    {
        var criteria = new InventoryFilterCriteria { FqdnContains = "   ", Tag = "  ", Site = "" };

        Assert.Equal(Rows.Count, InventoryFilter.Apply(Rows, criteria).Count);
        Assert.False(criteria.IsFiltering);
    }

    [Fact]
    public void Dropdown_choices_are_distinct_and_sorted()
    {
        Assert.Equal(["Belfast", "London", "Reading"], InventoryFilter.SiteChoices(Rows));
        Assert.Equal(["gc", "pilot", "rodc"], InventoryFilter.TagChoices(Rows));
    }

    internal static InventoryRow Row(
        string fqdn,
        string? site,
        string[] tags,
        DeploymentOutcome? outcome,
        long id = 0) => new()
        {
            DcId = id == 0 ? fqdn.GetHashCode() : id,
            Fqdn = fqdn,
            SiteName = site,
            Tags = tags,
            LastDeployment = outcome is null
                ? null
                : new DcLastDeployment
                {
                    DcId = 0,
                    Fqdn = fqdn,
                    MsiProductVersion = "7.7.34005.0",
                    OrgId = "org",
                    CompletedUtc = "2026-03-01T10:00:00.0000000Z",
                    Outcome = outcome.Value,
                },
        };
}

/// <summary>
/// The selection an operator has ticked, which is kept separate from what the grid is showing.
/// </summary>
public sealed class SelectionStateTests
{
    private static readonly IReadOnlyList<InventoryRow> London =
    [
        InventoryFilterTests.Row("dc01.corp.local", "London", [], null, id: 1),
        InventoryFilterTests.Row("dc02.corp.local", "London", [], null, id: 2),
    ];

    private static readonly IReadOnlyList<InventoryRow> Belfast =
    [
        InventoryFilterTests.Row("dc03.corp.local", "Belfast", [], null, id: 3),
    ];

    private static IReadOnlyList<InventoryRow> All => [.. London, .. Belfast];

    /// <summary>
    /// The defect this design exists to prevent: ticking DCs, changing the filter, and
    /// silently losing them.
    /// </summary>
    [Fact]
    public void Changing_the_visible_set_does_not_drop_selections()
    {
        var selection = new SelectionState();
        selection.SelectAll(London);

        Assert.Equal(2, selection.Count);

        // The operator filters to Belfast. Their London ticks survive.
        Assert.Equal(2, selection.HiddenSelectedCount(Belfast));
        Assert.Equal(2, selection.Count);
    }

    /// <summary>
    /// The other half of the same problem: an operator must be told when their selection
    /// includes hosts the filter is hiding, rather than deploying to invisible targets.
    /// </summary>
    [Fact]
    public void Selections_hidden_by_the_current_filter_are_counted()
    {
        var selection = new SelectionState();
        selection.SelectAll(All);

        Assert.Equal(0, selection.HiddenSelectedCount(All));
        Assert.Equal(1, selection.HiddenSelectedCount(London));
        Assert.Equal(2, selection.HiddenSelectedCount(Belfast));
    }

    [Fact]
    public void Select_all_and_select_none_act_only_on_what_is_visible()
    {
        var selection = new SelectionState();
        selection.SelectAll(All);

        selection.SelectNone(London);

        Assert.Equal(1, selection.Count);
        Assert.True(selection.IsSelected(3));
        Assert.False(selection.IsSelected(1));
    }

    [Fact]
    public void A_controller_that_leaves_the_inventory_is_pruned_from_the_selection()
    {
        var selection = new SelectionState();
        selection.SelectAll(All);

        // dc03 was deactivated by a re-enumeration.
        selection.Prune(London);

        Assert.Equal(2, selection.Count);
        Assert.False(selection.IsSelected(3));
    }

    [Fact]
    public void Setting_and_clearing_one_controller_works()
    {
        var selection = new SelectionState();

        selection.Set(1, true);
        Assert.True(selection.IsSelected(1));

        selection.Set(1, false);
        Assert.False(selection.IsSelected(1));
    }
}

/// <summary>
/// Outcome colour-coding (PRD 8.1).
/// </summary>
public sealed class OutcomeStyleTests
{
    /// <summary>
    /// Colour is the fast path, never the only path: an operator with colour-vision deficiency,
    /// or one reading a screenshot in a ticket, must still be able to read the state.
    /// </summary>
    [Theory]
    [InlineData(null, "never deployed")]
    [InlineData(DeploymentOutcome.Success, "success")]
    [InlineData(DeploymentOutcome.SuccessRebootRequired, "success - reboot pending")]
    [InlineData(DeploymentOutcome.Failure, "failed")]
    [InlineData(DeploymentOutcome.Timeout, "timed out")]
    [InlineData(DeploymentOutcome.Cancelled, "cancelled")]
    [InlineData(DeploymentOutcome.Skipped, "not attempted")]
    public void Every_state_carries_readable_text_not_only_a_colour(DeploymentOutcome? outcome, string expected)
    {
        Assert.Equal(expected, OutcomeStyle.For(outcome).Text);
    }

    [Fact]
    public void The_four_specified_states_are_visually_distinct()
    {
        var colours = new[]
        {
            OutcomeStyle.For(DeploymentOutcome.Success).BackColor,
            OutcomeStyle.For(DeploymentOutcome.SuccessRebootRequired).BackColor,
            OutcomeStyle.For(DeploymentOutcome.Failure).BackColor,
            OutcomeStyle.For(null).BackColor,
        };

        Assert.Equal(4, colours.Distinct().Count());
    }

    /// <summary>
    /// A pending reboot counts as a success (PRD 10.1) but is not shown green: the install
    /// worked, and the operator should still look at it.
    /// </summary>
    [Fact]
    public void A_pending_reboot_is_not_coloured_as_a_plain_success()
    {
        Assert.NotEqual(
            OutcomeStyle.For(DeploymentOutcome.Success).BackColor,
            OutcomeStyle.For(DeploymentOutcome.SuccessRebootRequired).BackColor);
    }

    [Fact]
    public void Every_deployment_stage_has_progress_text()
    {
        foreach (var stage in Enum.GetValues<DeploymentStage>())
        {
            Assert.False(string.IsNullOrWhiteSpace(OutcomeStyle.ForStage(stage).Text));
        }

        Assert.Equal("waiting", OutcomeStyle.ForStage(null).Text);
    }
}

/// <summary>
/// Whether the deployment screen may start a run (PRD 8.3).
/// </summary>
public sealed class DeploymentFormStateTests
{
    private static readonly MsiPackageInfo Msi = new()
    {
        FilePath = @"C:\packages\agent.msi",
        FileName = "agent.msi",
        FileSizeBytes = 67_000_000,
        Sha256 = new string('A', 64),
        ProductName = "Quest Change Auditor 7.7.0 Agent",
        ProductVersion = "7.7.34005.0",
        ProductCode = "{0F4BE828-CE3B-4DC1-9C3A-714952CE6A0A}",
        LooksLikeExpectedProduct = true,
    };

    private static DeploymentFormState Ready => new()
    {
        Msi = Msi,
        OrgId = "c3a22555-da90-4a57-8042-c543d0c32bc3",
        SelectedTargetCount = 3,
    };

    [Fact]
    public void A_complete_form_can_start()
    {
        Assert.True(Ready.CanStart);
        Assert.Null(Ready.BlockingReason);
    }

    /// <summary>
    /// The rule that matters most. Two orchestrators would each permit five concurrent
    /// targets, so a second run would silently double the R7.1 ceiling against domain
    /// controllers.
    /// </summary>
    [Fact]
    public void A_second_run_cannot_start_while_one_is_in_flight()
    {
        var state = Ready with { IsDeploying = true };

        Assert.False(state.CanStart);
        Assert.Contains("already running", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PRD 8.3: if MSI extraction fails, deployment is blocked.</summary>
    [Fact]
    public void Without_a_readable_package_the_form_cannot_start()
    {
        var state = Ready with { Msi = null };

        Assert.False(state.CanStart);
        Assert.Contains("installer package", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_org_id_the_form_cannot_start(string? orgId)
    {
        var state = Ready with { OrgId = orgId };

        Assert.False(state.CanStart);
        Assert.Contains("Org ID", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_org_id_containing_a_quote_blocks_the_form()
    {
        var state = Ready with { OrgId = "org\"id" };

        Assert.False(state.CanStart);
        Assert.Contains("double-quote", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_targets_the_form_cannot_start()
    {
        var state = Ready with { SelectedTargetCount = 0 };

        Assert.False(state.CanStart);
        Assert.Contains("Inventory tab", state.BlockingReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_alternate_credential_blocks_the_form()
    {
        var state = Ready with { AlternateCredentialIncomplete = true };

        Assert.False(state.CanStart);
        Assert.Contains("password", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PRD 8.3: the preview must be the command that will actually run, so it is built through
    /// the same builder rather than assembled for display.
    /// </summary>
    [Fact]
    public void The_command_preview_shows_the_real_command()
    {
        var preview = Ready.CommandPreview(cloudMode: true);

        Assert.StartsWith("msiexec /i ", preview, StringComparison.Ordinal);
        Assert.Contains("SG=1", preview, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME=c3a22555-da90-4a57-8042-c543d0c32bc3", preview, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME_VALID=1", preview, StringComparison.Ordinal);
        Assert.Contains("/qn", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_preview_reflects_cloud_mode_being_turned_off()
    {
        Assert.DoesNotContain("SG=1", Ready.CommandPreview(cloudMode: false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_preview_explains_itself_when_it_cannot_be_built()
    {
        Assert.Contains("select an installer", (Ready with { Msi = null }).CommandPreview(true), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valid Org ID", (Ready with { OrgId = "" }).CommandPreview(true), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PRD 8.3: the confirmation dialog restates the target count and the Org ID.</summary>
    [Fact]
    public void The_confirmation_prompt_restates_the_count_and_the_org_id()
    {
        var prompt = Ready.ConfirmationPrompt(
            ["dc01.corp.local", "dc02.corp.local", "dc03.corp.local"], hiddenSelectedCount: 0);

        Assert.Contains("3 domain controllers", prompt, StringComparison.Ordinal);
        Assert.Contains("c3a22555-da90-4a57-8042-c543d0c32bc3", prompt, StringComparison.Ordinal);
        Assert.Contains("dc01.corp.local", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selection that includes hosts the filter is hiding must say so, or the count in the
    /// dialog silently disagrees with what is on screen.
    /// </summary>
    [Fact]
    public void The_confirmation_prompt_warns_about_targets_hidden_by_the_filter()
    {
        var prompt = Ready.ConfirmationPrompt(["dc01.corp.local"], hiddenSelectedCount: 2);

        Assert.Contains("2 of these are hidden", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_target_list_is_truncated_rather_than_filling_the_screen()
    {
        var targets = Enumerable.Range(1, 40).Select(i => $"dc{i:00}.corp.local").ToList();

        var prompt = (Ready with { SelectedTargetCount = 40 }).ConfirmationPrompt(targets, 0);

        Assert.Contains("and 28 more", prompt, StringComparison.Ordinal);
        Assert.Contains("40 domain controllers", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void One_target_is_described_in_the_singular()
    {
        var prompt = (Ready with { SelectedTargetCount = 1 }).ConfirmationPrompt(["dc01.corp.local"], 0);

        Assert.Contains("1 domain controller?", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The site breakdown is not decoration: R7.2 caps concurrency within a site at half its
    /// DCs, so a selection concentrated in one small site runs slower than max-parallel
    /// suggests, and the operator should see why beforehand.
    /// </summary>
    [Fact]
    public void The_target_summary_breaks_the_selection_down_by_site()
    {
        var summary = DeploymentFormState.SummariseBySite(
        [
            InventoryFilterTests.Row("dc01.corp.local", "London", [], null),
            InventoryFilterTests.Row("dc02.corp.local", "London", [], null),
            InventoryFilterTests.Row("dc03.corp.local", "Belfast", [], null),
            InventoryFilterTests.Row("dc04.corp.local", null, [], null),
        ]);

        Assert.Equal(["(no site recorded): 1", "Belfast: 1", "London: 2"], summary);
    }
}

/// <summary>
/// The tag list on the Tags screen (PRD 8.2).
/// </summary>
/// <remarks>
/// Regression cover for a reported bug: "Create tag..." appeared to do nothing. The tag row
/// was written correctly, but the list was built from the tags present on inventory rows, so a
/// tag applied to no domain controllers did not appear. Creating a tag and then applying it is
/// the obvious order to work in, which made this the first thing an operator would hit.
/// </remarks>
public sealed class TagListTests
{
    private static readonly IReadOnlyList<InventoryRow> Inventory =
    [
        InventoryFilterTests.Row("dc01.corp.local", "London", ["pilot"], null, id: 1),
        InventoryFilterTests.Row("dc02.corp.local", "London", ["pilot", "gc"], null, id: 2),
    ];

    [Fact]
    public void A_tag_applied_to_nothing_still_appears()
    {
        var list = TagList.Build([Tag("pilot"), Tag("brand-new")], Inventory);

        Assert.Equal(["brand-new", "pilot"], list.Select(t => t.Name));
        Assert.Equal(0, list.Single(t => t.Name == "brand-new").DomainControllerCount);
    }

    [Fact]
    public void Counts_come_from_the_inventory()
    {
        var list = TagList.Build([Tag("pilot"), Tag("gc")], Inventory);

        Assert.Equal(2, list.Single(t => t.Name == "pilot").DomainControllerCount);
        Assert.Equal(1, list.Single(t => t.Name == "gc").DomainControllerCount);
    }

    [Fact]
    public void Counts_match_tags_case_insensitively()
    {
        var list = TagList.Build([Tag("PILOT")], Inventory);

        Assert.Equal(2, Assert.Single(list).DomainControllerCount);
    }

    [Fact]
    public void Tags_are_listed_alphabetically_regardless_of_case()
    {
        var list = TagList.Build([Tag("zulu"), Tag("Alpha"), Tag("mike")], Inventory);

        Assert.Equal(["Alpha", "mike", "zulu"], list.Select(t => t.Name));
    }

    /// <summary>
    /// An empty tag reads as empty rather than as "(0 domain controllers)", which an operator
    /// could mistake for a failure to create it.
    /// </summary>
    [Fact]
    public void An_empty_tag_describes_itself_plainly()
    {
        var empty = Assert.Single(TagList.Build([Tag("fresh")], []));

        Assert.Contains("no domain controllers yet", empty.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 domain controller)")]
    [InlineData(2, "2 domain controllers)")]
    public void The_count_is_pluralised(int count, string expected)
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => InventoryFilterTests.Row($"dc{i:00}.corp.local", "London", ["t"], null, id: i))
            .ToList();

        Assert.Contains(expected, Assert.Single(TagList.Build([Tag("t")], rows)).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_inventory_with_no_tags_yields_every_tag_at_zero()
    {
        var list = TagList.Build([Tag("a"), Tag("b")], [InventoryFilterTests.Row("dc01.corp.local", "London", [], null)]);

        Assert.Equal(2, list.Count);
        Assert.All(list, t => Assert.Equal(0, t.DomainControllerCount));
    }

    private static TagRecord Tag(string name) => new() { Name = name };
}
