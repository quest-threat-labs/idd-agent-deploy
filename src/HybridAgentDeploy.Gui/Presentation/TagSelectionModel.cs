namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>
/// The tags an operator has chosen to apply, and the choices available to them.
/// </summary>
/// <remarks>
/// <para>
/// Backs the dialog shown when importing a host list. Picking from the tags that exist, rather
/// than typing a name, is the point: a typed name that does not quite match — "pilot-ring"
/// against "pilotring" — silently creates a second tag and splits the group the operator
/// meant to build. They would not find out until a later deployment targeted half the ring.
/// </para>
/// <para>
/// PRD 6.2 applies "one or more tags to every host in the file, in a single operation", so
/// several may be selected at once. Creating a genuinely new tag is still possible, but it is
/// a deliberate act rather than the consequence of a typo.
/// </para>
/// </remarks>
public sealed class TagSelectionModel
{
    private readonly List<TagSummary> _choices;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    public TagSelectionModel(IEnumerable<TagSummary> existingTags)
    {
        ArgumentNullException.ThrowIfNull(existingTags);

        _choices = [.. existingTags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every tag offered, in the order it should be listed.</summary>
    public IReadOnlyList<TagSummary> Choices => _choices;

    /// <summary>The tags to apply, in the order they were offered.</summary>
    public IReadOnlyList<string> Selected =>
        [.. _choices.Where(c => _selected.Contains(c.Name)).Select(c => c.Name)];

    /// <summary>True when no tag has ever been created; the list has nothing to offer.</summary>
    public bool HasNoTags => _choices.Count == 0;

    public bool IsSelected(string name) => _selected.Contains(name);

    public void SetSelected(string name, bool selected)
    {
        if (selected)
        {
            _selected.Add(name);
        }
        else
        {
            _selected.Remove(name);
        }
    }

    /// <summary>
    /// Adds a tag the operator has just named, and selects it.
    /// </summary>
    /// <remarks>
    /// A name matching an existing tag selects that tag rather than adding a duplicate —
    /// matched without regard to case, the same way the inventory stores them. Returns the
    /// entry that ended up selected so the caller can highlight it.
    /// </remarks>
    public TagSummary AddOrSelect(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A tag name is required.", nameof(name));
        }

        var existing = _choices.FirstOrDefault(
            c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _selected.Add(existing.Name);
            return existing;
        }

        // Not yet in the database — it is created when the import runs. Zero is accurate:
        // no domain controller carries it at this moment.
        var added = new TagSummary(trimmed, 0);
        _choices.Add(added);
        _choices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _selected.Add(added.Name);
        return added;
    }

    /// <summary>True when the import can proceed.</summary>
    public bool CanApply => _selected.Count > 0;

    /// <summary>
    /// Why the import cannot proceed, or null when it can.
    /// </summary>
    /// <remarks>
    /// At least one tag is required. Importing with none selected would read the file, resolve
    /// every host, and change nothing — an outcome an operator would reasonably read as a
    /// failure of the import rather than of their selection.
    /// </remarks>
    public string? BlockingReason => CanApply
        ? null
        : HasNoTags
            ? "No tags exist yet. Create one to apply to the hosts in this file."
            : "Select at least one tag to apply to the hosts in this file.";

    /// <summary>A short description of what is about to happen, for the dialog.</summary>
    public string Describe(int hostCount) =>
        Selected.Count switch
        {
            0 => "No tags selected.",
            1 => $"'{Selected[0]}' will be applied to every domain controller named in the file.",
            _ => $"{Selected.Count} tags will be applied to every domain controller named in " +
                 $"the file: {string.Join(", ", Selected)}.",
        };
}
