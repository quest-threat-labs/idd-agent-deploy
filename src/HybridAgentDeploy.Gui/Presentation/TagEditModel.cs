using System.Windows.Forms;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>
/// One tag as it applies to the current selection.
/// </summary>
/// <param name="Initial">
/// How the selection stood when the dialog opened: <see cref="CheckState.Checked"/> when every
/// selected domain controller carries the tag, <see cref="CheckState.Unchecked"/> when none do,
/// and <see cref="CheckState.Indeterminate"/> when they differ.
/// </param>
/// <param name="CarryingCount">How many of the selected controllers carry the tag.</param>
/// <param name="SelectedCount">How many controllers are selected.</param>
public sealed record TagEditEntry(
    string Name,
    CheckState Initial,
    int CarryingCount,
    int SelectedCount)
{
    public CheckState Current { get; set; } = Initial;

    /// <summary>True when the operator has moved this tag off the state it started in.</summary>
    public bool Changed => Current != Initial;

    /// <summary>
    /// What the list shows for this tag.
    /// </summary>
    /// <remarks>
    /// The name alone for a tag every controller carries or none do — the box already says
    /// which. For a mixed tag the counts are spelled out, because "some of them" is exactly
    /// the case where an operator needs to know how many before deciding for all of them.
    ///
    /// A record's generated ToString prints its properties, which is what the list displayed
    /// until this was added: "TagEditEntry { Name = pilot, Initial = Indeterminate, ... }".
    /// </remarks>
    public override string ToString() =>
        Initial == CheckState.Indeterminate
            ? $"{Name}   (on {CarryingCount} of {SelectedCount})"
            : Name;
}

/// <summary>
/// Applying and removing tags across the selected domain controllers (PRD 8.1, 8.2).
/// </summary>
/// <remarks>
/// <para>
/// Three states, because with several controllers selected a tag genuinely has three answers:
/// all of them carry it, none do, or some do. Collapsing that to a checkbox would force the
/// operator to choose for controllers they were not thinking about.
/// </para>
/// <para>
/// A box left as it was changes nothing. Only tags the operator actually moves are applied or
/// removed, so opening this to change one tag cannot quietly normalise every other tag across
/// the selection — changes that would not be asked for and would not be seen.
/// </para>
/// </remarks>
public sealed class TagEditModel
{
    private readonly List<TagEditEntry> _entries;

    /// <param name="allTags">Every tag in the inventory.</param>
    /// <param name="selected">The domain controllers the operator has ticked.</param>
    public TagEditModel(IEnumerable<TagSummary> allTags, IReadOnlyList<InventoryRow> selected)
    {
        ArgumentNullException.ThrowIfNull(allTags);
        ArgumentNullException.ThrowIfNull(selected);

        SelectedCount = selected.Count;

        _entries =
        [
            .. allTags
                .Select(tag => BuildEntry(tag.Name, selected))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    public IReadOnlyList<TagEditEntry> Entries => _entries;

    public int SelectedCount { get; }

    public bool HasNoTags => _entries.Count == 0;

    /// <summary>
    /// Tags to apply to every selected controller.
    /// </summary>
    /// <remarks>
    /// A tag the operator ticked that was not already on all of them. Applying to a controller
    /// that already carries it is harmless — the association table ignores the duplicate — so
    /// the whole selection is passed rather than working out the difference per controller.
    /// </remarks>
    public IReadOnlyList<string> TagsToAdd =>
        [.. _entries.Where(e => e.Current == CheckState.Checked && e.Initial != CheckState.Checked)
            .Select(e => e.Name)];

    /// <summary>Tags to remove from every selected controller.</summary>
    public IReadOnlyList<string> TagsToRemove =>
        [.. _entries.Where(e => e.Current == CheckState.Unchecked && e.Initial != CheckState.Unchecked)
            .Select(e => e.Name)];

    public bool HasChanges => _entries.Any(e => e.Changed);

    public void SetState(string tagName, CheckState state)
    {
        var entry = Find(tagName);
        if (entry is not null)
        {
            entry.Current = state;
        }
    }

    public CheckState StateOf(string tagName) => Find(tagName)?.Current ?? CheckState.Unchecked;

    /// <summary>
    /// Clears every tag from the selection.
    /// </summary>
    /// <remarks>
    /// Staged like any other change rather than applied on the spot, so it can be reviewed in
    /// the summary and abandoned with Cancel.
    /// </remarks>
    public void RemoveAll()
    {
        foreach (var entry in _entries)
        {
            entry.Current = CheckState.Unchecked;
        }
    }

    /// <summary>
    /// Adds a tag the operator has just named, ticked so it will be applied.
    /// </summary>
    /// <remarks>
    /// A name matching an existing tag ticks that tag rather than creating a near-duplicate —
    /// matched without regard to case, as the inventory stores them. Typing a not-quite-matching
    /// name is how a group silently splits in two.
    /// </remarks>
    public TagEditEntry AddOrSelect(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A tag name is required.", nameof(name));
        }

        var existing = Find(trimmed);
        if (existing is not null)
        {
            existing.Current = CheckState.Checked;
            return existing;
        }

        // Not on any controller yet, so it starts unchecked and is immediately ticked — which
        // registers as a change and will be applied.
        var added = new TagEditEntry(trimmed, CheckState.Unchecked, 0, SelectedCount)
        {
            Current = CheckState.Checked,
        };

        _entries.Add(added);
        _entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return added;
    }

    /// <summary>
    /// What applying will do, in the operator's terms.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than left implicit. The dialog can be opened against sixty domain
    /// controllers, and "apply" should never be a leap of faith about which of them change.
    /// </remarks>
    public string Describe()
    {
        if (!HasChanges)
        {
            return "No changes. Tick a tag to add it to the selection, or clear one to remove it.";
        }

        var parts = new List<string>();

        if (TagsToAdd.Count > 0)
        {
            parts.Add($"Adding {Quote(TagsToAdd)} to {Controllers(SelectedCount)}.");
        }

        if (TagsToRemove.Count > 0)
        {
            parts.Add($"Removing {Quote(TagsToRemove)} from {Controllers(SelectedCount)}.");
        }

        return string.Join(" ", parts);
    }

    private TagEditEntry? Find(string name) =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Quote(IReadOnlyList<string> names) =>
        string.Join(", ", names.Select(n => $"'{n}'"));

    private static string Controllers(int count) =>
        count == 1 ? "1 domain controller" : $"{count} domain controllers";

    private static TagEditEntry BuildEntry(string tagName, IReadOnlyList<InventoryRow> selected)
    {
        var carrying = selected.Count(row => row.Tags.Contains(tagName, StringComparer.OrdinalIgnoreCase));

        var state = selected.Count == 0 || carrying == 0 ? CheckState.Unchecked
            : carrying == selected.Count ? CheckState.Checked
            : CheckState.Indeterminate;

        return new TagEditEntry(tagName, state, carrying, selected.Count);
    }
}
