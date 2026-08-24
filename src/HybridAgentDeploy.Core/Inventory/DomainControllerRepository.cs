using HybridAgentDeploy.Core.Models;
using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Reads and writes the <c>domain_controller</c> table.
/// </summary>
/// <remarks>
/// Enumeration and import are additive and non-destructive (PRD 6.2). Nothing in this
/// repository deletes a DC row: a controller that disappears from Active Directory is
/// deactivated, keeping its tags and its deployment history intact.
/// </remarks>
public sealed class DomainControllerRepository
{
    private const string SelectColumns =
        "id, fqdn, netbios_name, domain, site_name, os_version, is_read_only, " +
        "is_global_catalog, source, first_seen_utc, last_seen_utc, is_active";

    private readonly SqliteConnectionFactory _connections;

    public DomainControllerRepository(SqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Inserts a domain controller, or refreshes an existing one matched on FQDN.
    /// </summary>
    /// <remarks>
    /// Re-importing or re-enumerating a known host updates <c>last_seen_utc</c> and the
    /// discovered attributes without duplicating the row (PRD 6.2). <c>first_seen_utc</c>
    /// and <c>source</c> are preserved from the original sighting: how a DC first entered
    /// the inventory is a fact about the past and re-enumeration does not change it.
    /// A row previously deactivated is reactivated by being seen again.
    /// </remarks>
    /// <returns>The row id, whether newly inserted or pre-existing.</returns>
    public async Task<long> UpsertAsync(DomainControllerRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO domain_controller " +
            "  (fqdn, netbios_name, domain, site_name, os_version, is_read_only, " +
            "   is_global_catalog, source, first_seen_utc, last_seen_utc, is_active) " +
            "VALUES ($fqdn, $netbios, $domain, $site, $os, $rodc, $gc, $source, $first, $last, 1) " +
            "ON CONFLICT(fqdn) DO UPDATE SET " +
            "  netbios_name      = COALESCE(excluded.netbios_name, domain_controller.netbios_name), " +
            "  domain            = COALESCE(excluded.domain, domain_controller.domain), " +
            "  site_name         = COALESCE(excluded.site_name, domain_controller.site_name), " +
            "  os_version        = COALESCE(excluded.os_version, domain_controller.os_version), " +
            "  is_read_only      = excluded.is_read_only, " +
            "  is_global_catalog = excluded.is_global_catalog, " +
            "  last_seen_utc     = excluded.last_seen_utc, " +
            "  is_active         = 1 " +
            "RETURNING id;";

        command.Parameters.AddWithValue("$fqdn", record.Fqdn);
        command.Parameters.AddWithValue("$netbios", (object?)record.NetbiosName ?? DBNull.Value);
        command.Parameters.AddWithValue("$domain", (object?)record.Domain ?? DBNull.Value);
        command.Parameters.AddWithValue("$site", (object?)record.SiteName ?? DBNull.Value);
        command.Parameters.AddWithValue("$os", (object?)record.OsVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$rodc", record.IsReadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$gc", record.IsGlobalCatalog ? 1 : 0);
        command.Parameters.AddWithValue("$source", DbEnums.ToDb(record.Source));
        command.Parameters.AddWithValue("$first", record.FirstSeenUtc);
        command.Parameters.AddWithValue("$last", record.LastSeenUtc);

        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        record.Id = id;
        return id;
    }

    public async Task<DomainControllerRecord?> GetByFqdnAsync(string fqdn, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM domain_controller WHERE fqdn = $fqdn COLLATE NOCASE;";
        command.Parameters.AddWithValue("$fqdn", fqdn);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<DomainControllerRecord?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM domain_controller WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <param name="includeInactive">
    /// Deactivated DCs are excluded by default. History screens pass true, because a DC
    /// that has left the forest still has deployment history worth reading.
    /// </param>
    public async Task<IReadOnlyList<DomainControllerRecord>> GetAllAsync(
        bool includeInactive,
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM domain_controller " +
            (includeInactive ? string.Empty : "WHERE is_active = 1 ") +
            "ORDER BY fqdn COLLATE NOCASE;";

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DomainControllerRecord>> GetBySiteAsync(
        string siteName,
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM domain_controller " +
            "WHERE is_active = 1 AND site_name = $site COLLATE NOCASE " +
            "ORDER BY fqdn COLLATE NOCASE;";
        command.Parameters.AddWithValue("$site", siteName);

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks every active DC previously seen in Active Directory that was absent from this
    /// enumeration as inactive.
    /// </summary>
    /// <remarks>
    /// Scoped to <c>source = 'ad_enumeration'</c> so that hosts an operator imported from a
    /// file are never deactivated by an AD enumeration that legitimately does not contain
    /// them. Returns the FQDNs that were deactivated so the operator can be told which
    /// controllers disappeared rather than having to notice.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DeactivateEnumeratedExceptAsync(
        IEnumerable<string> seenFqdns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seenFqdns);

        var seen = new HashSet<string>(seenFqdns, StringComparer.OrdinalIgnoreCase);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var candidates = new List<(long Id, string Fqdn)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT id, fqdn FROM domain_controller " +
                "WHERE is_active = 1 AND source = $source;";
            select.Parameters.AddWithValue("$source", DbEnums.ToDb(DiscoverySource.AdEnumeration));

            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var deactivated = candidates.Where(c => !seen.Contains(c.Fqdn)).ToList();

        foreach (var (id, _) in deactivated)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE domain_controller SET is_active = 0 WHERE id = $id;";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return deactivated.Select(d => d.Fqdn).ToList();
    }

    /// <summary>
    /// How current the inventory is, for the staleness warning shown before a deployment and
    /// recorded in the run log.
    /// </summary>
    /// <remarks>
    /// The most recent <c>last_seen_utc</c> across AD-sourced rows is when enumeration last
    /// ran, so no separate bookkeeping table is needed. Scoped to <c>ad_enumeration</c>
    /// deliberately: only the directory can tell us a domain controller still exists.
    /// </remarks>
    public async Task<InventoryStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), MAX(CASE WHEN source = $source THEN last_seen_utc END) " +
            "FROM domain_controller WHERE is_active = 1;";
        command.Parameters.AddWithValue("$source", DbEnums.ToDb(DiscoverySource.AdEnumeration));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new InventoryStatus(0, null);
        }

