using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HybridAgentDeploy.Core.Logging;

/// <summary>
/// Options for <see cref="FileLoggerProvider"/>.
/// </summary>
public sealed class FileLoggerOptions
{
    /// <summary>Full path of the log file. Its directory is created if absent.</summary>
    public required string FilePath { get; init; }

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Roll the file aside once it exceeds this size. Zero disables rolling.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>How many rolled files to keep alongside the current one.</summary>
    public int RetainedFileCount { get; init; } = 5;
}

/// <summary>
/// A minimal file sink for <c>Microsoft.Extensions.Logging</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than taken from a logging library. MEL ships no file provider in the
/// box, and this tool is aimed at hardened Tier 0 environments where every third-party
/// dependency in the signed binary is one more thing for a customer's security team to
/// review. The requirement here is a few hundred lines of append-to-a-file.
/// </para>
/// <para>
/// This is the diagnostic log for the utility itself. It is <em>not</em> the per-run
/// <c>run.log</c> of PRD 12, whose content and ordering are prescribed and which is written
/// by a dedicated writer in Phase 2.
/// </para>
/// <para>
/// PRD 12.3 and SEC1: nothing written here may contain credential material. The one type
/// that holds a password, <c>OperatorCredential</c>, renders as its account name.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 8192);
    private readonly Task _writer;
    private bool _disposed;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.FilePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A single background writer keeps file access serialised and keeps the write off
        // the calling thread, which matters because NFR2 forbids blocking the UI thread on
        // I/O and log calls are made from it.
        _writer = Task.Factory.StartNew(
            DrainQueue,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _options, Enqueue));

    private void Enqueue(string line)
    {
        // Never let logging take down a deployment. If the queue is full the process is
        // already in trouble; dropping a diagnostic line is preferable to blocking a run
        // against a domain controller.
        if (!_disposed)
        {
            _queue.TryAdd(line);
        }
    }

    private void DrainQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(_options.FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // The log file is locked or the volume is full. Continue draining rather
                // than terminating the writer, so logging recovers if the condition clears.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning: a permissions problem on the log path must not stop a run.
            }
        }
    }

    private void RollIfNeeded()
    {
        if (_options.MaxFileSizeBytes <= 0)
        {
            return;
        }

        var current = new FileInfo(_options.FilePath);
        if (!current.Exists || current.Length < _options.MaxFileSizeBytes)
        {
            return;
        }

        var stamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        var rolled = Path.Combine(
            current.DirectoryName ?? ".",
            $"{Path.GetFileNameWithoutExtension(current.Name)}-{stamp}{current.Extension}");

        current.MoveTo(rolled, overwrite: true);
        PruneRolledFiles(current.DirectoryName, Path.GetFileNameWithoutExtension(_options.FilePath),
            Path.GetExtension(_options.FilePath));
    }

    private void PruneRolledFiles(string? directory, string baseName, string extension)
    {
        if (string.IsNullOrEmpty(directory) || _options.RetainedFileCount <= 0)
        {
            return;
        }

        var stale = new DirectoryInfo(directory)
            .GetFiles($"{baseName}-*{extension}")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(_options.RetainedFileCount);

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Someone has the old log open. It will be pruned on a later roll.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Bounded so a stuck writer cannot hang process exit; the alternative is losing the
        // last few lines, which is the lesser problem.
        _writer.Wait(TimeSpan.FromSeconds(5));
        _queue.Dispose();
        _loggers.Clear();
    }
}

/// <summary>
/// Formats one category's messages and hands them to the provider's writer.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerOptions _options;
    private readonly Action<string> _write;

    public FileLogger(string category, FileLoggerOptions options, Action<string> write)
    {
        _category = category;
        _options = options;
        _write = write;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _options.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var builder = new StringBuilder(256);

        // UTC in logs, per CLAUDE.md. Local time appears only in the UI, labelled.
        builder.Append(UtcTimestamp.Now())
            .Append(" [").Append(LevelLabel(logLevel)).Append("] ")
            .Append(_category).Append(" - ")
            .Append(formatter(state, exception));

        if (exception is not null)
        {
            builder.Append(Environment.NewLine).Append(exception);
        }

        _write(builder.ToString());
    }

    /// <summary>Fixed width so a log read in a plain text editor stays aligned.</summary>
    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
