using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Logging;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Runs the PRD 5.4 deployment sequence across a set of domain controllers, under the safety
/// guards of PRD 7.
/// </summary>
/// <remarks>
/// <para>
/// This class does not know whether a real domain controller exists, and must never learn.
/// Everything remote goes through <see cref="ITargetTransport"/>, which is what allows the
/// concurrency ceiling, the site guard, the circuit breaker, the retry logic, and the
/// exit-code handling to be developed and tested without a forest.
/// </para>
/// <para>
/// The per-target sequence is deliberate and is not reordered: pre-flight, stage, execute,
/// retrieve, clean up, record.
/// </para>
/// </remarks>
public sealed class DeploymentOrchestrator
{
    /// <summary>Backoff before each 1618 retry (PRD 10.1).</summary>
    /// <remarks>
    /// Two waits for three attempts. PRD 10.1 says "up to 3 attempts" and PRD 15.1 requires a
    /// test asserting "exactly 3 attempts", so three attempts is authoritative and the third
    /// published backoff value has no attempt to precede.
    /// </remarks>
    private static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

    /// <summary>Attempts for a target returning 1618 (PRD 10.1, 15.1).</summary>
    public const int MaxAttemptsForContendedInstaller = 3;

    /// <summary>
    /// SEC7: under <c>C:\Windows\Temp</c>, which inherits admin-only ACLs. Never a
    /// world-readable location, and the ACLs on the staging path are never loosened.
    /// </summary>
    private const string StagingRoot = @"C:\Windows\Temp\HybridAgentDeploy";

    /// <summary>
    /// The per-run staging directory name on each target: the full run GUID, as PRD 5.4
    /// specifies. Deliberately not the eight-character short form used for the local log
    /// directory (PRD 12.1) — a directory left behind on a domain controller must be
    /// traceable to the run that created it without ambiguity (NFR7).
    /// </summary>
    private static string StagingDirectoryName(Guid runGuid) => runGuid.ToString("D");

    private readonly ITargetTransport _transport;
    private readonly DeploymentRepository _deployments;
    private readonly ILogger<DeploymentOrchestrator> _log;
    private readonly TimeProvider _time;

