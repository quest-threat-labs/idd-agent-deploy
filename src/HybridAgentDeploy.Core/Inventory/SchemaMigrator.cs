using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Brings an inventory database up to the current schema version.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are embedded SQL resources named <c>NNN_description.sql</c> and are applied
/// in ascending numeric order, each inside its own transaction. They are forward-only: a
/// migration that has shipped is never edited, only superseded.
/// </para>
/// <para>
/// Deliberately hand-rolled rather than taken from a migration library. This is a small,
/// fixed set of scripts, and a tool destined for hardened environments benefits from every
/// third-party dependency it does not carry.
/// </para>
/// </remarks>
public sealed class SchemaMigrator
{
    private const string ResourcePrefix = "HybridAgentDeploy.Core.Inventory.Migrations.";

    private readonly SqliteConnectionFactory _connections;
    private readonly ILogger<SchemaMigrator> _log;

    public SchemaMigrator(SqliteConnectionFactory connections, ILogger<SchemaMigrator>? log = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _log = log ?? NullLogger<SchemaMigrator>.Instance;
    }

    /// <summary>The highest migration version embedded in this build.</summary>
    public static int TargetVersion => DiscoverMigrations().Max(m => m.Version);

    /// <summary>
    /// Applies every migration the database has not yet seen. Safe to call on every start;
    /// a database already at the target version is left untouched.
    /// </summary>
    /// <returns>The version the database is at once this returns.</returns>
    public Task<int> MigrateAsync(CancellationToken ct) => MigrateToAsync(int.MaxValue, ct);

    /// <summary>
    /// Applies migrations up to and including a given version, and no further.
    /// </summary>
    /// <remarks>
    /// Exists so a test can build the database an <em>earlier</em> build of this tool would
    /// have written, put data in it, and then upgrade — which is the case that matters in the
    /// field and that migrating from empty does not exercise. Not used by the application,
    /// which always migrates to the latest.
    /// </remarks>
    public async Task<int> MigrateToAsync(int targetVersion, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);

        await EnsureVersionTableAsync(connection, ct).ConfigureAwait(false);
        var current = await ReadCurrentVersionAsync(connection, ct).ConfigureAwait(false);

        var pending = DiscoverMigrations()
            .Where(m => m.Version > current && m.Version <= targetVersion)
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            _log.LogDebug("Inventory schema at version {Version}; no migrations to apply.", current);
            return current;
        }

        foreach (var migration in pending)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyAsync(connection, migration, ct).ConfigureAwait(false);
            current = migration.Version;
            _log.LogInformation(
                "Applied inventory schema migration {Version} ({Name}).",
                migration.Version,
                migration.Name);
        }

        return current;
    }

    public async Task<int> GetCurrentVersionAsync(CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await EnsureVersionTableAsync(connection, ct).ConfigureAwait(false);
        return await ReadCurrentVersionAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS schema_version (" +
            "    version     INTEGER PRIMARY KEY," +
            "    name        TEXT NOT NULL," +
            "    applied_utc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyAsync(
        SqliteConnection connection,
        Migration migration,
        CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
            await using (var script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                script.CommandText = migration.Sql;
                await script.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_version (version, name, applied_utc) VALUES ($v, $n, $a);";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$n", migration.Name);
                record.Parameters.AddWithValue("$a", UtcTimestamp.Now());
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Inventory schema migration {migration.Version} ({migration.Name}) failed and was " +
                $"rolled back; the database remains usable at its previous version. {ex.Message}",
                ex);
        }
    }

    private static List<Migration> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = resource.Substring(ResourcePrefix.Length);
            var separator = fileName.IndexOf('_');
            if (separator <= 0 || !int.TryParse(
                    fileName.Substring(0, separator),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version))
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{fileName}' is not named NNN_description.sql. " +
                    "Migration ordering depends on that numeric prefix.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Embedded migration '{resource}' could not be read from the assembly.");
            using var reader = new StreamReader(stream);

            migrations.Add(new Migration(
                version,
                Path.GetFileNameWithoutExtension(fileName),
                reader.ReadToEnd()));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException(
                "No schema migrations are embedded in HybridAgentDeploy.Core. Confirm the " +
                "EmbeddedResource item group covering Inventory/Migrations/*.sql is present " +
                "in the project file.");
        }

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two embedded migrations share version {duplicate.Key}: " +
                string.Join(", ", duplicate.Select(m => m.Name)) + ".");
        }

        return migrations;
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
