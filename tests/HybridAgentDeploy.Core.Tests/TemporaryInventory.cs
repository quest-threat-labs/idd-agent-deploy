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

        // Pooling off so disposing releases the file and the directory can be deleted, without
        // the process-wide ClearAllPools that raced other test classes running in parallel.
        var connections = new SqliteConnectionFactory(Path.Combine(directory, "inventory.db"), pooling: false);
        await new SchemaMigrator(connections).MigrateAsync(ct);

        return new TemporaryInventory(directory, connections);
    }

    public async ValueTask DisposeAsync()
    {
        // No ClearAllPools here. It is process-wide, and with xUnit running test classes in
        // parallel it pulled pooled connections out from under other classes mid-test — an
        // intermittent failure that surfaced in whichever unrelated test happened to be
        // running. This factory disables pooling instead, so closing releases the file.
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