    public DeploymentOrchestrator(
        ITargetTransport transport,
        DeploymentRepository deployments,
        ILogger<DeploymentOrchestrator>? log = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _log = log ?? NullLogger<DeploymentOrchestrator>.Instance;

        // Injected so the 1618 backoff can be asserted without a test waiting three minutes.
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Deploys to every target in the request.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation stops <em>starting</em> new targets. Targets already in flight run to
    /// completion or to their own timeout, and their staging directories are always cleaned
    /// up: PRD 8.4 forbids abandoning an in-flight msiexec without attempting cleanup.
    /// </param>
    public async Task<DeploymentRunSummary> DeployAsync(
        DeploymentRequest request,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runGuid = Guid.NewGuid();
        var startedUtc = _time.GetUtcNow();

        // A representative command, built once against a placeholder path, so the run log can
        // record the exact template before any target is touched (PRD 12.2).
        var commandTemplate = MsiCommandBuilder.BuildInstallCommand(
            Path.Combine(StagingRoot, StagingDirectoryName(runGuid), request.Msi.FileName),
            Path.Combine(StagingRoot, StagingDirectoryName(runGuid), "install.log"),
            request.OrgId,
            request.CloudMode).ToDisplayString();

        var run = new DeploymentRunRecord
        {
            RunGuid = runGuid,
            StartedUtc = UtcTimestamp.Format(startedUtc),
            OperatorAccount = request.OperatorAccount,
            MsiPath = request.Msi.FilePath,
            MsiFileName = request.Msi.FileName,
            MsiSha256 = request.Msi.Sha256,
            MsiProductVersion = request.Msi.ProductVersion,
            MsiProductName = request.Msi.ProductName,
            MsiProductCode = request.Msi.ProductCode,
            OrgId = request.OrgId,
            CloudMode = request.CloudMode,
            MaxParallel = request.MaxParallel,
            TargetCount = request.Targets.Count,
            LogDirectory = request.LogDirectory,
        };

        var runId = await _deployments.CreateRunAsync(run, CancellationToken.None).ConfigureAwait(false);

        await using var runLog = await RunLogWriter.CreateAsync(request.LogDirectory, CancellationToken.None)
            .ConfigureAwait(false);

        var siteGuard = new SiteConcurrencyGuard(request.ActiveDcsPerSite);
        await runLog.WriteHeaderAsync(request, runGuid, startedUtc, commandTemplate, siteGuard, CancellationToken.None)
            .ConfigureAwait(false);

        var breaker = new CircuitBreaker();
        var state = new RunState(request.Targets.Count);

        // The global ceiling. R7.1: a slot spans the entire sequence, pre-flight through
        // cleanup, and is released only when that whole sequence terminates.
        using var globalSlots = new SemaphoreSlim(request.MaxParallel, request.MaxParallel);

        var outcomes = new List<TargetOutcome>(request.Targets.Count);
        var tasks = new List<Task<TargetOutcome>>(request.Targets.Count);
        var unstarted = new List<DeploymentTarget>();

        foreach (var target in request.Targets)
        {
            if (breaker.IsTripped || cancellationToken.IsCancellationRequested)
            {
                unstarted.Add(target);
                continue;
            }

            // Waiting here rather than inside the task keeps the number of started
            // operations bounded, so a halt or a cancellation stops work that has not begun.
            await globalSlots.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            // Re-check after waiting: the run may have halted while this target queued.
            if (breaker.IsTripped || cancellationToken.IsCancellationRequested)
            {
                globalSlots.Release();
                unstarted.Add(target);
                continue;
            }

            tasks.Add(RunTargetAsync(
                target, request, runId, runGuid, siteGuard, breaker, state,
                globalSlots, runLog, progress, cancellationToken));
        }

        foreach (var task in tasks)
        {
            outcomes.Add(await task.ConfigureAwait(false));
        }

        // Targets that never started are recorded as Skipped rather than omitted. A run that
        // halted after five of sixty DCs must not leave the other fifty-five unaccounted for.
        foreach (var target in unstarted)
        {
            outcomes.Add(await RecordSkippedAsync(
                target, runId, breaker.IsTripped, cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false));
        }

        var completedUtc = _time.GetUtcNow();

        run.Id = runId;
        run.CompletedUtc = UtcTimestamp.Format(completedUtc);
        run.SuccessCount = outcomes.Count(o => o.IsSuccess);
        run.FailureCount = outcomes.Count(o => !o.IsSuccess && o.Outcome != DeploymentOutcome.Skipped);
        run.WasHalted = breaker.IsTripped;
        run.HaltReason = breaker.HaltReason;
        await _deployments.CompleteRunAsync(run, CancellationToken.None).ConfigureAwait(false);

        var summary = new DeploymentRunSummary
        {
            RunGuid = runGuid,
            RunId = runId,
            Outcomes = outcomes,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            WasHalted = breaker.IsTripped,
            HaltReason = breaker.HaltReason,
            WasCancelled = cancellationToken.IsCancellationRequested,
            LogDirectory = request.LogDirectory,
            CommandTemplate = commandTemplate,
        };

        await runLog.WriteSummaryAsync(summary, CancellationToken.None).ConfigureAwait(false);
        await runLog.WriteResultsCsvAsync(summary, CancellationToken.None).ConfigureAwait(false);

        return summary;
    }

    private async Task<TargetOutcome> RunTargetAsync(
        DeploymentTarget target,
        DeploymentRequest request,
        long runId,
        Guid runGuid,
        SiteConcurrencyGuard siteGuard,
        CircuitBreaker breaker,
        RunState state,
        SemaphoreSlim globalSlots,
        RunLogWriter runLog,
        IProgress<DeploymentProgress>? progress,
        CancellationToken operatorCancellation)
    {
        // Yield so the caller's loop continues acquiring slots rather than running the first
        // target inline.
        await Task.Yield();

        try
        {
            // R7.2: a secondary gate, acquired after the global semaphore.
            Report(progress, target, TargetProgressState.WaitingForSlot, null, state, globalSlots, request, null);
            using var siteSlot = await siteGuard.AcquireAsync(target.SiteName, CancellationToken.None)
                .ConfigureAwait(false);

            state.EnterFlight();
            Report(progress, target, TargetProgressState.Running, DeploymentStage.Preflight, state, globalSlots, request, null);

            var outcome = await ExecuteSequenceAsync(
                target, request, runId, runGuid, runLog, progress, state, globalSlots, operatorCancellation)
                .ConfigureAwait(false);

            state.RecordCompletion(outcome);

            if (breaker.RecordOutcome(target.Fqdn, outcome.ErrorCategory))
            {
                _log.LogError("Circuit breaker tripped: {Reason}", breaker.HaltReason);
                await runLog.WriteLineAsync(
                    $"CIRCUIT BREAKER TRIPPED. {breaker.HaltReason}", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            Report(progress, target, TargetProgressState.Completed, outcome.Stage, state, globalSlots, request,
                outcome.ErrorDetail);

            return outcome;
        }
        finally
        {
            state.ExitFlight();

            // R7.1: the slot is released only now, after the entire sequence has terminated —
            // not when msiexec returned.
            globalSlots.Release();
        }
    }

    private async Task<TargetOutcome> ExecuteSequenceAsync(
        DeploymentTarget target,
        DeploymentRequest request,
        long runId,
        Guid runGuid,
        RunLogWriter runLog,
        IProgress<DeploymentProgress>? progress,
        RunState state,
        SemaphoreSlim globalSlots,
        CancellationToken operatorCancellation)
    {
        var startedUtc = _time.GetUtcNow();
        var warnings = new List<string>();

        var stagingDirectory = Path.Combine(StagingRoot, StagingDirectoryName(runGuid));
        var stagedLogPath = Path.Combine(stagingDirectory, "install.log");
        var localLogPath = Path.Combine(request.LogDirectory, $"{target.Fqdn}_install.log");

        var result = new DeploymentResultRecord
        {
            RunId = runId,
            DcId = target.DcId,
            StartedUtc = UtcTimestamp.Format(startedUtc),
            Outcome = DeploymentOutcome.Skipped,
            Stage = DeploymentStage.Preflight,
        };
        var resultId = await _deployments.CreateResultAsync(result, CancellationToken.None).ConfigureAwait(false);

        // The operator's cancel must not abort work already in flight (PRD 8.4), so the token
        // handed to the transport is driven only by this target's own timeout.
        using var timeoutSource = new CancellationTokenSource(request.PerTargetTimeout, _time);
        var targetToken = timeoutSource.Token;

        var stage = DeploymentStage.Preflight;
        var attemptCount = 1;
        var stagingRecorded = false;

        try
        {
            await runLog.WriteLineAsync($"{target.Fqdn}: pre-flight", CancellationToken.None).ConfigureAwait(false);

            var preflight = await _transport.PreflightAsync(target.Fqdn, targetToken).ConfigureAwait(false);
            if (!preflight.Succeeded)
            {
                return await FailAsync(
                    resultId, target, result, DeploymentStage.Preflight,
                    preflight.ErrorCategory ?? ErrorCategory.Connectivity,
                    preflight.ErrorDetail ?? $"Pre-flight failed on {target.Fqdn}.",
                    startedUtc, attemptCount, null, null, warnings, runLog).ConfigureAwait(false);
            }

            stage = DeploymentStage.Stage;
            ReportStage(progress, target, stage, state, globalSlots, request);
            await runLog.WriteLineAsync($"{target.Fqdn}: staging to {stagingDirectory}", CancellationToken.None)
                .ConfigureAwait(false);

            // NFR7: recorded BEFORE the directory is created, so a process killed here leaves
            // an orphan the tool can still identify and clean up.
            await _deployments.RecordStagingPathAsync(resultId, stagingDirectory, CancellationToken.None)
                .ConfigureAwait(false);
            stagingRecorded = true;

            var staged = await _transport
                .StageFileAsync(target.Fqdn, request.Msi.FilePath, stagingDirectory, targetToken)
                .ConfigureAwait(false);

            if (!staged.Succeeded || staged.StagedPath is null)
            {
                return await FailAsync(
                    resultId, target, result, DeploymentStage.Stage,
                    staged.ErrorCategory ?? ErrorCategory.Staging,
                    staged.ErrorDetail ?? $"Staging failed on {target.Fqdn}.",
                    startedUtc, attemptCount, null, stagingDirectory, warnings, runLog).ConfigureAwait(false);
            }

            // SEC6: a file that changed in transit must never be installed on a domain
            // controller. Verified here rather than trusted to the transport, because this is
            // the last point before an elevated install runs.
            if (!string.Equals(staged.VerifiedSha256, request.Msi.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(
                    resultId, target, result, DeploymentStage.Stage, ErrorCategory.Staging,
                    $"The MSI staged on {target.Fqdn} does not match the source. Expected SHA-256 " +
                    $"{request.Msi.Sha256}, found {staged.VerifiedSha256 ?? "no hash"}. The install " +
                    "was NOT attempted. Re-run against this DC; if it recurs, the source file or " +
                    "the path to it is suspect and should be investigated before deploying anywhere.",
                    startedUtc, attemptCount, null, stagingDirectory, warnings, runLog).ConfigureAwait(false);
            }

            stage = DeploymentStage.Execute;
            ReportStage(progress, target, stage, state, globalSlots, request);

            var command = MsiCommandBuilder.BuildInstallCommand(
                staged.StagedPath, stagedLogPath, request.OrgId, request.CloudMode);

            var execution = await ExecuteWithRetryAsync(
                target, command, request, runLog, targetToken).ConfigureAwait(false);

            attemptCount = execution.AttemptCount;

            // Retrieve before interpreting the exit code: PRD 5.4 step 4 fetches the log
            // whether the install succeeded or failed, and it is most valuable on failure.
            stage = DeploymentStage.Retrieve;
            ReportStage(progress, target, stage, state, globalSlots, request);

            string? retrievedLogPath = null;
            var retrieval = await _transport
                .RetrieveFileAsync(target.Fqdn, stagedLogPath, localLogPath, targetToken)
                .ConfigureAwait(false);

            if (retrieval.Succeeded)
            {
                retrievedLogPath = retrieval.LocalPath;
            }
            else
            {
                warnings.Add(
                    $"The msiexec verbose log could not be retrieved from {target.Fqdn}: " +
                    $"{retrieval.ErrorDetail}");
            }

            if (execution.Result.TimedOut)
            {
                return await FinishAsync(
                    resultId, target, result, DeploymentStage.Execute, DeploymentOutcome.Timeout,
                    ErrorCategory.Timeout,
                    execution.Result.ErrorDetail ??
                        $"The installation on {target.Fqdn} did not complete within " +
                        $"{request.PerTargetTimeout.TotalMinutes:0} minutes.",
                    null, startedUtc, attemptCount, retrievedLogPath, stagingDirectory,
                    warnings, false, runLog).ConfigureAwait(false);
            }

            if (!execution.Result.Launched || execution.Result.ExitCode is null)
            {
                return await FailAsync(
                    resultId, target, result, DeploymentStage.Execute,
                    execution.Result.ErrorCategory ?? ErrorCategory.Internal,
                    execution.Result.ErrorDetail ?? $"msiexec could not be started on {target.Fqdn}.",
                    startedUtc, attemptCount, retrievedLogPath, stagingDirectory, warnings, runLog)
                    .ConfigureAwait(false);
            }

            var exitCode = execution.Result.ExitCode.Value;
            var mapping = MsiExitCode.Map(exitCode, target.Fqdn);

            // A 1618 that survived every retry is Contended rather than transient.
            var category = mapping.IsSuccess ? null : mapping.ErrorCategory;

            return await FinishAsync(
                resultId, target, result, DeploymentStage.Execute, mapping.Outcome, category,
                mapping.IsSuccess ? null : mapping.Description, exitCode, startedUtc, attemptCount,
                retrievedLogPath, stagingDirectory, warnings, mapping.RequiresProminentWarning, runLog)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return await FinishAsync(
                resultId, target, result, stage, DeploymentOutcome.Timeout, ErrorCategory.Timeout,
                $"The deployment to {target.Fqdn} exceeded the per-target timeout of " +
                $"{request.PerTargetTimeout.TotalMinutes:0} minutes during the {stage} stage. " +
                "Cleanup was still attempted. Confirm the DC is responsive and consider raising " +
                "the timeout for that host.",
                null, startedUtc, attemptCount, null, stagingRecorded ? stagingDirectory : null,
                warnings, false, runLog).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled failure deploying to {Dc}.", target.Fqdn);

            return await FinishAsync(
                resultId, target, result, stage, DeploymentOutcome.Failure, ErrorCategory.Internal,
                $"An unexpected error occurred deploying to {target.Fqdn} during the {stage} " +
                $"stage: {ex.Message}. This is a defect in the utility rather than a problem with " +
                "the domain controller; the run log holds the detail.",
                null, startedUtc, attemptCount, null, stagingRecorded ? stagingDirectory : null,
                warnings, false, runLog).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs msiexec, retrying only on 1618.
    /// </summary>
    /// <remarks>
    /// PRD R7.6: nothing else is ever retried automatically. Silently retrying an MSI that
    /// failed for an unknown reason against a domain controller is not acceptable behaviour;
    /// failed targets are re-runnable by operator action instead.
    /// </remarks>
    private async Task<RetryOutcome> ExecuteWithRetryAsync(
        DeploymentTarget target,
        RemoteCommand command,
        DeploymentRequest request,
        RunLogWriter runLog,
        CancellationToken targetToken)
    {
        ExecutionResult result;
        var attempt = 1;

        while (true)
        {
            await runLog.WriteLineAsync(
                $"{target.Fqdn}: executing (attempt {attempt} of {MaxAttemptsForContendedInstaller} " +
                "permitted for a contended installer)", CancellationToken.None).ConfigureAwait(false);

            result = await _transport
                .ExecuteAsync(target.Fqdn, command, request.PerTargetTimeout, targetToken)
                .ConfigureAwait(false);

            if (result.TimedOut || !result.Launched || result.ExitCode is null)
            {
                return new RetryOutcome(result, attempt);
            }

            var mapping = MsiExitCode.Map(result.ExitCode.Value, target.Fqdn);
            if (!mapping.IsRetryable || attempt >= MaxAttemptsForContendedInstaller)
            {
                return new RetryOutcome(result, attempt);
            }

            var backoff = RetryBackoffs[Math.Min(attempt - 1, RetryBackoffs.Length - 1)];

            await runLog.WriteLineAsync(
                $"{target.Fqdn}: exit code 1618 (another installation in progress). " +
                $"Waiting {backoff.TotalSeconds:0}s before attempt {attempt + 1}.",
                CancellationToken.None).ConfigureAwait(false);

            _log.LogWarning(
                "{Dc} returned 1618; waiting {Backoff}s before attempt {Next}.",
                target.Fqdn, backoff.TotalSeconds, attempt + 1);

            // Counts against the per-target timeout, which is what R7.5 means by a timeout
            // "per deployment operation".
            await Task.Delay(backoff, _time, targetToken).ConfigureAwait(false);
            attempt++;
        }
    }

    private async Task<TargetOutcome> FailAsync(
        long resultId,
        DeploymentTarget target,
        DeploymentResultRecord result,
        DeploymentStage stage,
        ErrorCategory category,
        string detail,
        DateTimeOffset startedUtc,
        int attemptCount,
        string? msiLogPath,
        string? stagingDirectory,
        List<string> warnings,
        RunLogWriter runLog) =>
        await FinishAsync(
            resultId, target, result, stage, DeploymentOutcome.Failure, category, detail, null,
            startedUtc, attemptCount, msiLogPath, stagingDirectory, warnings, false, runLog)
            .ConfigureAwait(false);

    /// <summary>
    /// Cleans up, writes the result row, and builds the in-memory outcome.
    /// </summary>
    /// <remarks>
    /// Every exit path from the per-target sequence lands here, which is what guarantees that
    /// no staging directory is left behind and no target goes unrecorded — including on the
    /// timeout and unhandled-exception paths.
    /// </remarks>
    private async Task<TargetOutcome> FinishAsync(
        long resultId,
        DeploymentTarget target,
        DeploymentResultRecord result,
        DeploymentStage stage,
        DeploymentOutcome outcome,
        ErrorCategory? category,
        string? detail,
        int? exitCode,
        DateTimeOffset startedUtc,
        int attemptCount,
        string? msiLogPath,
        string? stagingDirectory,
        List<string> warnings,
        bool prominentWarning,
        RunLogWriter runLog)
    {
        var stagingCleaned = false;

        if (stagingDirectory is not null)
        {
            // Cleanup runs on a fresh token: it must be attempted even when the target's own
            // timeout has already fired (PRD 8.4, SEC8).
            try
            {
                await _transport.CleanupAsync(target.Fqdn, stagingDirectory, CancellationToken.None)
                    .ConfigureAwait(false);
                stagingCleaned = true;
                await _deployments.MarkStagingCleanedAsync(resultId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // PRD 5.4 step 5: log a warning; do not fail the deployment. The agent is
                // installed either way, and failing a successful install because a temp
                // directory survived would misreport what happened.
                var warning =
                    $"The staging directory '{stagingDirectory}' could not be removed from " +
                    $"{target.Fqdn}: {ex.Message} The deployment result is unaffected. The path " +
                    "is recorded in the inventory database and can be cleaned up later.";
                warnings.Add(warning);
                _log.LogWarning(ex, "Cleanup failed on {Dc}.", target.Fqdn);
                await runLog.WriteLineAsync($"{target.Fqdn}: WARNING {warning}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var completedUtc = _time.GetUtcNow();

        result.Id = resultId;
        result.CompletedUtc = UtcTimestamp.Format(completedUtc);
        result.Outcome = outcome;
        result.Stage = stage;
        result.ExitCode = exitCode;
        result.AttemptCount = attemptCount;
        result.ErrorCategory = category;
        result.ErrorDetail = detail;
        result.MsiLogPath = msiLogPath;
        result.StagingPath = stagingDirectory;
        result.StagingCleaned = stagingCleaned;

        await _deployments.UpdateResultAsync(result, CancellationToken.None).ConfigureAwait(false);

        await runLog.WriteLineAsync(
            $"{target.Fqdn}: {outcome}" +
            (exitCode is not null ? $" (exit code {exitCode})" : string.Empty) +
            (detail is not null ? $" - {detail}" : string.Empty),
            CancellationToken.None).ConfigureAwait(false);

        return new TargetOutcome
        {
            Target = target,
            ResultId = resultId,
            Outcome = outcome,
            Stage = stage,
            ExitCode = exitCode,
            ErrorCategory = category,
            ErrorDetail = detail,
            AttemptCount = attemptCount,
            MsiLogPath = msiLogPath,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            RequiresProminentWarning = prominentWarning,
            Warnings = warnings,
        };
    }

    private async Task<TargetOutcome> RecordSkippedAsync(
        DeploymentTarget target,
        long runId,
        bool halted,
        bool cancelled)
    {
        var reason = halted
            ? "The run was halted by the circuit breaker before this domain controller was " +
              "attempted. Nothing was copied to it and no installation was started."
            : cancelled
                ? "The run was cancelled before this domain controller was attempted. Nothing " +
                  "was copied to it and no installation was started."
                : "This domain controller was not attempted.";

        var now = _time.GetUtcNow();
        var record = new DeploymentResultRecord
        {
            RunId = runId,
            DcId = target.DcId,
            StartedUtc = UtcTimestamp.Format(now),
            CompletedUtc = UtcTimestamp.Format(now),
            Outcome = DeploymentOutcome.Skipped,
            Stage = null,
            ErrorDetail = reason,
        };

        var resultId = await _deployments.CreateResultAsync(record, CancellationToken.None).ConfigureAwait(false);

        return new TargetOutcome
        {
            Target = target,
            ResultId = resultId,
            Outcome = DeploymentOutcome.Skipped,
            Stage = null,
            ErrorDetail = reason,
            StartedUtc = now,
            CompletedUtc = now,
        };
    }

    private static void ReportStage(
        IProgress<DeploymentProgress>? progress,
        DeploymentTarget target,
        DeploymentStage stage,
        RunState state,
        SemaphoreSlim globalSlots,
        DeploymentRequest request) =>
        Report(progress, target, TargetProgressState.Running, stage, state, globalSlots, request, null);

    private static void Report(
        IProgress<DeploymentProgress>? progress,
        DeploymentTarget target,
        TargetProgressState progressState,
        DeploymentStage? stage,
        RunState state,
        SemaphoreSlim globalSlots,
        DeploymentRequest request,
        string? message)
    {
        if (progress is null)
        {
            return;
        }

        var (completed, success, failure) = state.Snapshot();

        progress.Report(new DeploymentProgress(
            target,
            progressState,
            stage,
            completed,
            state.TotalCount,
            success,
            failure,
            request.MaxParallel - globalSlots.CurrentCount,
            message));
    }

    private sealed record RetryOutcome(ExecutionResult Result, int AttemptCount);

    /// <summary>Counters shared across concurrent operations.</summary>
    private sealed class RunState(int totalCount)
    {
        private readonly Lock _gate = new();
        private int _completed;
        private int _success;
        private int _failure;
        private int _inFlight;

        public int TotalCount { get; } = totalCount;

        public void EnterFlight() => Interlocked.Increment(ref _inFlight);

        public void ExitFlight() => Interlocked.Decrement(ref _inFlight);

        public void RecordCompletion(TargetOutcome outcome)
        {
            lock (_gate)
            {
                _completed++;
                if (outcome.IsSuccess)
                {
                    _success++;
                }
                else if (outcome.Outcome != DeploymentOutcome.Skipped)
                {
                    _failure++;
                }
            }
        }

        public (int Completed, int Success, int Failure) Snapshot()
        {
            lock (_gate)
            {
                return (_completed, _success, _failure);
            }
        }
    }
}
