using System.Security;
using HybridAgentDeploy.Core.Logging;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// SEC1 and PRD 12.3: credentials are held in memory for one run and are never written to
/// the database, to configuration, or to any log. Account names only.
/// </summary>
public sealed class OperatorCredentialTests
{
    [Fact]
    public void The_account_name_is_available_and_trimmed()
    {
        using var credential = new OperatorCredential(@"  CORP\admin  ", MakeSecret("hunter2"));

        Assert.Equal(@"CORP\admin", credential.AccountName);
    }

    /// <summary>
    /// The property that makes accidental leakage hard. Any log call that interpolates a
    /// credential — which is the realistic way a password reaches a log file — yields the
    /// account name instead.
    /// </summary>
    [Fact]
    public void Rendering_a_credential_yields_the_account_name_never_the_password()
    {
        using var credential = new OperatorCredential(@"CORP\admin", MakeSecret("hunter2"));

        var interpolated = $"Connecting as {credential}";

        Assert.Equal(@"Connecting as CORP\admin", interpolated);
        Assert.DoesNotContain("hunter2", interpolated, StringComparison.Ordinal);
    }

    [Fact]
    public void The_password_is_available_only_inside_a_scoped_callback()
    {
        using var credential = new OperatorCredential(@"CORP\admin", MakeSecret("hunter2"));

        var seen = credential.UsePassword(password => password);

        Assert.Equal("hunter2", seen);
    }

    [Fact]
    public void A_disposed_credential_refuses_to_yield_its_password()
    {
        var credential = new OperatorCredential(@"CORP\admin", MakeSecret("hunter2"));
        credential.Dispose();

        Assert.Throws<ObjectDisposedException>(() => credential.UsePassword(p => p));
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        var credential = new OperatorCredential(@"CORP\admin", MakeSecret("hunter2"));
        credential.Dispose();
        credential.Dispose();
    }

    [Fact]
    public void An_empty_account_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new OperatorCredential("   ", MakeSecret("x")));
    }

    /// <summary>
    /// The operator account recorded against every run (PRD 6, 12.2). DOMAIN\user form, and
    /// nothing else.
    /// </summary>
    [Fact]
    public void The_current_windows_account_is_reported_in_domain_user_form()
    {
        var account = OperatorCredential.CurrentWindowsAccountName();

        Assert.False(string.IsNullOrWhiteSpace(account));
        Assert.Contains('\\', account);
    }

    private static SecureString MakeSecret(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        return secure;
    }
}

/// <summary>
/// The file sink for the utility's own diagnostic log. The per-run <c>run.log</c> of PRD 12
/// is a separate writer that arrives in Phase 2.
/// </summary>
public sealed class FileLoggerTests
{
    [Fact]
    public void Messages_are_written_to_the_configured_file()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "app.log");

        try
        {
            using (var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path }))
            {
                var logger = provider.CreateLogger("HybridAgentDeploy.Tests");
                logger.LogInformation("Enumerated {Count} domain controllers.", 12);
            }

            var contents = File.ReadAllText(path);

            Assert.Contains("Enumerated 12 domain controllers.", contents, StringComparison.Ordinal);
            Assert.Contains("[INF]", contents, StringComparison.Ordinal);
            Assert.Contains("HybridAgentDeploy.Tests", contents, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>CLAUDE.md: UTC in storage and logs.</summary>
    [Fact]
    public void Each_line_is_stamped_in_utc()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "app.log");

        try
        {
            using (var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path }))
            {
                provider.CreateLogger("Test").LogWarning("Cleanup did not complete.");
            }

            var line = File.ReadAllLines(path).Single();
            var stamp = line[..UtcTimestamp.FormatString.Length];

            Assert.EndsWith("Z", stamp, StringComparison.Ordinal);
            var parsed = HybridAgentDeploy.Core.UtcTimestamp.Parse(stamp);
            Assert.True((DateTimeOffset.UtcNow - parsed).Duration() < TimeSpan.FromMinutes(1));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Messages_below_the_minimum_level_are_not_written()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "app.log");

        try
        {
            using (var provider = new FileLoggerProvider(new FileLoggerOptions
            {
                FilePath = path,
                MinimumLevel = LogLevel.Warning,
            }))
            {
                var logger = provider.CreateLogger("Test");
                logger.LogDebug("noise");
                logger.LogWarning("signal");
            }

            var contents = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

            Assert.DoesNotContain("noise", contents, StringComparison.Ordinal);
            Assert.Contains("signal", contents, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void An_exception_is_recorded_with_its_message()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "app.log");

        try
        {
            using (var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path }))
            {
                provider.CreateLogger("Test").LogError(
                    new IOException("access denied writing to the staging directory"),
                    "Staging failed on {Dc}.",
                    "DC01.corp.local");
            }

            var contents = File.ReadAllText(path);

            Assert.Contains("Staging failed on DC01.corp.local.", contents, StringComparison.Ordinal);
            Assert.Contains("access denied", contents, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// The log directory is created rather than required to exist: PRD 12.1 puts the log
    /// root somewhere the operator may never have run the tool before.
    /// </summary>
    [Fact]
    public void A_missing_log_directory_is_created()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "nested", "deeper", "app.log");

        try
        {
            using (var provider = new FileLoggerProvider(new FileLoggerOptions { FilePath = path }))
            {
                provider.CreateLogger("Test").LogInformation("started");
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
