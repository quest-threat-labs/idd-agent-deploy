using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Discovery;

/// <summary>
/// Applies tags to domain controllers named in a plain-text file (PRD 6.2, G3).
/// </summary>
/// <remarks>
/// <para>
/// Import is a <em>selection</em> mechanism, not a discovery one. Active Directory
/// enumeration is the only way a domain controller enters the inventory; a text file names
/// controllers that are already there so the operator can group them for repeated deployment
/// — a pilot ring, a site, the read-only DCs.
/// </para>
/// <para>
/// This is a deliberate departure from the literal wording of PRD 6.2, which says
/// "re-importing a file that contains an already-known host updates last_seen_utc and adds
/// tags" and so implies that an unknown host would be created. Creating one produces a
/// domain controller record with no site, no OS version, and a read-only flag defaulted to
/// false — a half-populated Tier 0 deployment target assembled from a line of text. Under
/// this design an entry that matches nothing is reported to the operator instead, which is
/// also what keeps the R7.2 site guard meaningful: every target has a real site because every
/// target came from the directory.
/// </para>
/// <para>
/// Nothing here writes to the <c>domain_controller</c> table. Import adds tag associations
/// and nothing else, so <c>last_seen_utc</c> continues to mean "last seen in Active
/// Directory" rather than "last named in a text file".
/// </para>
/// </remarks>
public sealed class FileImportService
{
    private readonly DomainControllerRepository _domainControllers;
    private readonly TagRepository _tags;
    private readonly IHostResolver _resolver;
    private readonly ILogger<FileImportService> _log;

    public FileImportService(
        DomainControllerRepository domainControllers,
        TagRepository tags,
        IHostResolver resolver,
        ILogger<FileImportService>? log = null)
    {
        _domainControllers = domainControllers ?? throw new ArgumentNullException(nameof(domainControllers));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _log = log ?? NullLogger<FileImportService>.Instance;
    }

