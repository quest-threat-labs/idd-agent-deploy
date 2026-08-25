using System.Collections.Concurrent;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Testing;

/// <summary>
/// An <see cref="ITargetTransport"/> that fabricates per-host outcomes without touching a
/// network (PRD 5.3).
/// </summary>
/// <remarks>
/// <para>
/// Not a test double bolted on after the fact. The deployment orchestrator is built and
/// tested entirely against this in Phase 2 and, per the phase's acceptance criteria, must
/// never learn whether a real domain controller exists.
/// </para>
/// <para>
/// It lives in the test-support assembly rather than in Core so that a transport which
/// fabricates success is not present in the signed binary an operator points at their
/// domain controllers.
/// </para>
/// <para>
/// Every call is recorded in <see cref="Calls"/>, and <see cref="ConcurrencyObserved"/>
/// tracks how many per-target sequences were in flight at once — that is what lets a test
/// assert the ceiling of 5 held and that slots were released only after cleanup (PRD 15.1).
/// </para>
/// </remarks>
public sealed class SimulatedTransport : ITargetTransport
{
    private readonly ConcurrentDictionary<string, SimulatedHost> _hosts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<TransportCall> _calls = new();
    private readonly SimulatedHost _default = new();

    private int _inFlight;
    private int _peakInFlight;

    /// <summary>Every call made, in the order it completed. Read by tests.</summary>
    public IReadOnlyCollection<TransportCall> Calls => _calls;

    /// <summary>
    /// Every command passed to <see cref="ExecuteAsync"/>, with its arguments still
    /// separated.
    /// </summary>
    /// <remarks>
    /// Kept structured rather than rendered so the SEC10 test can assert that an Org ID
    /// containing shell metacharacters stayed inside a single argument, which is the property
    /// that makes it inert — asserting on a rendered string would only prove the quoter ran.
    /// </remarks>
    public ConcurrentQueue<RemoteCommand> ExecutedCommands { get; } = new();

    /// <summary>
    /// The highest number of per-target sequences observed in flight simultaneously.
    /// </summary>
    /// <remarks>
    /// A sequence counts as in flight from its pre-flight until its cleanup, which is the
    /// span PRD R7.1 says a concurrency slot must cover. A test asserting this never exceeds
    /// 5 is asserting the real requirement, not just that a semaphore exists.
    /// </remarks>
    public int ConcurrencyObserved => Volatile.Read(ref _peakInFlight);

    /// <summary>Configures how one host behaves. Unconfigured hosts succeed.</summary>
    public SimulatedTransport ConfigureHost(string fqdn, Action<SimulatedHost> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_hosts.GetOrAdd(fqdn, _ => new SimulatedHost()));
        return this;
    }

    /// <summary>Configures the behaviour used by hosts with no explicit configuration.</summary>
    public SimulatedTransport ConfigureDefault(Action<SimulatedHost> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_default);
        return this;
    }

    private SimulatedHost For(string fqdn) => _hosts.GetValueOrDefault(fqdn, _default);

    public async Task<PreflightResult> PreflightAsync(string targetHost, CancellationToken ct)
    {
        EnterSequence();
        var host = For(targetHost);
        await host.DelayAsync(host.PreflightDelay, ct).ConfigureAwait(false);
        Record(targetHost, DeploymentStage.Preflight);

        if (host.PreflightFailure is { } failure)
        {
            // A pre-flight failure ends the sequence here — nothing further is attempted
            // against this host, so the slot must be released now.
            ExitSequence();
            return PreflightResult.Failed(failure.Category, failure.Detail);
        }

        return PreflightResult.Success();
    }

    public async Task<StagingResult> StageFileAsync(
        string targetHost,
        string localPath,
        string remoteDirectory,
        CancellationToken ct)
    {
        var host = For(targetHost);
        await host.DelayAsync(host.StagingDelay, ct).ConfigureAwait(false);
        Record(targetHost, DeploymentStage.Stage, remoteDirectory);

        if (host.StagingFailure is { } failure)
        {
            return StagingResult.Failed(failure.Category, failure.Detail);
        }

        var stagedPath = Path.Combine(remoteDirectory, Path.GetFileName(localPath));
        return StagingResult.Success(stagedPath, host.HashToReportFor(localPath));
    }

    public async Task<ExecutionResult> ExecuteAsync(
        string targetHost,
        RemoteCommand command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var host = For(targetHost);
        await host.DelayAsync(host.ExecutionDelay, ct).ConfigureAwait(false);

        // Recorded as the rendered line so tests can assert on what was run, and separately
        // as the argument list so they can assert an injection attempt stayed in its slot.
        ExecutedCommands.Enqueue(command);
        Record(targetHost, DeploymentStage.Execute, command.ToDisplayString());

        if (host.ExecutionTimesOut)
        {
            return ExecutionResult.Timeout(
                $"msiexec on {targetHost} did not return within {timeout.TotalMinutes:0} minutes.");
        }

        if (host.ExecutionLaunchFailure is { } failure)
        {
            return ExecutionResult.FailedToLaunch(failure.Category, failure.Detail);
        }

        return ExecutionResult.Completed(host.NextExitCode(), host.StandardOutput);
    }

    public async Task<RetrievalResult> RetrieveFileAsync(
        string targetHost,
        string remotePath,
        string localDestination,
        CancellationToken ct)
    {
        var host = For(targetHost);
        await host.DelayAsync(host.RetrievalDelay, ct).ConfigureAwait(false);
        Record(targetHost, DeploymentStage.Retrieve, remotePath);

        if (host.RetrievalFailure is { } failure)
        {
            return RetrievalResult.Failed(failure.Category, failure.Detail);
        }

        return RetrievalResult.Success(localDestination);
    }

    public async Task CleanupAsync(string targetHost, string remoteDirectory, CancellationToken ct)
    {
        var host = For(targetHost);
        try
        {
            await host.DelayAsync(host.CleanupDelay, ct).ConfigureAwait(false);
            Record(targetHost, DeploymentStage.Cleanup, remoteDirectory);

            if (host.CleanupThrows)
            {
                throw new IOException(
                    $"The staging directory '{remoteDirectory}' on {targetHost} could not be removed.");
            }
        }
        finally
        {
            // Cleanup terminates the sequence whether or not it succeeded, so the slot is
            // released here and only here for hosts that got past pre-flight.
            ExitSequence();
        }
    }

    private void EnterSequence()
    {
        var current = Interlocked.Increment(ref _inFlight);

        int peak;
        while (current > (peak = Volatile.Read(ref _peakInFlight)))
        {
            Interlocked.CompareExchange(ref _peakInFlight, current, peak);
        }
    }

    private void ExitSequence() => Interlocked.Decrement(ref _inFlight);

    private void Record(string host, DeploymentStage stage, string? detail = null) =>
        _calls.Enqueue(new TransportCall(host, stage, detail, DateTimeOffset.UtcNow));
}