        return new InventoryStatus(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : UtcTimestamp.Parse(reader.GetString(1)));
    }

    /// <summary>
    /// The <c>dc_last_deployment</c> view (PRD 6.1), keyed by DC id. DCs that have never
    /// been deployed to are absent from the result rather than present with nulls.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, DcLastDeployment>> GetLastDeploymentsAsync(
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dc_id, fqdn, msi_product_version, org_id, completed_utc, outcome, " +
            "       exit_code, msi_log_path " +
            "FROM dc_last_deployment;";

        var results = new Dictionary<long, DcLastDeployment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dcId = reader.GetInt64(0);
            results[dcId] = new DcLastDeployment
            {
                DcId = dcId,
                Fqdn = reader.GetString(1),
                MsiProductVersion = reader.GetString(2),
                OrgId = reader.GetString(3),
                CompletedUtc = reader.IsDBNull(4) ? null : reader.GetString(4),
                Outcome = DbEnums.ToOutcome(reader.GetString(5)),
                ExitCode = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                MsiLogPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
        }

        return results;
    }

    private static async Task<IReadOnlyList<DomainControllerRecord>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken ct)
    {
        var results = new List<DomainControllerRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static DomainControllerRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Fqdn = reader.GetString(1),
        NetbiosName = reader.IsDBNull(2) ? null : reader.GetString(2),
        Domain = reader.IsDBNull(3) ? null : reader.GetString(3),
        SiteName = reader.IsDBNull(4) ? null : reader.GetString(4),
        OsVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
        IsReadOnly = reader.GetInt32(6) != 0,
        IsGlobalCatalog = reader.GetInt32(7) != 0,
        Source = DbEnums.ToSource(reader.GetString(8)),
        FirstSeenUtc = reader.GetString(9),
        LastSeenUtc = reader.GetString(10),
        IsActive = reader.GetInt32(11) != 0,
    };
}
