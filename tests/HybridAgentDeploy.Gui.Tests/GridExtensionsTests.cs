using HybridAgentDeploy.Gui.Views;

namespace HybridAgentDeploy.Gui.Tests;

/// <summary>
/// Row tagging (PRD 8.5).
/// </summary>
/// <remarks>
/// The history grids carry their backing record on <c>DataGridViewRow.Tag</c>, and the buttons
/// below them are enabled from a <c>SelectionChanged</c> handler that reads it. Adding the first
/// row makes it current and raises that event from inside <c>Rows.Add</c>, so whether the tag is
/// attached before or after the add decides whether the handler can see it — and no second event
/// fires to correct the answer. These tests pin that ordering.
/// </remarks>
public sealed class GridExtensionsTests
{
    [Fact]
    public void Tagged_row_carries_its_tag_when_the_selection_event_fires()
    {
        using var grid = NewGrid();

        object? seen = null;
        var selectionChanges = 0;

        grid.SelectionChanged += (_, _) =>
        {
            selectionChanges++;
            seen = grid.CurrentRow?.Tag;
        };

        grid.AddTaggedRow("first", "dc01.corp.local");
        grid.AddTaggedRow("second", "dc02.corp.local");

        Assert.Equal(1, selectionChanges);
        Assert.Equal("first", seen);
    }

    /// <summary>
    /// The regression this guards: assigning the tag after <c>Rows.Add</c> leaves the handler
    /// looking at an apparently untagged row, and nothing fires again to put that right.
    /// </summary>
    [Fact]
    public void Tagging_after_the_add_is_too_late_for_the_selection_event()
    {
        using var grid = NewGrid();

        object? seen = null;

        grid.SelectionChanged += (_, _) => seen = grid.CurrentRow?.Tag;

        var index = grid.Rows.AddRow("dc01.corp.local");
        grid.Rows[index].Tag = "first";

        Assert.Null(seen);
        Assert.Equal("first", grid.CurrentRow?.Tag);
    }

    [Fact]
    public void Null_cell_values_render_as_empty_text()
    {
        using var grid = NewGrid();

        // A lone null would bind to the params array itself rather than to a single cell value.
        var index = grid.AddTaggedRow("tag", (object?)null);

        Assert.Equal(string.Empty, grid.Rows[index].Cells[0].Value);
    }

    /// <summary>A grid configured as the history tab's two are.</summary>
    /// <remarks>
    /// Forcing the handle is not incidental. A <c>DataGridView</c> whose handle has never been
    /// created does not make an added row current and raises no <c>SelectionChanged</c> at all,
    /// so the ordering under test only exists once the control is real — as it always is by the
    /// time the tab's <c>Load</c> handler populates it. No parent form is needed for that, which
    /// keeps these tests free of a window.
    /// </remarks>
    private static DataGridView NewGrid()
    {
        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
        };

        grid.Columns.Add("fqdn", "Domain controller");

        _ = grid.Handle;

        return grid;
    }
}