/// <summary>One transport call, for assertions about ordering and content.</summary>
public sealed record TransportCall(
    string TargetHost,
    DeploymentStage Stage,
    string? Detail,
    DateTimeOffset AtUtc);

/// <summary>A configured failure: the category the orchestrator will see, and its detail.</summary>
public sealed record SimulatedFailure(ErrorCategory Category, string Detail);

/// <summary>
/// How one simulated host behaves at each stage of the PRD 5.4 sequence.
/// </summary>
public sealed class SimulatedHost
{
    private readonly Queue<int> _exitCodes = new();
    private int _lastExitCode;

    public SimulatedFailure? PreflightFailure { get; set; }
    public SimulatedFailure? StagingFailure { get; set; }
    public SimulatedFailure? ExecutionLaunchFailure { get; set; }
    public SimulatedFailure? RetrievalFailure { get; set; }

    /// <summary>Drives the PRD R7.5 timeout path without waiting fifteen minutes.</summary>
    public bool ExecutionTimesOut { get; set; }

    /// <summary>
    /// Cleanup failures are logged as warnings and must not fail the deployment
    /// (PRD 5.4 step 5).
    /// </summary>
    public bool CleanupThrows { get; set; }

    /// <summary>
    /// The hash reported for the staged copy, for exercising SEC6 verification.
    /// </summary>
    /// <remarks>
    /// Setting it simulates a file that arrived altered — or, when it matches the source, one
    /// that arrived intact. Leaving it null means a healthy host: the source file's real
    /// SHA-256 is reported when the file exists on disk, and the all-zero placeholder when it
    /// does not, which is what lets a test use a fictional MSI path without tripping the
    /// verification it is not trying to exercise.
    /// </remarks>
    public string? StagedSha256 { get; set; }

    internal string HashToReportFor(string localPath)
    {
        if (StagedSha256 is { } configured)
        {
            return configured;
        }

        if (!File.Exists(localPath))
        {
            return new string('0', 64);
        }

        using var stream = File.OpenRead(localPath);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// What a command that runs to completion writes to standard output.
    /// </summary>
    /// <remarks>
    /// Returned for every execution on this host rather than varying by command. Validation is
    /// the only caller that reads standard output, and it reads it from exactly one call — the
    /// installed-agent query — so per-command fidelity would be machinery with no reader.
    /// </remarks>
    public string? StandardOutput { get; set; }

    public TimeSpan PreflightDelay { get; set; }
    public TimeSpan StagingDelay { get; set; }
    public TimeSpan ExecutionDelay { get; set; }
    public TimeSpan RetrievalDelay { get; set; }
    public TimeSpan CleanupDelay { get; set; }

    /// <summary>
    /// Queues exit codes returned by successive execution attempts.
    /// </summary>
    /// <remarks>
    /// A sequence is what makes the 1618 retry testable end to end: queue
    /// <c>1618, 1618, 0</c> and the third attempt succeeds, which is the behaviour PRD 10.1
    /// describes. Once the queue empties the last code repeats, so a host configured with a
    /// single code behaves consistently however many times it is called.
    /// </remarks>
    public SimulatedHost ReturnsExitCodes(params int[] codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        foreach (var code in codes)
        {
            _exitCodes.Enqueue(code);
        }

        return this;
    }

    internal int NextExitCode()
    {
        lock (_exitCodes)
        {
            if (_exitCodes.Count > 0)
            {
                _lastExitCode = _exitCodes.Dequeue();
            }

            return _lastExitCode;
        }
    }

    internal Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, ct) : Task.CompletedTask;
}
