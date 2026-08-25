using HybridAgentDeploy.Core.Inventory;
using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: schema migration from empty to current.
/// </summary>
public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task Migrating_an_empty_database_reaches_the_target_version()
    {
        var directory = CreateTempDirectory();
        try
        {
            var connections = new SqliteConnectionFactory(Path.Combine(directory, "inventory.db"), pooling: false);
            var migrator = new SchemaMigrator(connections);

            Assert.Equal(0, await migrator.GetCurrentVersionAsync(CancellationToken.None));

            var version = await migrator.MigrateAsync(CancellationToken.None);

            Assert.Equal(SchemaMigrator.TargetVersion, version);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        var directory = CreateTempDirectory();
        try
        {
            var connections = new SqliteConnectionFactory(Path.Combine(directory, "inventory.db"), pooling: false);
            var migrator = new SchemaMigrator(connections);

            var first = await migrator.MigrateAsync(CancellationToken.None);
            var second = await migrator.MigrateAsync(CancellationToken.None);

            Assert.Equal(first, second);

            // Startup calls MigrateAsync unconditionally, so re-running must not re-apply a
            // migration or record it twice.
            await using var connection = await connections.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_version;";
            var applied = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));

            Assert.Equal(SchemaMigrator.TargetVersion, applied);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Theory]
    [InlineData("domain_controller")]
    [InlineData("tag")]
    [InlineData("dc_tag")]
    [InlineData("deployment_run")]
    [InlineData("deployment_result")]
    [InlineData("schema_version")]
    public async Task Every_table_in_the_specification_exists(string tableName)
    {
        await using var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);

        await using var connection = await inventory.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var found = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task The_last_deployment_view_exists()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);

        await using var connection = await inventory.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'dc_last_deployment';";

        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None)));
    }

    /// <summary>
    /// NFR6: the database must survive an abrupt process kill without corruption. WAL is
    /// half of how that is achieved, and it is a property of the file, so it is worth
    /// asserting rather than assuming.
    /// </summary>
    [Fact]
    public async Task Write_ahead_logging_is_enabled()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);

        await using var connection = await inventory.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        var mode = (string?)await command.ExecuteScalarAsync(CancellationToken.None);
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    /// <summary>
    /// The ON DELETE CASCADE clauses in the schema are inert unless foreign keys are enabled
    /// on every connection, and SQLite leaves them off by default.
    /// </summary>
    [Fact]
    public async Task Foreign_keys_are_enforced_on_every_connection()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);

        await using var connection = await inventory.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";

        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task A_domain_controller_fqdn_is_unique_case_insensitively()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(CancellationToken.None);

        await using var connection = await inventory.Connections.OpenAsync(CancellationToken.None);

        await using var first = connection.CreateCommand();
        first.CommandText =
            "INSERT INTO domain_controller (fqdn, source, first_seen_utc, last_seen_utc) " +
            "VALUES ('DC01.corp.local', 'manual', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');";
        await first.ExecuteNonQueryAsync(CancellationToken.None);

        await using var second = connection.CreateCommand();
        second.CommandText =
            "INSERT INTO domain_controller (fqdn, source, first_seen_utc, last_seen_utc) " +
            "VALUES ('dc01.CORP.LOCAL', 'manual', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');";

        await Assert.ThrowsAsync<SqliteException>(
            () => second.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        // Deliberately no ClearAllPools: it is process-wide and races the other test classes
        // xUnit runs in parallel. These factories disable pooling instead.
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
