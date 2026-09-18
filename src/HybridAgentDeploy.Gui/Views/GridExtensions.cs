namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Grid helpers.
/// </summary>
internal static class GridExtensions
{
    /// <summary>
    /// Adds a row, rendering null cell values as empty text.
    /// </summary>
    /// <remarks>
    /// <c>DataGridViewRowCollection.Add</c> takes a non-nullable object array, but most columns
    /// here are genuinely optional — a domain controller with no recorded site, a result with
    /// no exit code. Converting once, here, keeps every call site free of null-coalescing
    /// noise and stops an empty cell being rendered as the string "null".
    /// </remarks>
    public static int AddRow(this DataGridViewRowCollection rows, params object?[] values) =>
        rows.Add([.. values.Select(value => value ?? string.Empty)]);

    /// <summary>
    /// Adds a row already carrying <paramref name="tag"/>, rendering null cell values as empty text.
    /// </summary>
    /// <remarks>
    /// The tag is attached before the row joins the grid, and that ordering is the point of this
    /// overload. Adding the first row makes it current, which raises <c>SelectionChanged</c>
    /// synchronously from inside <c>Rows.Add</c>. A handler reading <c>CurrentRow.Tag</c> would
    /// see null if the tag were assigned afterwards, and no further event fires for the rows that
    /// follow — so the grid would sit on a selected-looking row the handler believes is untagged.
    /// </remarks>
    public static int AddTaggedRow(this DataGridView grid, object tag, params object?[] values)
    {
        var row = new DataGridViewRow();
        row.CreateCells(grid, [.. values.Select(value => value ?? string.Empty)]);
        row.Tag = tag;

        return grid.Rows.Add(row);
    }
}
