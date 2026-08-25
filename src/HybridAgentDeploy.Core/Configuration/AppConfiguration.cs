using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridAgentDeploy.Core.Configuration;

/// <summary>
/// Where this instance keeps its inventory database and its run logs.
/// </summary>
/// <remarks>
/// PRD 6 and 12.1 give <c>%LOCALAPPDATA%\Quest\HybridAgentDeploy</c> as the default, while
/// NFR4 and NFR5 require the tool to run from a UNC path or removable media with nothing
/// installed. Those pull in opposite directions: a copy of the tool carried on a USB stick
/// to an air-gapped jump host should carry its inventory with it, not scatter a database
/// across every machine it is run on.
///
/// The resolution is a config file that is present only when the operator wants portable
/// behaviour. See <see cref="AppConfigurationLoader"/>.
/// </remarks>
public sealed record AppConfiguration
{
    /// <summary>Full path to the SQLite inventory database.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// Root under which each run gets its own directory (PRD 12.1). Individual runs are
    /// placed in <c>yyyyMMdd-HHmmss-{run-guid-short}</c> beneath this.
    /// </summary>
    public required string LogRootPath { get; init; }

    /// <summary>
    /// True when these paths came from a config file beside the executable rather than
    /// from %LOCALAPPDATA%. Surfaced so the operator can be shown which inventory they are
    /// actually looking at — silently reading a different database than expected is exactly
    /// the kind of surprise this tool cannot afford.
    /// </summary>
    public bool IsPortable { get; init; }

    /// <summary>The config file these settings came from, when there was one.</summary>
    public string? SourceFilePath { get; init; }
}

/// <summary>
/// The shape of <c>hybridagentdeploy.json</c>. Both properties are optional; either may be
/// relative, in which case it is anchored to the directory holding the config file.
/// </summary>
internal sealed record AppConfigurationFile
{
    [JsonPropertyName("databasePath")]
    public string? DatabasePath { get; init; }

    [JsonPropertyName("logRootPath")]
    public string? LogRootPath { get; init; }
}

/// <summary>
/// Resolves the paths the tool uses, preferring a config file beside the executable.
/// </summary>
public static class AppConfigurationLoader
{
    public const string ConfigFileName = "hybridagentdeploy.json";

    private const string DefaultVendorFolder = "Quest";
    private const string DefaultProductFolder = "HybridAgentDeploy";
    private const string DefaultDatabaseFileName = "inventory.db";
    private const string DefaultLogFolderName = "logs";

    /// <summary>
    /// Loads configuration, falling back to the PRD defaults.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory to look in, defaulting to the one containing the running assembly. Passed
    /// explicitly by tests so this is exercisable without a deployed layout.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The config file exists but cannot be read or parsed. Deliberately fatal rather than
    /// silently falling back: an operator who wrote a config file meant it, and quietly
    /// using a different inventory database than the one they configured would be worse
    /// than refusing to start.
    /// </exception>
    public static AppConfiguration Load(string? baseDirectory = null)
    {
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(directory, ConfigFileName);

        if (!File.Exists(configPath))
        {
            return Defaults();
        }

        AppConfigurationFile? file;
        try
        {
            using var stream = File.OpenRead(configPath);
            file = JsonSerializer.Deserialize<AppConfigurationFile>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The configuration file '{configPath}' could not be read: {ex.Message} " +
                "Correct the file, or remove it to fall back to the default paths under " +
                "%LOCALAPPDATA%\\Quest\\HybridAgentDeploy.",
                ex);
        }

        var defaults = Defaults();
        return new AppConfiguration
        {
            DatabasePath = Resolve(file?.DatabasePath, directory) ?? defaults.DatabasePath,
            LogRootPath = Resolve(file?.LogRootPath, directory) ?? defaults.LogRootPath,
            IsPortable = true,
            SourceFilePath = configPath,
        };
    }

    /// <summary>The PRD 6 and 12.1 defaults, used when no config file is present.</summary>
    public static AppConfiguration Defaults()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultVendorFolder,
            DefaultProductFolder);

        return new AppConfiguration
        {
            DatabasePath = Path.Combine(root, DefaultDatabaseFileName),
            LogRootPath = Path.Combine(root, DefaultLogFolderName),
            IsPortable = false,
            SourceFilePath = null,
        };
    }

    /// <summary>
    /// The per-run log directory beneath the configured root (PRD 12.1):
    /// <c>yyyyMMdd-HHmmss-{first 8 of run guid}</c>.
    /// </summary>
    public static string RunLogDirectory(string logRoot, Guid runGuid, DateTimeOffset startedUtc)
    {
        var stamp = startedUtc.ToUniversalTime().ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        var shortGuid = runGuid.ToString("N")[..8];
        return Path.Combine(logRoot, $"{stamp}-{shortGuid}");
    }

    /// <summary>
    /// The per-run log directory for a validation, beside the deployment runs.
    /// </summary>
    /// <remarks>
    /// Same timestamped shape so it sorts chronologically alongside them, with a suffix so an
    /// operator scanning the folder can tell at a glance which directories represent something
    /// that actually installed an agent on a domain controller and which do not.
    /// </remarks>
    public static string ValidationLogDirectory(string logRoot, Guid runGuid, DateTimeOffset startedUtc) =>
        RunLogDirectory(logRoot, runGuid, startedUtc) + "-validate";

    private static string? Resolve(string? configured, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());

        // A relative path is anchored to the config file's own directory, which is what
        // makes a USB stick or a UNC share self-contained: the whole layout moves together.
        return Path.GetFullPath(
            Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
    }
}
