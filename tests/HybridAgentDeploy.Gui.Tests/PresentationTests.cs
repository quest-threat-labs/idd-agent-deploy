using HybridAgentDeploy.Gui.Views;
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
    /// <remarks>
    /// Both activities are checked, and each blocks the other. A validation opens sessions to
    /// the same domain controllers under its own ceiling of five, so allowing one to run
    /// alongside a deployment would make the effective ceiling ten just as surely as a second
    /// deployment would.
    /// </remarks>
    [Theory]
    [InlineData("deployment")]
    [InlineData("validation")]
    public void Nothing_else_can_start_while_a_run_is_in_flight(string activity)
    {
        var state = Ready with { ActivityInFlight = activity };

        Assert.False(state.CanStart);
        Assert.False(state.CanValidate);
        Assert.Contains("already running", state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(activity, state.BlockingReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already running", state.ValidationBlockingReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validation does not need an Org ID: no installer runs, so there is nothing for one to
    /// configure. Requiring one would make an operator invent a value to find out whether
    /// their domain controllers are reachable — and an invented value is exactly what should
    /// not be sitting in the box when they later press Start.
    /// </summary>
    [Fact]
    public void Validation_does_not_require_an_org_id()
    {
        var state = Ready with { OrgId = null };

        Assert.False(state.CanStart);
        Assert.True(state.CanValidate);
        Assert.Null(state.ValidationBlockingReason);
    }

    [Fact]
    public void Validation_still_requires_a_package_and_at_least_one_target()
    {
        Assert.False((Ready with { Msi = null }).CanValidate);
        Assert.Contains(
            "installer package",
            (Ready with { Msi = null }).ValidationBlockingReason!,
            StringComparison.OrdinalIgnoreCase);

        Assert.False((Ready with { SelectedTargetCount = 0 }).CanValidate);
    }

    /// <summary>
    /// The operator is told plainly that nothing will be installed, before anything opens a
    /// session to a Tier 0 host.
    /// </summary>
    [Fact]
    public void The_validation_prompt_says_nothing_is_installed()
    {
        var prompt = Ready.ValidationPrompt(["dc01.corp.local"], hiddenSelectedCount: 0);

        Assert.Contains("Nothing is installed", prompt, StringComparison.Ordinal);
        Assert.Contains("msiexec is not run", prompt, StringComparison.Ordinal);
        Assert.Contains("dc01.corp.local", prompt, StringComparison.Ordinal);
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

/// <summary>
/// The "Run as" choice on the deployment screen (SEC2).
/// </summary>
/// <remarks>
/// Regression cover for a reported bug: selecting "Alternate account" did not deselect
/// "Current Windows identity". WinForms scopes radio-button exclusion to the immediate parent,
/// and each option had been placed in its own row panel — two groups of one.
///
/// This was not only cosmetic. Whether alternate credentials are used is decided by the
/// alternate button's checked state, so an operator who filled in an alternate account and then
/// clicked back to their current identity would have deployed under the alternate account while
/// the screen showed otherwise — running against a domain controller as an account other than
/// the one they believed they had chosen.
/// </remarks>
public sealed class RunAsPanelTests
{
    [Fact]
    public void Both_options_share_one_parent_so_windows_treats_them_as_one_group()
    {
        using var fixture = new RunAsFixture();

        Assert.NotNull(fixture.Current.Parent);
        Assert.Same(fixture.Current.Parent, fixture.Alternate.Parent);
    }

    /// <summary>The behaviour that was reported broken, asserted directly.</summary>
    [Fact]
    public void Selecting_the_alternate_account_deselects_the_current_identity()
    {
        using var fixture = new RunAsFixture();
        fixture.Current.Checked = true;

        fixture.Alternate.Checked = true;

        Assert.True(fixture.Alternate.Checked);
        Assert.False(fixture.Current.Checked);
    }

    [Fact]
    public void Selecting_the_current_identity_deselects_the_alternate_account()
    {
        using var fixture = new RunAsFixture();
        fixture.Alternate.Checked = true;

        fixture.Current.Checked = true;

        Assert.True(fixture.Current.Checked);
        Assert.False(fixture.Alternate.Checked);
    }

    /// <summary>
    /// The two can never both be selected, which is what decides whether a run uses the
    /// operator's own identity or an alternate account.
    /// </summary>
    [Fact]
    public void The_two_options_are_never_selected_at_once()
    {
        using var fixture = new RunAsFixture();

        foreach (var select in new[] { true, false, true, true, false })
        {
            if (select)
            {
                fixture.Alternate.Checked = true;
            }
            else
            {
                fixture.Current.Checked = true;
            }

            Assert.False(fixture.Current.Checked && fixture.Alternate.Checked);
        }
    }

    [Fact]
    public void The_alternate_account_fields_are_present_in_the_panel()
    {
        using var fixture = new RunAsFixture();

        Assert.Same(fixture.Panel, fixture.UserName.Parent);
        Assert.Same(fixture.Panel, fixture.Password.Parent);
    }

    private sealed class RunAsFixture : IDisposable
    {
        public RunAsFixture()
        {
            Current = new RadioButton { Text = "Current Windows identity", Checked = true };
            Alternate = new RadioButton { Text = "Alternate account:" };
            UserName = new TextBox();
            Password = new TextBox();

            Panel = DeployTab.RunAsPanel(Current, Alternate, UserName, Password);

            // A parent form so the controls behave as they do in the running application.
            Host = new Form();
            Host.Controls.Add(Panel);
        }

        public RadioButton Current { get; }

        public RadioButton Alternate { get; }

        public TextBox UserName { get; }

        public TextBox Password { get; }

        public Control Panel { get; }

        private Form Host { get; }

        public void Dispose() => Host.Dispose();
    }
}

/// <summary>
/// Choosing which tags an import applies (PRD 6.2).
/// </summary>
/// <remarks>
/// The import dialog used to be a free-text box. A name that did not quite match an existing
/// tag created a second one and split the group the operator was building — silently, and only
/// visible later when a deployment targeted half the ring. Selection is now from the tags that
/// exist, and this covers the rules behind that.
/// </remarks>
public sealed class TagSelectionModelTests
{
    private static TagSelectionModel New(params string[] existing) =>
        new(existing.Select(name => new TagSummary(name, 0)));

    [Fact]
    public void Choices_are_listed_alphabetically_regardless_of_case()
    {
        var model = New("zulu", "Alpha", "mike");

        Assert.Equal(["Alpha", "mike", "zulu"], model.Choices.Select(c => c.Name));
    }

    [Fact]
    public void Nothing_is_selected_until_the_operator_chooses()
    {
        var model = New("pilot", "london");

        Assert.Empty(model.Selected);
        Assert.False(model.CanApply);
    }

    /// <summary>PRD 6.2 applies one or more tags in a single operation.</summary>
    [Fact]
    public void Several_tags_can_be_applied_at_once()
    {
        var model = New("pilot", "london", "rodc");

        model.SetSelected("pilot", true);
        model.SetSelected("rodc", true);

        Assert.Equal(["pilot", "rodc"], model.Selected);
        Assert.True(model.CanApply);
    }

    [Fact]
    public void Deselecting_removes_a_tag_from_the_result()
    {
        var model = New("pilot", "london");
        model.SetSelected("pilot", true);
        model.SetSelected("london", true);

        model.SetSelected("pilot", false);

        Assert.Equal(["london"], model.Selected);
    }

    /// <summary>
    /// The defect this dialog exists to prevent. A name matching an existing tag selects that
    /// tag rather than adding a near-duplicate that would split the group.
    /// </summary>
    [Fact]
    public void Naming_an_existing_tag_selects_it_rather_than_duplicating_it()
    {
        var model = New("pilot");

        var result = model.AddOrSelect("PILOT");

        Assert.Single(model.Choices);
        Assert.Equal("pilot", result.Name);
        Assert.Equal(["pilot"], model.Selected);
    }

    [Fact]
    public void A_genuinely_new_tag_is_added_and_selected()
    {
        var model = New("pilot");

        model.AddOrSelect("phase-2");

        Assert.Equal(["phase-2", "pilot"], model.Choices.Select(c => c.Name));
        Assert.Equal(["phase-2"], model.Selected);
        Assert.True(model.CanApply);
    }

    [Fact]
    public void A_new_tag_is_trimmed()
    {
        var model = New();

        Assert.Equal("phase-2", model.AddOrSelect("  phase-2  ").Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => New().AddOrSelect(name));
    }

    /// <summary>
    /// An import with no tags would read the file, resolve every host, and change nothing — an
    /// outcome an operator would read as the import having failed.
    /// </summary>
    [Fact]
    public void At_least_one_tag_is_required_and_the_reason_says_so()
    {
        var model = New("pilot");

        Assert.False(model.CanApply);
        Assert.Contains("Select at least one tag", model.BlockingReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_tags_at_all_the_reason_points_at_creating_one()
    {
        var model = New();

        Assert.True(model.HasNoTags);
        Assert.Contains("Create one", model.BlockingReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_tag_clears_the_blocking_reason()
    {
        var model = New("pilot");
        model.SetSelected("pilot", true);

        Assert.Null(model.BlockingReason);
    }

    [Theory]
    [InlineData(new[] { "pilot" }, "'pilot' will be applied")]
    [InlineData(new[] { "pilot", "rodc" }, "2 tags will be applied")]
    public void The_summary_describes_what_will_happen(string[] chosen, string expected)
    {
        var model = New("pilot", "rodc");

        foreach (var tag in chosen)
        {
            model.SetSelected(tag, true);
        }

        Assert.Contains(expected, model.Describe(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_order_follows_the_listing_not_the_clicking()
    {
        var model = New("alpha", "zulu");

        model.SetSelected("zulu", true);
        model.SetSelected("alpha", true);

        Assert.Equal(["alpha", "zulu"], model.Selected);
    }
}

/// <summary>
/// The connection between the tag list control and the model behind it.
/// </summary>
/// <remarks>
/// Testing the model alone would leave the wiring unverified — and the wiring is what decides
/// which tags an import actually applies. Driving the real control with synthetic mouse and
/// keyboard messages proved unreliable, so the binding is exercised directly instead.
/// </remarks>
public sealed class TagSelectionBindingTests
{
    [Fact]
    public void Ticking_an_item_selects_that_tag()
    {
        using var fixture = new BindingFixture("pilot", "rodc");

        fixture.List.SetItemChecked(0, true);

        Assert.Equal(["pilot"], fixture.Model.Selected);
        Assert.True(fixture.Model.CanApply);
    }

    [Fact]
    public void Unticking_an_item_deselects_that_tag()
    {
        using var fixture = new BindingFixture("pilot", "rodc");
        fixture.List.SetItemChecked(0, true);

        fixture.List.SetItemChecked(0, false);

        Assert.Empty(fixture.Model.Selected);
        Assert.False(fixture.Model.CanApply);
    }

    [Fact]
    public void Several_items_can_be_ticked()
    {
        using var fixture = new BindingFixture("pilot", "rodc", "london");

        fixture.List.SetItemChecked(0, true);
        fixture.List.SetItemChecked(2, true);

        // Listed alphabetically: london, pilot, rodc.
        Assert.Equal(["london", "rodc"], fixture.Model.Selected);
    }

    /// <summary>
    /// The caller is told when the selection changes, which is what re-enables the Apply
    /// button and updates the summary.
    /// </summary>
    [Fact]
    public void The_change_callback_fires_when_an_item_is_ticked()
    {
        using var fixture = new BindingFixture("pilot");

        fixture.List.SetItemChecked(0, true);

        Assert.True(fixture.Changed);
    }

    private sealed class BindingFixture : IDisposable
    {
        public BindingFixture(params string[] tags)
        {
            Model = new TagSelectionModel(tags.Select(t => new TagSummary(t, 0)));
            List = new CheckedListBox();

            foreach (var choice in Model.Choices)
            {
                List.Items.Add(choice, false);
            }

            TagSelectionDialog.BindList(List, Model, () => Changed = true);
        }

        public TagSelectionModel Model { get; }

        public CheckedListBox List { get; }

        public bool Changed { get; private set; }

        public void Dispose() => List.Dispose();
    }
}
