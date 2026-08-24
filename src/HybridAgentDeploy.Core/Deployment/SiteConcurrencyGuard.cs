namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Limits how many domain controllers in one Active Directory site are deployed to at once
/// (PRD R7.2).
/// </summary>
/// <remarks>
/// <para>
/// Never occupies more than half the active DCs in a site simultaneously, rounded down, with
/// a floor of one. In a two-DC site that means one at a time. With a global cap of five this
/// rarely binds, but in small sites it stops a bad MSI from touching both controllers at once
/// and taking the site's authentication capacity with it.
/// </para>
/// <para>
/// The denominator is the count of active DCs recorded in that site, not the count selected
/// for this run. The requirement protects the site's surviving capacity, which depends on how
/// many controllers the site has rather than on how many the operator happened to tick.
/// </para>
/// <para>
/// Targets with no recorded site — every host that arrived by file import, since importing
/// never contacts Active Directory — share a single bucket and are therefore deployed one at
/// a time. This is the conservative reading: a site-less target might be the second of two
/// controllers in a small site and there is no way to tell from the inventory. The cost is
/// real and the run log states it plainly, because an import-only deployment running at
/// concurrency one otherwise looks like a hang.
/// </para>
/// <para>
/// Acquired <em>after</em> the global semaphore, as R7.2 specifies: a secondary gate, not a
/// replacement for the ceiling of five.
/// </para>
/// </remarks>
public sealed class SiteConcurrencyGuard
{
    /// <summary>
    /// Bucket key for targets with no known site. Not a real site name; a real site could
    /// never be named this, since AD site names cannot contain spaces or angle brackets.
    /// </summary>
    public const string UnknownSiteKey = "<no site recorded>";

    private readonly Dictionary<string, SemaphoreSlim> _gates;
    private readonly Dictionary<string, int> _limits;

    /// <summary>
    /// Builds the guard from the active DC population per site.
    /// </summary>
    /// <param name="activeDcsPerSite">
    /// Site name to count of active DCs in the inventory for that site. Sites absent from
    /// this map are treated as having a single DC, which yields a limit of one — the safe
    /// direction to be wrong in.
    /// </param>
    public SiteConcurrencyGuard(IReadOnlyDictionary<string, int> activeDcsPerSite)
    {
        ArgumentNullException.ThrowIfNull(activeDcsPerSite);

        _limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _gates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        foreach (var (site, count) in activeDcsPerSite)
        {
            var limit = DeploymentLimits.SiteConcurrencyLimit(count);
            _limits[site] = limit;
            _gates[site] = new SemaphoreSlim(limit, limit);
        }

        if (!_gates.ContainsKey(UnknownSiteKey))
        {
            // One at a time for anything whose site is unknown. See the class remarks.
            _limits[UnknownSiteKey] = 1;
            _gates[UnknownSiteKey] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>The effective concurrency limit for a site, for the run log and for tests.</summary>
    public int LimitFor(string? siteName) => _limits.GetValueOrDefault(KeyFor(siteName), 1);

    /// <summary>True when this site's targets will be deployed strictly one at a time.</summary>
    public bool IsSerialised(string? siteName) => LimitFor(siteName) == 1;

    /// <summary>
    /// Waits for a slot in the target's site and returns a handle that releases it on
    /// dispose.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string? siteName, CancellationToken ct)
    {
        var key = KeyFor(siteName);
        var gate = GateFor(key);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SiteSlot(gate);
    }

    private SemaphoreSlim GateFor(string key)
    {
        lock (_gates)
        {
            if (_gates.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // A site that was not in the inventory snapshot. Treat it as a single-DC site:
            // the guard exists to be cautious, and guessing high here would defeat it.
            _limits[key] = 1;
            var gate = new SemaphoreSlim(1, 1);
            _gates[key] = gate;
            return gate;
        }
    }

    private static string KeyFor(string? siteName) =>
        string.IsNullOrWhiteSpace(siteName) ? UnknownSiteKey : siteName.Trim();

    private sealed class SiteSlot(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
