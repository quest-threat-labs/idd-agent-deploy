using HybridAgentDeploy.Core;
using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// The safety ceilings of PRD 7. The orchestrator that applies them arrives in Phase 2;
/// the constants and the clamping are Phase 1 and are worth locking down now, because
/// R7.1 says there must be no path that raises them.
/// </summary>
public sealed class DeploymentLimitsTests
{
    [Fact]
    public void The_concurrency_ceiling_is_five()
    {
        Assert.Equal(5, DeploymentLimits.MaxConcurrencyCeiling);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(50, 5)]
    [InlineData(int.MaxValue, 5)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void Requested_parallelism_is_clamped_to_the_ceiling(int requested, int expected)
    {
        // PRD R7.1: rejecting a request above 5 is not an error the operator has to work
        // around. Clamp silently and note it in the run log.
        Assert.Equal(expected, DeploymentLimits.ClampParallelism(requested));
    }

    [Fact]
    public void Clamping_is_reported_so_the_run_log_can_record_it()
    {
        Assert.True(DeploymentLimits.WasParallelismClamped(6));
        Assert.False(DeploymentLimits.WasParallelismClamped(5));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(15, 15)]
    [InlineData(60, 60)]
    [InlineData(1, 5)]
    [InlineData(600, 60)]
    public void Requested_timeouts_are_clamped_to_the_permitted_range(int requested, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            DeploymentLimits.ClampTimeout(requested));
    }

    /// <summary>
    /// PRD R7.2: never occupy more than half the active DCs in a site at once, rounded down,
    /// floor of 1. In a two-DC site that means one at a time.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(10, 5)]
    public void The_site_guard_never_occupies_more_than_half_a_site(int dcsInSite, int expected)
    {
        Assert.Equal(expected, DeploymentLimits.SiteConcurrencyLimit(dcsInSite));
    }
}

/// <summary>
/// The storage timestamp format. Lexicographic order must equal chronological order or the
/// <c>dc_last_deployment</c> view silently returns the wrong row.
/// </summary>
public sealed class UtcTimestampTests
{
    [Fact]
    public void A_timestamp_round_trips_to_the_same_instant()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 14, 30, 15, TimeSpan.FromHours(5));

        var parsed = UtcTimestamp.Parse(UtcTimestamp.Format(instant));

        Assert.Equal(instant.UtcDateTime, parsed.UtcDateTime);
    }

    [Fact]
    public void A_local_time_is_stored_as_utc()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 14, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-03-01T12:30:00.0000000Z", UtcTimestamp.Format(instant));
    }

    /// <summary>
    /// The property the view depends on. A format that is not fixed-width — dropping a
    /// leading zero from the month, say — would sort "2026-1-05" after "2026-12-05".
    /// </summary>
    [Fact]
    public void Text_ordering_matches_chronological_ordering()
    {
        var instants = new[]
        {
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 1, 9, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero),
        };

        var formatted = instants.Select(UtcTimestamp.Format).ToList();

        Assert.Equal(formatted, formatted.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_stored_timestamp_is_the_same_width()
    {
        var widths = new[]
        {
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
        }.Select(i => UtcTimestamp.Format(i).Length).Distinct();

        Assert.Single(widths);
    }

    /// <summary>CLAUDE.md: local time only in the UI, and labelled when shown.</summary>
    [Fact]
    public void The_display_form_labels_both_zones()
    {
        var display = UtcTimestamp.ToDisplayString("2026-03-01T12:30:00.0000000Z");

        Assert.Contains("UTC", display, StringComparison.Ordinal);
        Assert.Contains("local", display, StringComparison.Ordinal);
    }
}

/// <summary>
/// Path resolution. PRD 6 and 12.1 default to %LOCALAPPDATA%; NFR4 and NFR5 require the
/// tool to run from a UNC path or removable media with nothing installed.
/// </summary>
public sealed class AppConfigurationTests
{
    [Fact]
    public void With_no_config_file_the_specified_defaults_are_used()
    {
        var directory = CreateTempDirectory();
        try
        {
            var config = AppConfigurationLoader.Load(directory);

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            Assert.False(config.IsPortable);
            Assert.Null(config.SourceFilePath);
            Assert.Equal(
                Path.Combine(localAppData, "Quest", "HybridAgentDeploy", "inventory.db"),
                config.DatabasePath);
            Assert.Equal(
                Path.Combine(localAppData, "Quest", "HybridAgentDeploy", "logs"),
                config.LogRootPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The portable case: a relative path in the config file is anchored to the executable's
    /// own directory, so a copy carried on removable media keeps its inventory with it.
    /// </summary>
    [Fact]
    public void A_relative_path_in_the_config_file_is_anchored_to_that_directory()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, AppConfigurationLoader.ConfigFileName),
                """
                {
                  "databasePath": "data/inventory.db",
                  "logRootPath": "data/logs"
                }
                """);

            var config = AppConfigurationLoader.Load(directory);

            Assert.True(config.IsPortable);
            Assert.Equal(Path.Combine(directory, "data", "inventory.db"), config.DatabasePath);
            Assert.Equal(Path.Combine(directory, "data", "logs"), config.LogRootPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_absolute_path_in_the_config_file_is_used_as_written()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, AppConfigurationLoader.ConfigFileName),
                """{ "databasePath": "D:\\quest\\inventory.db" }""");

            var config = AppConfigurationLoader.Load(directory);

            Assert.Equal(@"D:\quest\inventory.db", config.DatabasePath);

            // An unspecified setting still falls back to its default.
            Assert.Equal(AppConfigurationLoader.Defaults().LogRootPath, config.LogRootPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A malformed config file is fatal rather than silently ignored. An operator who wrote
    /// one meant it, and quietly reading a different inventory database than the one they
    /// configured is worse than refusing to start.
    /// </summary>
    [Fact]
    public void A_malformed_config_file_is_reported_rather_than_ignored()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, AppConfigurationLoader.ConfigFileName);
            File.WriteAllText(path, "{ this is not json");

            var ex = Assert.Throws<InvalidOperationException>(() => AppConfigurationLoader.Load(directory));

            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>PRD 12.1: one directory per run, named for its start time and run GUID.</summary>
    [Fact]
    public void A_run_log_directory_is_named_for_its_timestamp_and_run_guid()
    {
        var runGuid = Guid.Parse("abcd1234-0000-0000-0000-000000000000");
        var started = new DateTimeOffset(2026, 3, 1, 10, 5, 30, TimeSpan.Zero);

        var directory = AppConfigurationLoader.RunLogDirectory(@"C:\logs", runGuid, started);

        Assert.Equal(@"C:\logs\20260301-100530-abcd1234", directory);
    }

    [Fact]
    public void A_run_log_directory_uses_utc_regardless_of_the_local_zone()
    {
        var runGuid = Guid.Parse("abcd1234-0000-0000-0000-000000000000");
        var started = new DateTimeOffset(2026, 3, 1, 12, 5, 30, TimeSpan.FromHours(2));

        Assert.Equal(
            @"C:\logs\20260301-100530-abcd1234",
            AppConfigurationLoader.RunLogDirectory(@"C:\logs", runGuid, started));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
