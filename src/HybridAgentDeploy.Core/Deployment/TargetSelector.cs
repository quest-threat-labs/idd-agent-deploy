using HybridAgentDeploy.Core.Inventory;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// The resolved target list for a run.
/// </summary>
/// <param name="ActiveDcsPerSite">
/// Active DCs per site across the whole inventory, for the R7.2 guard. Counts the inventory
/// rather than the selection: the requirement protects a site's surviving capacity, which
/// depends on how many controllers the site has, not on how many the operator ticked.
/// </param>
/// <param name="UnknownTags">
/// Tags named that do not exist. Reported rather than ignored — a mistyped tag in a scheduled
/// script would otherwise silently resolve to an empty selection.
/// </param>
/// <param name="UnknownHosts">
/// Hosts named that are not in the inventory. Same reasoning, and the same remedy: enumerate.
/// </param>
public sealed record TargetSelection(
    IReadOnlyList<DeploymentTarget> Targets,
    IReadOnlyDictionary<string, int> ActiveDcsPerSite,
    IReadOnlyList<string> UnknownTags,
    IReadOnlyList<string> UnknownHosts);

/// <summary>
/// Turns an operator's selection — by tag, by host, or everything — into deployment targets.
/// </summary>
/// <remarks>
/// Lives in Core rather than the CLI so the GUI resolves selections through the same code.
/// A tag that resolves to a different set depending on which interface asked would be a
/// particularly unpleasant defect on Tier 0.
/// </remarks>
public sealed class TargetSelector
{
    private readonly DomainControllerRepository _domainControllers;
    private readonly TagRepository _tags;

    public TargetSelector(DomainControllerRepository domainControllers, TagRepository tags)
    {
        _domainControllers = domainControllers ?? throw new ArgumentNullException(nameof(domainControllers));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
    }

    /// <summary>
    /// Resolves a selection. Only active domain controllers are ever returned: a DC that has
    /// left the forest keeps its history but is not a deployment target.
    /// </summary>
    public async Task<TargetSelection> ResolveAsync(
        IReadOnlyList<string> tagNames,
        IReadOnlyList<string> hosts,
        bool all,
        CancellationToken ct)
    {
        var active = await _domainControllers.GetAllAsync(includeInactive: false, ct).ConfigureAwait(false);

        var perSite = active
            .GroupBy(dc => string.IsNullOrWhiteSpace(dc.SiteName)
                ? SiteConcurrencyGuard.UnknownSiteKey
                : dc.SiteName!,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var byId = active.ToDictionary(dc => dc.Id);
        var selected = new Dictionary<long, DeploymentTarget>();
        var unknownTags = new List<string>();
        var unknownHosts = new List<string>();

        if (all)
        {
            foreach (var dc in active)
            {
                selected[dc.Id] = DeploymentTarget.From(dc);
            }
        }

        foreach (var tagName in tagNames)
        {
            ct.ThrowIfCancellationRequested();

            if (await _tags.GetByNameAsync(tagName, ct).ConfigureAwait(false) is null)
            {
                unknownTags.Add(tagName);
                continue;
            }

            foreach (var dcId in await _tags.GetDcIdsWithTagAsync(tagName, ct).ConfigureAwait(false))
            {
                if (byId.TryGetValue(dcId, out var dc))
                {
                    selected[dc.Id] = DeploymentTarget.From(dc);
                }
            }
        }

        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();

            var match = active.FirstOrDefault(dc =>
                string.Equals(dc.Fqdn, host, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dc.NetbiosName, host, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                unknownHosts.Add(host);
                continue;
            }

            selected[match.Id] = DeploymentTarget.From(match);
        }

        // Deduplicated by id, so overlapping tags or a host also covered by a tag select the
        // DC once. Ordered by name so a run is reproducible and its log reads predictably.
        var targets = selected.Values
            .OrderBy(t => t.Fqdn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TargetSelection(targets, perSite, unknownTags, unknownHosts);
    }
}
