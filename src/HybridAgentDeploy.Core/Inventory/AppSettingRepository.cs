using HybridAgentDeploy.Core;
using HybridAgentDeploy.Core.Deployment;
using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// The handful of values the tool remembers between sessions.
/// </summary>
/// <remarks>
/// <para>
/// Kept in the inventory database rather than in a settings file, so that a portable
/// installation carries its remembered values with it (NFR4), and so an operator working two
/// forests out of two folders never gets one forest's value prefilled into the other's.
/// </para>
/// <para>
/// Nothing secret is stored here. SEC1 forbids persisting credential material anywhere, and
/// this changes nothing about that: the Org ID is a tenant identifier already recorded in
/// <c>deployment_run</c> and written into every run log.
/// </para>
/// </remarks>
public sealed class AppSettingRepository
{
    private readonly SqliteConnectionFactory _connections;

    public AppSettingRepository(SqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// The key under which the last Org ID for a given mode is remembered.
    /// </summary>
    /// <remarks>
    /// One value per mode, not one overall. The two modes take different kinds of identifier —
    /// a tenant GUID for Identity Defense, a short installation name such as <c>DEFAULT</c> for
    /// Change Auditor — so a single remembered value would prefill the wrong kind of thing
    /// every time the operator switched the cloud-mode checkbox, and the mismatch warning would
    /// fire on a value the tool itself had just supplied.
    /// </remarks>
    public static string OrgIdKey(AgentMode mode) => $"orgId.{mode}";

    /// <summary>Reads a remembered value, or null when nothing has been stored under that key.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    /// <summary>
    /// Stores a value, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// A null or blank value removes the key rather than storing emptiness, so that "the
    /// operator cleared the box" and "nothing has ever been stored" are the same state on the
    /// next read instead of two states behaving differently.
    /// </remarks>
    public async Task SetAsync(string key, string? value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(value))
        {
            command.CommandText = "DELETE FROM app_setting WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
        }
        else
        {
            command.CommandText =
                """
                INSERT INTO app_setting (key, value, updated_utc)
                VALUES ($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
                """;

            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value.Trim());
            command.Parameters.AddWithValue("$now", UtcTimestamp.Now());
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Convenience wrapper: the last Org ID used in the given mode.</summary>
    public Task<string?> GetLastOrgIdAsync(AgentMode mode, CancellationToken ct) =>
        GetAsync(OrgIdKey(mode), ct);

    /// <summary>
    /// Remembers an Org ID against the mode it was used in.
    /// </summary>
    /// <remarks>
    /// Called when a run starts rather than when one succeeds. A deployment that fails is
    /// exactly the case where the operator is about to try again, and losing what they typed
    /// because the run went badly would be the wrong way round.
    /// </remarks>
    public Task RememberOrgIdAsync(AgentMode mode, string? orgId, CancellationToken ct) =>
        SetAsync(OrgIdKey(mode), orgId, ct);
}
