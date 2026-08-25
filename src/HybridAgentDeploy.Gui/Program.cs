using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Logging;
using Microsoft.Extensions.Logging;

namespace HybridAgentDeploy.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        AppConfiguration configuration;
        try
        {
            configuration = AppConfigurationLoader.Load();
        }
        catch (InvalidOperationException ex)
        {
            // A malformed config file is fatal rather than silently ignored: an operator who
            // wrote one meant it, and quietly using a different inventory database than the
            // one they configured would be worse than refusing to start.
            MessageBox.Show(
                ex.Message,
                "Configuration error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new FileLoggerProvider(new FileLoggerOptions
            {
                FilePath = Path.Combine(configuration.LogRootPath, "hybridagentdeploy.log"),
                MinimumLevel = LogLevel.Information,
            })));

        var connections = new SqliteConnectionFactory(configuration.DatabasePath);

        try
        {
            // Migration is idempotent and runs on every start, so an operator never has to
            // think about database versions.
            new SchemaMigrator(connections, loggerFactory.CreateLogger<SchemaMigrator>())
                .MigrateAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The inventory database at {configuration.DatabasePath} could not be opened or " +
                $"upgraded.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Inventory unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm(configuration, connections, loggerFactory));
    }
}
