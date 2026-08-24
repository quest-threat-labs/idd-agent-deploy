using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Opens connections to the inventory database with the settings every caller must have.
/// </summary>
/// <remarks>
/// WAL mode and transactional writes are what let the database survive an abrupt process
/// kill without corruption (NFR6). Foreign keys are off by default in SQLite and must be
/// enabled per connection, or the ON DELETE CASCADE clauses in the schema are inert.
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private int _journalModeConfigured;

    public SqliteConnectionFactory(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Opens a connection with foreign keys enforced. The first connection also sets WAL,
    /// which is a persistent property of the database file rather than of the connection.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (Interlocked.Exchange(ref _journalModeConfigured, 1) == 0)
            {
                await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
