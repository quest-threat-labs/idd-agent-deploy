using HybridAgentDeploy.Core.Inventory;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// A migrated inventory database in a throwaway directory, deleted on dispose.
/// </summary>
/// <remarks>
/// A real SQLite file rather than an in-memory database. The schema uses a view, partial
/// indexes, and ON DELETE CASCADE, and the point of these tests is to prove those behave as
/// written — against the same storage engine and journal mode the tool will actually use
/// (NFR6).
/// </remarks>
internal sealed class TemporaryInventory : IAsyncDisposable
{
    private readonly string _directory;

    private TemporaryInventory(string directory, SqliteConnectionFactory connections)
    {
        _directory = directory;
        Connections = connections;
        DomainControllers = new DomainControllerRepository(connections);
        Tags = new TagRepository(connections);
        Deployments = new DeploymentRepository(connections);
    }

    public SqliteConnectionFactory Connections { get; }

    public DomainControllerRepository DomainControllers { get; }

    public TagRepository Tags { get; }

    public DeploymentRepository Deployments { get; }

    public static async Task<TemporaryInventory> CreateAsync(CancellationToken ct = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var connections = new SqliteConnectionFactory(Path.Combine(directory, "inventory.db"));
        await new SchemaMigrator(connections).MigrateAsync(ct);

        return new TemporaryInventory(directory, connections);
    }

    public async ValueTask DisposeAsync()
    {
        // Release the pooled connections holding the file, or the delete fails on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Yield();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
