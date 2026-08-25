using HybridAgentDeploy.Gui.Presentation;
using HybridAgentDeploy.Gui.Views;

namespace HybridAgentDeploy.Gui.Tests;

/// <summary>
/// Adding and removing tags across a selection of domain controllers (PRD 8.1, 8.2).
/// </summary>
/// <remarks>
/// Three states matter here because a selection genuinely has three answers per tag: all carry
/// it, none do, or some do. The rule that a box left alone changes nothing is what makes it
/// safe to open the dialog to change one tag without disturbing everything else.
/// </remarks>
public sealed class TagEditModelTests
{
    private static InventoryRow Dc(string name, params string[] tags) =>
        InventoryFilterTests.Row($"{name}.corp.local", "London", tags, null, id: name.GetHashCode());

    private static TagEditModel Model(IReadOnlyList<InventoryRow> selected, params string[] allTags) =>
        new(allTags.Select(t => new TagSummary(t, 0)), selected);

    [Fact]
    public void A_tag_every_selected_controller_carries_starts_checked()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02", "pilot")], "pilot");

        Assert.Equal(CheckState.Checked, model.Entries.Single().Initial);
    }

    [Fact]
    public void A_tag_no_selected_controller_carries_starts_unchecked()
    {
        var model = Model([Dc("dc01"), Dc("dc02")], "pilot");

        Assert.Equal(CheckState.Unchecked, model.Entries.Single().Initial);
    }

    /// <summary>The state a plain checkbox could not represent honestly.</summary>
    [Fact]
    public void A_tag_only_some_carry_starts_indeterminate()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02")], "pilot");

        Assert.Equal(CheckState.Indeterminate, model.Entries.Single().Initial);
    }

    [Fact]
    public void Nothing_changes_until_the_operator_moves_something()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02")], "pilot", "rodc");

        Assert.False(model.HasChanges);
        Assert.Empty(model.TagsToAdd);
        Assert.Empty(model.TagsToRemove);
    }

    /// <summary>
    /// The rule this design exists for: opening the dialog to change one tag must not quietly
    /// normalise every other mixed tag across the selection.
    /// </summary>
    [Fact]
    public void An_untouched_mixed_tag_is_left_alone()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02", "rodc")], "pilot", "rodc", "london");

        model.SetState("london", CheckState.Checked);

        Assert.Equal(["london"], model.TagsToAdd);
        Assert.Empty(model.TagsToRemove);
    }

    [Fact]
    public void Ticking_an_unchecked_tag_adds_it()
    {
        var model = Model([Dc("dc01"), Dc("dc02")], "pilot");

        model.SetState("pilot", CheckState.Checked);

        Assert.Equal(["pilot"], model.TagsToAdd);
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void Clearing_a_checked_tag_removes_it()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02", "pilot")], "pilot");

        model.SetState("pilot", CheckState.Unchecked);

        Assert.Equal(["pilot"], model.TagsToRemove);
    }

    [Fact]
    public void Ticking_a_mixed_tag_applies_it_to_the_whole_selection()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02")], "pilot");

        model.SetState("pilot", CheckState.Checked);

        Assert.Equal(["pilot"], model.TagsToAdd);
    }

    [Fact]
    public void Clearing_a_mixed_tag_removes_it_from_the_whole_selection()
    {
        var model = Model([Dc("dc01", "pilot"), Dc("dc02")], "pilot");

        model.SetState("pilot", CheckState.Unchecked);

        Assert.Equal(["pilot"], model.TagsToRemove);
    }

    /// <summary>Returning a tag to where it started is not a change.</summary>
    [Fact]
    public void Toggling_a_tag_back_leaves_no_change()
    {
        var model = Model([Dc("dc01", "pilot")], "pilot");

        model.SetState("pilot", CheckState.Unchecked);
        model.SetState("pilot", CheckState.Checked);

        Assert.False(model.HasChanges);
        Assert.Empty(model.TagsToRemove);
    }

    [Fact]
    public void Remove_all_clears_every_tag_the_selection_carries()
    {
        var model = Model(
            [Dc("dc01", "pilot", "rodc"), Dc("dc02", "pilot")],
            "pilot", "rodc", "unused");

        model.RemoveAll();

        Assert.Equal(["pilot", "rodc"], model.TagsToRemove);

        // A tag nothing carried is not "removed" — there is nothing to remove.
        Assert.DoesNotContain("unused", model.TagsToRemove);
        Assert.Empty(model.TagsToAdd);
    }

    [Fact]
    public void A_new_tag_is_added_ticked_and_counts_as_a_change()
    {
        var model = Model([Dc("dc01")], "pilot");

        model.AddOrSelect("phase-2");

        Assert.Equal(["phase-2"], model.TagsToAdd);
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void Naming_an_existing_tag_ticks_it_rather_than_duplicating_it()
    {
        var model = Model([Dc("dc01")], "pilot");

        model.AddOrSelect("PILOT");

        Assert.Single(model.Entries);
        Assert.Equal(["pilot"], model.TagsToAdd);
    }

    [Fact]
    public void Entries_are_listed_alphabetically_regardless_of_case()
    {
        var model = Model([Dc("dc01")], "zulu", "Alpha", "mike");

        Assert.Equal(["Alpha", "mike", "zulu"], model.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData(1, "1 domain controller")]
    [InlineData(4, "4 domain controllers")]
    public void The_summary_states_how_many_controllers_are_affected(int count, string expected)
    {
        var rows = Enumerable.Range(1, count).Select(i => Dc($"dc{i:00}")).ToList();
        var model = Model(rows, "pilot");

        model.SetState("pilot", CheckState.Checked);

        Assert.Contains(expected, model.Describe(), StringComparison.Ordinal);
        Assert.Contains("Adding", model.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_reports_additions_and_removals_together()
    {
        var model = Model([Dc("dc01", "rodc")], "pilot", "rodc");

        model.SetState("pilot", CheckState.Checked);
        model.SetState("rodc", CheckState.Unchecked);

        var description = model.Describe();

        Assert.Contains("Adding", description, StringComparison.Ordinal);
        Assert.Contains("Removing", description, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_changes_the_summary_says_so()
    {
        Assert.Contains("No changes", Model([Dc("dc01")], "pilot").Describe(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The connection between the tag edit list and the model behind it.
/// </summary>
/// <remarks>
/// Testing the model alone would leave the wiring unverified, and the wiring is what decides
/// whether a tag reaches a domain controller. Driving a CheckedListBox with synthetic input is
/// unreliable, so the binding is exercised directly.
/// </remarks>
public sealed class TagEditBindingTests
{
    [Fact]
    public void Ticking_a_tag_records_it_as_an_addition()
    {
        using var fixture = new Fixture(carried: false, "pilot");

        fixture.List.SetItemCheckState(0, CheckState.Checked);

        Assert.Equal(["pilot"], fixture.Model.TagsToAdd);
        Assert.True(fixture.Changed);
    }

    [Fact]
    public void Clearing_a_tag_the_selection_carries_records_a_removal()
    {
        using var fixture = new Fixture(carried: true, "pilot");

        fixture.List.SetItemCheckState(0, CheckState.Unchecked);

        Assert.Equal(["pilot"], fixture.Model.TagsToRemove);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool carried, params string[] tags)
        {
            var dc = InventoryFilterTests.Row(
                "dc01.corp.local", "London", carried ? tags : [], null, id: 1);

            Model = new TagEditModel(tags.Select(t => new TagSummary(t, 0)), [dc]);
            List = new CheckedListBox();

            foreach (var entry in Model.Entries)
            {
                List.Items.Add(entry);
            }

            for (var i = 0; i < Model.Entries.Count; i++)
            {
                List.SetItemCheckState(i, Model.Entries[i].Current);
            }

            TagEditDialog.BindList(List, Model, () => Changed = true);
        }

        public TagEditModel Model { get; }

        public CheckedListBox List { get; }

        public bool Changed { get; private set; }

        public void Dispose() => List.Dispose();
    }
}
