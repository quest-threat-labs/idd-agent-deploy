using System.Security;
using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Logging;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;
using Microsoft.Extensions.Logging;

namespace HybridAgentDeploy.Cli;

/// <summary>
/// The composition root: configuration, the migrated database, and the repositories.
/// </summary>
/// <remarks>
/// Built once per command invocation. The schema is migrated on every start, which is safe
/// and idempotent, and means an operator never has to think about database versions.
/// </remarks>
internal sealed class AppServices : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    private AppServices(
        AppConfiguration configuration,
        SqliteConnectionFactory connections,
        ILoggerFactory loggerFactory)
    {
        Configuration = configuration;
        Connections = connections;
        _loggerFactory = loggerFactory;

        DomainControllers = new DomainControllerRepository(connections);
        Tags = new TagRepository(connections);
        Deployments = new DeploymentRepository(connections);
        TargetSelector = new TargetSelector(DomainControllers, Tags);
        Resolver = new DnsHostResolver();
        MsiInspector = new MsiInspector();
    }

    public AppConfiguration Configuration { get; }

    public SqliteConnectionFactory Connections { get; }

    public DomainControllerRepository DomainControllers { get; }

    public TagRepository Tags { get; }

    public DeploymentRepository Deployments { get; }

    public TargetSelector TargetSelector { get; }

    public IHostResolver Resolver { get; }

    public IMsiInspector MsiInspector { get; }

    public ILogger<T> Logger<T>() => _loggerFactory.CreateLogger<T>();

    public static async Task<AppServices> CreateAsync(string? databasePathOverride, CancellationToken ct)
    {
        var configuration = AppConfigurationLoader.Load();
        var databasePath = databasePathOverride ?? configuration.DatabasePath;

        var connections = new SqliteConnectionFactory(databasePath);

        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new FileLoggerProvider(new FileLoggerOptions
            {
                // The utility's own diagnostic log, distinct from the per-run run.log of
                // PRD 12. It never goes to the console: stdout belongs to command output and
                // stderr to operator-facing diagnostics.
                FilePath = Path.Combine(configuration.LogRootPath, "hybridagentdeploy.log"),
                MinimumLevel = LogLevel.Information,
            })));

        await new SchemaMigrator(connections, loggerFactory.CreateLogger<SchemaMigrator>())
            .MigrateAsync(ct)
            .ConfigureAwait(false);

        return new AppServices(configuration, connections, loggerFactory);
    }

    public ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Collects an alternate credential without ever letting the password reach the command line.
/// </summary>
/// <remarks>
/// <para>
/// SEC1 and PRD 12.3. A password passed as an argument is visible in process listings, in
/// shell history, and in any transcript of the script that ran it — so there is deliberately
/// no <c>--password</c> option, and adding one would be a security regression.
/// </para>
/// <para>
/// The password is read from the console with echo suppressed when one is attached, and from
/// standard input when it is redirected, so scheduled and piped invocations still work.
/// </para>
/// </remarks>
internal static class CredentialPrompt
{
    public static OperatorCredential? Collect(string? userName, Output output)
    {
        // SEC2: no --user means the operator's current Windows identity, which is the default.
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var account = userName.Trim();
        var password = Console.IsInputRedirected ? ReadFromStdin() : ReadFromConsole(account, output);

        if (password.Length == 0)
        {
            throw new CommandFailedException(
                ExitCode.InvalidArgumentsOrPreflight,
                $"No password was supplied for '{account}'. Provide it when prompted, or pipe it " +
                "on standard input for unattended use.");
        }

        password.MakeReadOnly();
        return new OperatorCredential(account, password);
    }

    private static SecureString ReadFromStdin()
    {
        var secure = new SecureString();

        // One line, so a password file or a piped secret works without the caller having to
        // strip a trailing newline.
        var line = Console.In.ReadLine() ?? string.Empty;
        foreach (var c in line)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private static SecureString ReadFromConsole(string account, Output output)
    {
        // The prompt goes to stderr so that stdout stays clean even here.
        output.Diagnostic($"Password for {account}: ");

        var secure = new SecureString();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                output.Diagnostic(string.Empty);
                return secure;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secure.Length > 0)
                {
                    secure.RemoveAt(secure.Length - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                secure.AppendChar(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// Aborts a command with a specific exit code and an operator-facing message.
/// </summary>
/// <remarks>
/// Thrown rather than returned so that a failure deep in a command cannot be accidentally
/// ignored by a caller that forgot to check a result.
/// </remarks>
internal sealed class CommandFailedException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
