using HybridAgentDeploy.Core.Models;
using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Reads and writes tags and their many-to-many association with domain controllers
/// (PRD 6, 8.2).
/// </summary>
/// <remarks>
/// Tag names are unique case-insensitively, so "Pilot" and "pilot" are the same tag. That
/// is what an operator expects, and it prevents two near-identical tags from silently
/// splitting a deployment target set in half.
/// </remarks>
public sealed class TagRepository
{
    private readonly SqliteConnectionFactory _connections;

    public TagRepository(SqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Returns the existing tag with this name, or creates it.
    /// </summary>
    /// <remarks>
    /// Get-or-create rather than create-or-throw because the primary caller is file import
    /// with <c>--tag</c>, where the operator means "put these hosts in this group" and
    /// should not have to know or care whether the group already exists (PRD 6.2).
    /// </remarks>
    public async Task<TagRecord> GetOrCreateAsync(
        string name,
        string? description,
        CancellationToken ct)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A tag name is required.", nameof(name));
        }

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO tag (name, description, created_utc) VALUES ($name, $desc, $created) " +
            "ON CONFLICT(name) DO UPDATE SET " +
            "  description = COALESCE(excluded.description, tag.description) " +
            "RETURNING id, name, description, created_utc;";
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", UtcTimestamp.Now());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Map(reader);
    }

    public async Task<IReadOnlyList<TagRecord>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, description, created_utc FROM tag ORDER BY name COLLATE NOCASE;";

        var results = new List<TagRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<TagRecord?> GetByNameAsync(string name, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, description, created_utc FROM tag WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task RenameAsync(long tagId, string newName, CancellationToken ct)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A tag name is required.", nameof(newName));
        }

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tag SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$id", tagId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a tag and its associations.
    /// </summary>
    /// <remarks>
    /// The <c>dc_tag</c> cascade removes the associations only. No domain controller row
    /// and no deployment history is touched — PRD 8.2 is explicit about this, because a
    /// tag is a saved selection, not an owner of the hosts in it.
    /// </remarks>
    public async Task DeleteAsync(long tagId, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tag WHERE id = $id;";
        command.Parameters.AddWithValue("$id", tagId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one tag to many DCs in a single transaction. Re-applying an existing
    /// association is a no-op rather than an error.
    /// </summary>
    public async Task ApplyTagAsync(long tagId, IEnumerable<long> dcIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dcIds);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var appliedUtc = UtcTimestamp.Now();
        foreach (var dcId in dcIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO dc_tag (dc_id, tag_id, applied_utc) VALUES ($dc, $tag, $applied) " +
                "ON CONFLICT(dc_id, tag_id) DO NOTHING;";
            command.Parameters.AddWithValue("$dc", dcId);
            command.Parameters.AddWithValue("$tag", tagId);
            command.Parameters.AddWithValue("$applied", appliedUtc);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveTagAsync(long tagId, IEnumerable<long> dcIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dcIds);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        foreach (var dcId in dcIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM dc_tag WHERE dc_id = $dc AND tag_id = $tag;";
            command.Parameters.AddWithValue("$dc", dcId);
            command.Parameters.AddWithValue("$tag", tagId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every DC id carrying the named tag. The primary way a saved target group is turned
    /// back into a target list weeks later (PRD G3).
    /// </summary>
    public async Task<IReadOnlyList<long>> GetDcIdsWithTagAsync(string tagName, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dt.dc_id FROM dc_tag dt " +
            "JOIN tag t ON t.id = dt.tag_id " +
            "JOIN domain_controller dc ON dc.id = dt.dc_id " +
            "WHERE t.name = $name COLLATE NOCASE AND dc.is_active = 1 " +
            "ORDER BY dc.fqdn COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", tagName);

        var results = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(reader.GetInt64(0));
        }

        return results;
    }

    /// <summary>
    /// Tag names per DC id, for the Tags column of the inventory grid (PRD 8.1). Loaded in
    /// one query rather than per row: NFR3 allows one second to render 500 DCs.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> GetTagsByDcAsync(
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dt.dc_id, t.name FROM dc_tag dt " +
            "JOIN tag t ON t.id = dt.tag_id " +
            "ORDER BY dt.dc_id, t.name COLLATE NOCASE;";

        var results = new Dictionary<long, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dcId = reader.GetInt64(0);
            if (!results.TryGetValue(dcId, out var names))
            {
                names = [];
                results[dcId] = names;
            }

            names.Add(reader.GetString(1));
        }

        return results.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value);
    }

    private static TagRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedUtc = reader.GetString(3),
    };
}