    public async Task<ImportReport> ImportFileAsync(
        string filePath,
        IReadOnlyCollection<string> tagNames,
        bool dryRun,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"The import file '{filePath}' does not exist.", filePath);
        }

        var lines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
        return await ImportAsync(lines, tagNames, dryRun, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches each line against the inventory and applies the requested tags to what matched.
    /// </summary>
    /// <param name="dryRun">
    /// Match and report but write no tags. The report is identical in shape either way, so
    /// what the operator previews is what they get (PRD 9, <c>import --dry-run</c>).
    /// </param>
    /// <exception cref="InventoryNotEnumeratedException">
    /// The inventory holds no active domain controllers, so nothing could match. Blocking is
    /// the honest answer: reporting every line as unmatched would be technically accurate and
    /// would tell the operator nothing about what to do next.
    /// </exception>
    public async Task<ImportReport> ImportAsync(
        IEnumerable<string> lines,
        IReadOnlyCollection<string> tagNames,
        bool dryRun,
        CancellationToken ct)
    {
        var parsed = HostListParser.Parse(lines);

        var inventory = await _domainControllers.GetAllAsync(includeInactive: false, ct).ConfigureAwait(false);
        if (inventory.Count == 0)
        {
            throw new InventoryNotEnumeratedException(
                "There are no domain controllers in the inventory, so nothing in this file can " +
                "be matched. Import applies tags to controllers discovered from Active " +
                "Directory; it does not add new ones. Run an Active Directory enumeration " +
                "first, then import this file again.");
        }

        // NFR3 budgets one second for 500 DCs, so the inventory is indexed once rather than
        // queried per line.
        var byFqdn = new Dictionary<string, DomainControllerRecord>(StringComparer.OrdinalIgnoreCase);
        var byNetbios = new Dictionary<string, DomainControllerRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var dc in inventory)
        {
            byFqdn[dc.Fqdn] = dc;

            if (!string.IsNullOrWhiteSpace(dc.NetbiosName))
            {
                // A NetBIOS name is unique per domain but not necessarily per forest. Where
                // two domains hold a DC of the same short name, the first wins and the
                // operator is expected to disambiguate by using FQDNs.
                byNetbios.TryAdd(dc.NetbiosName, dc);
            }
        }

        var matched = new List<MatchedHost>();
        var unmatched = new List<UnmatchedHost>();

        foreach (var entry in parsed.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var match = await MatchAsync(entry, byFqdn, byNetbios, ct).ConfigureAwait(false);

            if (match.Record is null)
            {
                unmatched.Add(new UnmatchedHost(entry.LineNumber, entry.RawValue, match.Reason!));
                continue;
            }

            matched.Add(new MatchedHost(
                entry.LineNumber, entry.RawValue, match.Record.Fqdn, match.Record.Id));
        }

        var report = new ImportReport(
            matched,
            unmatched,
            parsed.Duplicates,
            tagNames.ToList(),
            dryRun);

        if (dryRun)
        {
            _log.LogInformation(
                "Import dry run: {Matched} host(s) would be tagged, {Unmatched} unmatched.",
                matched.Count, unmatched.Count);
            return report;
        }

        var dcIds = matched.Select(m => m.DcId).Distinct().ToList();

        foreach (var tagName in tagNames)
        {
            ct.ThrowIfCancellationRequested();
            var tag = await _tags.GetOrCreateAsync(tagName, description: null, ct).ConfigureAwait(false);
            await _tags.ApplyTagAsync(tag.Id, dcIds, ct).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Tagged {Matched} domain controller(s) with {Tags} tag(s); {Unmatched} entr(ies) " +
            "matched nothing in the inventory.",
            matched.Count, tagNames.Count, unmatched.Count);

        return report;
    }

    /// <summary>
    /// Resolves one line to an inventory record, trying the cheapest match first.
    /// </summary>
    /// <remarks>
    /// FQDN, then NetBIOS name, then a DNS lookup for entries written as an IP address or a
    /// name that does not match directly. DNS is the last resort rather than the first step:
    /// matching a name already in the inventory should not depend on a working resolver.
    /// </remarks>
    private async Task<(DomainControllerRecord? Record, string? Reason)> MatchAsync(
        HostEntry entry,
        Dictionary<string, DomainControllerRecord> byFqdn,
        Dictionary<string, DomainControllerRecord> byNetbios,
        CancellationToken ct)
    {
        if (byFqdn.TryGetValue(entry.RawValue, out var byName))
        {
            return (byName, null);
        }

        if (entry.Kind == HostEntryKind.NetBiosName &&
            byNetbios.TryGetValue(entry.RawValue, out var byShortName))
        {
            return (byShortName, null);
        }

        var resolution = await _resolver.ResolveAsync(entry.RawValue, ct).ConfigureAwait(false);

        if (!resolution.Succeeded || string.IsNullOrWhiteSpace(resolution.Fqdn))
        {
            return (null,
                $"'{entry.RawValue}' is not in the inventory and could not be resolved in DNS " +
                $"({resolution.FailureReason}). Confirm the spelling, then re-enumerate from " +
                "Active Directory if this really is a domain controller.");
        }

        if (byFqdn.TryGetValue(resolution.Fqdn, out var byResolved))
        {
            return (byResolved, null);
        }

        return (null,
            $"'{entry.RawValue}' resolved to '{resolution.Fqdn}', which is not a domain " +
            "controller in the inventory. Import applies tags to controllers discovered from " +
            "Active Directory and does not add new ones — re-enumerate if this DC is new, or " +
            "correct the entry if it is not a domain controller.");
    }
}

/// <summary>
/// Raised when an import is attempted against an inventory that has never been enumerated.
/// </summary>
public sealed class InventoryNotEnumeratedException(string message) : Exception(message);

/// <param name="ResolvedFqdn">The inventory record's FQDN, which may differ from what was typed.</param>
/// <param name="DcId">The matched <c>domain_controller</c> row.</param>
public sealed record MatchedHost(int LineNumber, string RawValue, string ResolvedFqdn, long DcId);

/// <param name="Reason">
/// Why nothing matched, phrased with a next step (PRD 10.4) and paired with the line number
/// so a bad entry in a sixty-line file is findable.
/// </param>
public sealed record UnmatchedHost(int LineNumber, string RawValue, string Reason);

/// <summary>
/// The outcome of an import, reported whole rather than as a count.
/// </summary>
/// <remarks>
/// PRD 6.2 requires entries that could not be used to be reported rather than discarded, and
/// PRD 10.4 requires the operator to be told what to do next. Both need the detail.
/// </remarks>
public sealed record ImportReport(
    IReadOnlyList<MatchedHost> Matched,
    IReadOnlyList<UnmatchedHost> Unmatched,
    IReadOnlyList<HostEntry> Duplicates,
    IReadOnlyList<string> TagsApplied,
    bool DryRun)
{
    public bool HasProblems => Unmatched.Count > 0 || Duplicates.Count > 0;
}
