using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>
/// A tag and how many domain controllers currently carry it.
/// </summary>
public sealed record TagSummary(string Name, int DomainControllerCount)
{
    public override string ToString() =>
        DomainControllerCount == 0
            ? $"{Name}  (no domain controllers yet)"
            : $"{Name}  ({DomainControllerCount} domain controller{(DomainControllerCount == 1 ? string.Empty : "s")})";
}

/// <summary>
/// Builds the tag list shown on the Tags screen (PRD 8.2).
/// </summary>
/// <remarks>
/// Driven by the tag table, not by the tags present on inventory rows. Deriving the list from
/// the inventory join meant a tag applied to nothing did not exist as far as the screen was
/// concerned — so creating a tag appeared to do nothing at all, even though the row had been
/// written. Creating a tag and then applying it is the obvious order to work in, which made
/// that the first thing an operator would hit.
/// </remarks>
public static class TagList
{
    public static IReadOnlyList<TagSummary> Build(
        IEnumerable<TagRecord> allTags,
        IEnumerable<InventoryRow> inventory)
    {
        ArgumentNullException.ThrowIfNull(allTags);
        ArgumentNullException.ThrowIfNull(inventory);

        var counts = inventory
            .SelectMany(row => row.Tags)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return
        [
            .. allTags
                .Select(tag => new TagSummary(tag.Name, counts.GetValueOrDefault(tag.Name, 0)))
                .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
