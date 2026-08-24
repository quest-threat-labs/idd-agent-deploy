using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Discovery;

/// <summary>
/// Imports a plain-text list of hosts into the inventory, optionally tagging every host in
/// the file in one operation (PRD 6.2).
/// </summary>
/// <remarks>
/// Applying tags during import is the primary use case the requirement describes: an
/// operator has a text file naming a pilot group and wants that group to still exist weeks
/// later when it is time to upgrade.
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
    /// Parses, resolves, and persists a host list.
    /// </summary>
    /// <param name="dryRun">
    /// Parse and resolve but write nothing. The report is identical in shape either way, so
    /// what the operator previews is what they get (PRD 9, <c>import --dry-run</c>).
    /// </param>
    public async Task<ImportReport> ImportAsync(
        IEnumerable<string> lines,
        IReadOnlyCollection<string> tagNames,
        bool dryRun,
        CancellationToken ct)
    {
        var parsed = HostListParser.Parse(lines);

        var imported = new List<ImportedHost>();
        var unresolved = new List<UnresolvedHost>();

        foreach (var entry in parsed.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var resolution = await _resolver.ResolveAsync(entry.RawValue, ct).ConfigureAwait(false);

            if (!resolution.Succeeded || string.IsNullOrWhiteSpace(resolution.Fqdn))
            {
                // PRD 6.2: report unresolvable entries rather than silently discarding them.
                // No inventory row is created — a host that cannot be resolved cannot be
                // deployed to, and a phantom row would show up in target lists as though it
                // could be.
                unresolved.Add(new UnresolvedHost(
                    entry.LineNumber,
                    entry.RawValue,
                    resolution.FailureReason ?? "The host could not be resolved to an FQDN."));
                continue;
            }

            imported.Add(new ImportedHost(entry.LineNumber, entry.RawValue, resolution.Fqdn));
        }

        if (dryRun)
        {
            _log.LogInformation(
                "Import dry run: {Resolved} host(s) would be imported, {Unresolved} unresolved.",
                imported.Count,
                unresolved.Count);

            return new ImportReport(imported, unresolved, parsed.Duplicates, tagNames.ToList(), DryRun: true);
        }

        var dcIds = new List<long>(imported.Count);
        foreach (var host in imported)
        {
            ct.ThrowIfCancellationRequested();

            // Re-importing a known host updates last_seen_utc and adds tags without
            // duplicating the row (PRD 6.2); the repository upsert handles that.
            var id = await _domainControllers.UpsertAsync(
                new DomainControllerRecord
                {
                    Fqdn = host.ResolvedFqdn,
                    NetbiosName = NetbiosFrom(host.ResolvedFqdn),
                    Domain = DomainFrom(host.ResolvedFqdn),
                    Source = DiscoverySource.FileImport,
                },
                ct).ConfigureAwait(false);

            dcIds.Add(id);
        }

        foreach (var tagName in tagNames)
        {
            ct.ThrowIfCancellationRequested();
            var tag = await _tags.GetOrCreateAsync(tagName, description: null, ct).ConfigureAwait(false);
            await _tags.ApplyTagAsync(tag.Id, dcIds, ct).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Imported {Resolved} host(s) with {Tags} tag(s); {Unresolved} entr(ies) did not resolve.",
            imported.Count,
            tagNames.Count,
            unresolved.Count);

        return new ImportReport(imported, unresolved, parsed.Duplicates, tagNames.ToList(), DryRun: false);
    }

    private static string? NetbiosFrom(string fqdn)
    {
        var dot = fqdn.IndexOf('.');
        return dot > 0 ? fqdn[..dot].ToUpperInvariant() : fqdn.ToUpperInvariant();
    }

    private static string? DomainFrom(string fqdn)
    {
        var dot = fqdn.IndexOf('.');
        return dot > 0 && dot < fqdn.Length - 1 ? fqdn[(dot + 1)..] : null;
    }
}

/// <param name="ResolvedFqdn">The FQDN written to the inventory.</param>
public sealed record ImportedHost(int LineNumber, string RawValue, string ResolvedFqdn);

/// <param name="Reason">
/// Shown to the operator with the line number, so a bad entry in a long file is findable.
/// </param>
public sealed record UnresolvedHost(int LineNumber, string RawValue, string Reason);

/// <summary>
/// The outcome of an import, reported to the operator whole rather than as a count.
/// </summary>
/// <remarks>
/// PRD 6.2 requires unresolvable entries to be reported rather than discarded, and PRD 10.4
/// requires the operator to be told what to do next. Both need the detail, not a total.
/// </remarks>
public sealed record ImportReport(
    IReadOnlyList<ImportedHost> Imported,
    IReadOnlyList<UnresolvedHost> Unresolved,
    IReadOnlyList<HostEntry> Duplicates,
    IReadOnlyList<string> TagsApplied,
    bool DryRun)
{
    public bool HasProblems => Unresolved.Count > 0 || Duplicates.Count > 0;
}
