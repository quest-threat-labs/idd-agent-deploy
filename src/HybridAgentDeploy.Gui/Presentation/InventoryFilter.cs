using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>Last-outcome filter values for the inventory grid (PRD 8.1).</summary>
public enum OutcomeFilter
{
    Any,
    Success,
    RebootRequired,
    Failure,
    NeverDeployed,
}

/// <summary>
/// The inventory grid's filter settings (PRD 8.1): free text on FQDN, a domain, a tag, a
/// site, and the last outcome.
/// </summary>
/// <remarks>
/// An empty or null value means "do not filter on this". The criteria combine with AND, which
/// is what an operator narrowing a list expects.
/// </remarks>
public sealed record InventoryFilterCriteria
{
    public string? FqdnContains { get; init; }

    /// <summary>
    /// An exact domain name, as recorded by enumeration.
    /// </summary>
    /// <remarks>
    /// Distinct from typing the domain into <see cref="FqdnContains"/>, which matches any
    /// substring: in a forest with <c>corp.local</c> and <c>research.corp.local</c>, typing
    /// the parent domain's name matches every controller in both. An operator scoping a
    /// deployment to one domain needs the exact answer, not the one that happens to include
    /// the child.
    /// </remarks>
    public string? Domain { get; init; }

    public string? Tag { get; init; }
    public string? Site { get; init; }
    public OutcomeFilter Outcome { get; init; } = OutcomeFilter.Any;

    public static InventoryFilterCriteria None { get; } = new();

    public bool IsFiltering =>
        !string.IsNullOrWhiteSpace(FqdnContains) ||
        !string.IsNullOrWhiteSpace(Domain) ||
        !string.IsNullOrWhiteSpace(Tag) ||
        !string.IsNullOrWhiteSpace(Site) ||
        Outcome != OutcomeFilter.Any;
}

/// <summary>
/// Applies the inventory filters.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over rows, deliberately separate from the grid so it can be tested. This is
/// where subtle bugs hide: a filter that silently omits a domain controller would remove it
/// from the selection an operator then deploys, and nothing downstream would notice that the
/// DC was simply never offered.
/// </para>
/// <para>
/// Filtering never changes which rows are <em>selected</em>, only which are visible. Hiding a
/// checked row while leaving it checked would deploy to a DC the operator cannot see, so
/// callers must reconcile the selection against the visible set — see
/// <see cref="SelectionState"/>.
/// </para>
/// </remarks>
public static class InventoryFilter
{
    public static IReadOnlyList<InventoryRow> Apply(
        IEnumerable<InventoryRow> rows,
        InventoryFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(criteria);

        return [.. rows.Where(row => Matches(row, criteria))];
    }

    public static bool Matches(InventoryRow row, InventoryFilterCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.FqdnContains) &&
            !row.Fqdn.Contains(criteria.FqdnContains.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Domain) &&
            !string.Equals(row.Domain, criteria.Domain.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Tag) &&
            !row.Tags.Contains(criteria.Tag.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Site) &&
            !string.Equals(row.SiteName, criteria.Site.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesOutcome(row.LastOutcome, criteria.Outcome);
    }

    private static bool MatchesOutcome(DeploymentOutcome? outcome, OutcomeFilter filter) => filter switch
    {
        OutcomeFilter.Any => true,
        OutcomeFilter.NeverDeployed => outcome is null,
        OutcomeFilter.Success => outcome == DeploymentOutcome.Success,
        OutcomeFilter.RebootRequired => outcome == DeploymentOutcome.SuccessRebootRequired,

        // Everything that is not a success and not "never deployed" reads as a failure to an
        // operator scanning the grid — a timeout and a cancelled target are both things they
        // need to look at, and splitting them into separate filter entries would bury them.
        OutcomeFilter.Failure => outcome is not null && !outcome.Value.IsSuccess(),

        _ => true,
    };

    /// <summary>
    /// Distinct domain names present in the inventory, for the domain dropdown.
    /// </summary>
    /// <remarks>
    /// Sorted alphabetically rather than by forest hierarchy. A child domain therefore sits
    /// beside its parent — <c>corp.local</c> then <c>research.corp.local</c> — which is how an
    /// operator scans a list, and reconstructing the tree from names alone would be guesswork.
    /// </remarks>
    public static IReadOnlyList<string> DomainChoices(IEnumerable<InventoryRow> rows) =>
        [.. rows.Select(r => r.Domain)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Distinct site names present in the inventory, for the site dropdown.</summary>
    public static IReadOnlyList<string> SiteChoices(IEnumerable<InventoryRow> rows) =>
        [.. rows.Select(r => r.SiteName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Distinct tag names present in the inventory, for the tag dropdown.</summary>
    public static IReadOnlyList<string> TagChoices(IEnumerable<InventoryRow> rows) =>
        [.. rows.SelectMany(r => r.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Which domain controllers the operator has ticked, kept separately from what the grid is
/// currently showing.
/// </summary>
/// <remarks>
/// <para>
/// Selection is tracked by DC id rather than by grid row, so that changing a filter does not
/// silently drop ticks. That matters both ways round: an operator who ticks three DCs, filters
/// to a different site, and then deploys must not lose the original three — and equally must
/// not deploy to hosts they can no longer see without being told.
/// </para>
/// <para>
/// <see cref="HiddenSelectedCount"/> exists so the deployment screen can say plainly that the
/// selection includes domain controllers the current filter is hiding.
/// </para>
/// </remarks>
public sealed class SelectionState
{
    private readonly HashSet<long> _selected = [];

    public IReadOnlyCollection<long> SelectedIds => _selected;

    public int Count => _selected.Count;

    public bool IsSelected(long dcId) => _selected.Contains(dcId);

    public void Set(long dcId, bool selected)
    {
        if (selected)
        {
            _selected.Add(dcId);
        }
        else
        {
            _selected.Remove(dcId);
        }
    }

    /// <summary>Ticks every currently visible row, leaving hidden selections untouched.</summary>
    public void SelectAll(IEnumerable<InventoryRow> visible)
    {
        foreach (var row in visible)
        {
            _selected.Add(row.DcId);
        }
    }

    /// <summary>
    /// Unticks every currently visible row.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to what is visible. "Select none" clearing hidden selections too
    /// would be defensible, but an operator who has filtered to one site and clicks it expects
    /// to clear that site — not to silently discard work done under a previous filter.
    /// </remarks>
    public void SelectNone(IEnumerable<InventoryRow> visible)
    {
        foreach (var row in visible)
        {
            _selected.Remove(row.DcId);
        }
    }

    public void Clear() => _selected.Clear();

    /// <summary>
    /// Selected domain controllers that the current filter is hiding — the number the operator
    /// would deploy to without seeing.
    /// </summary>
    public int HiddenSelectedCount(IEnumerable<InventoryRow> visible)
    {
        var visibleIds = visible.Select(r => r.DcId).ToHashSet();
        return _selected.Count(id => !visibleIds.Contains(id));
    }

    /// <summary>
    /// Drops selections for DCs that no longer exist in the inventory, after a re-enumeration
    /// deactivates one.
    /// </summary>
    public void Prune(IEnumerable<InventoryRow> allRows)
    {
        var known = allRows.Select(r => r.DcId).ToHashSet();
        _selected.RemoveWhere(id => !known.Contains(id));
    }
}
