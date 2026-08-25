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
}
