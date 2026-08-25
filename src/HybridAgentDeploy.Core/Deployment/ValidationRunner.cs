using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Logging;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>Everything one validation run needs.</summary>
/// <remarks>
/// Values are clamped on construction, exactly as <see cref="DeploymentRequest"/> clamps them.
/// Validation touches the same domain controllers over the same channels and is subject to the
/// same ceilings; a second entry point that paced itself differently would be a way around
/// them.
/// </remarks>
public sealed class ValidationRequest
{
    /// <summary>
    /// The package each target's installed agent is compared against.
    /// </summary>
    /// <remarks>
    /// Required. Validation exists to answer "will this deployment work", and half that
    /// answer is what the package would do to what is already installed. Nothing is staged
    /// from it and it is never executed.
    /// </remarks>
    public required MsiPackageInfo Msi { get; init; }

    /// <summary>
    /// Whether the deployment this is checking would pass <c>SG=1</c>.
    /// </summary>
    /// <remarks>
    /// Needed because the installer refuses an upgrade that changes an agent from one product
    /// to the other, so "will this deployment work" cannot be answered without knowing which
    /// product it targets.
    /// </remarks>
    public bool CloudMode { get; init; } = true;

    /// <summary>The product the run would configure the agent for.</summary>
    public AgentMode RequestedMode => CloudMode ? AgentMode.IdentityDefense : AgentMode.ChangeAuditor;

    public required IReadOnlyList<DeploymentTarget> Targets { get; init; }

    /// <summary>DOMAIN\user. Recorded against the run; never a password (SEC1, PRD 12.3).</summary>
    public required string OperatorAccount { get; init; }

    /// <summary>The run's own log directory, separate from any deployment's.</summary>
    public required string LogDirectory { get; init; }

    /// <summary>Active DCs per site, for the R7.2 guard. Counts the inventory, not the selection.</summary>
    public IReadOnlyDictionary<string, int> ActiveDcsPerSite { get; init; } =
        new Dictionary<string, int>();

    public InventoryStatus? InventoryStatus { get; init; }

    private readonly int _maxParallel = DeploymentLimits.MaxConcurrencyCeiling;

    /// <summary>Requested concurrency, clamped to 1..5 on assignment (R7.1).</summary>
    public int MaxParallel
    {
        get => _maxParallel;
        init => _maxParallel = DeploymentLimits.ClampParallelism(value);
    }

    public bool ParallelismWasClamped { get; private init; }

    private readonly TimeSpan _perTargetTimeout = TimeSpan.FromMinutes(DeploymentLimits.MinTimeoutMinutes);

    /// <summary>Bounds the whole per-target sequence, as R7.5 does for a deployment.</summary>
    public TimeSpan PerTargetTimeout
    {
        get => _perTargetTimeout;
        init => _perTargetTimeout = DeploymentLimits.ClampTimeout((int)Math.Round(value.TotalMinutes));
    }

    public static ValidationRequest Create(
        MsiPackageInfo msi,
        IReadOnlyList<DeploymentTarget> targets,
        string operatorAccount,
        string logDirectory,
        bool cloudMode = true,
        int maxParallel = DeploymentLimits.MaxConcurrencyCeiling,
        int timeoutMinutes = DeploymentLimits.MinTimeoutMinutes,
        IReadOnlyDictionary<string, int>? activeDcsPerSite = null,
        InventoryStatus? inventoryStatus = null) =>
        new()
        {
            Msi = msi,
            CloudMode = cloudMode,
            Targets = targets,
            OperatorAccount = operatorAccount,
            LogDirectory = logDirectory,
            MaxParallel = maxParallel,
            PerTargetTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            ActiveDcsPerSite = activeDcsPerSite ?? new Dictionary<string, int>(),
            InventoryStatus = inventoryStatus,
            ParallelismWasClamped = DeploymentLimits.WasParallelismClamped(maxParallel),
        };
}

/// <summary>
/// What a target is currently doing during validation, for the progress grid.
/// </summary>
/// <param name="CheckName">
/// The check under way, or the one that finished. Named rather than numbered so a run that
/// stalls says which check it stalled on.
/// </param>
public sealed record ValidationProgress(
    DeploymentTarget Target,
    TargetProgressState State,
    string? CheckName,
    int CompletedCount,
    int TotalCount,
    int ReadyCount,
    int ProblemCount,
    int OccupiedSlots);

/// <summary>
/// Validates a set of domain controllers, under the same pacing a deployment runs under
/// (PRD 11).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately parallel to <see cref="DeploymentOrchestrator"/> rather than folded into it.
/// A flag on the orchestrator that skipped the install would put "do not run msiexec" one
/// boolean away from "run msiexec" on a Tier 0 host, and every future change to the
/// orchestrator would have to be read twice. These are different operations with different
/// risks, so they are different types — and this one contains no code that can execute an
/// installer.
/// </para>
/// <para>
/// <b>What it shares with a deployment:</b> the concurrency ceiling of five (R7.1), the site
/// guard (R7.2), the per-target timeout (R7.5), and the rule that a slot spans the entire
/// per-target sequence including cleanup. Validation opens real sessions and writes real files
/// to domain controllers, so it is paced like anything else that does.
/// </para>
/// <para>
/// <b>What it deliberately does not share:</b> the circuit breaker (R7.3), and any row in
/// <c>deployment_run</c>. The breaker exists to stop a bad deployment part-way through sixty
/// controllers; validation installs nothing, and an operator validating sixty DCs specifically
/// wants the list of all the broken ones rather than the first three. Halting there would
/// defeat the purpose of the screen. And nothing was deployed, so recording a deployment would
/// make the history lie about what ran against the customer's domain controllers.
/// </para>
/// </remarks>
public sealed class ValidationRunner
{
    private readonly ITargetTransport _transport;
    private readonly ILogger<ValidationRunner> _log;
    private readonly TimeProvider _time;

    public ValidationRunner(
        ITargetTransport transport,
        ILogger<ValidationRunner>? log = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _log = log ?? NullLogger<ValidationRunner>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Validates every target in the request.</summary>
    /// <param name="cancellationToken">
    /// Stops <em>starting</em> new targets. Targets already in flight run to completion, so
    /// their probe directories are always removed rather than abandoned on a domain
    /// controller.
    /// </param>
    public async Task<ValidationRunSummary> ValidateAsync(
        ValidationRequest request,
        IProgress<ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runGuid = Guid.NewGuid();
        var startedUtc = _time.GetUtcNow();
        var stagingDirectory = StagingPaths.ForValidation(runGuid);

        var siteGuard = new SiteConcurrencyGuard(request.ActiveDcsPerSite);

        await using var log = await ValidationLogWriter
            .CreateAsync(request.LogDirectory, CancellationToken.None).ConfigureAwait(false);

        await log.WriteHeaderAsync(request, runGuid, startedUtc, stagingDirectory, siteGuard,
            CancellationToken.None).ConfigureAwait(false);

        var validator = new TargetValidator(_transport);
        var state = new RunState(request.Targets.Count);

        // R7.1: a slot spans the entire per-target sequence, probe through cleanup.
        using var globalSlots = new SemaphoreSlim(request.MaxParallel, request.MaxParallel);

        var tasks = new List<Task<ValidationOutcome>>(request.Targets.Count);
        var unstarted = new List<DeploymentTarget>();

        foreach (var target in request.Targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                unstarted.Add(target);
                continue;
            }

            await globalSlots.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                globalSlots.Release();
                unstarted.Add(target);
                continue;
            }

            tasks.Add(RunTargetAsync(
                validator, target, request, siteGuard, state, globalSlots, stagingDirectory,
                log, progress));
        }

        var outcomes = new List<ValidationOutcome>(request.Targets.Count);
        foreach (var task in tasks)
        {
            outcomes.Add(await task.ConfigureAwait(false));
        }

        // Targets never reached are listed rather than omitted, so a cancelled run does not
        // leave the operator believing the rest passed.
        foreach (var target in unstarted)
        {
            var now = _time.GetUtcNow();
            outcomes.Add(new ValidationOutcome
            {
                Target = target,
                Checks =
                [
                    new ValidationCheck(
                        "Not checked",
                        false,
                        $"{target.Fqdn} was not checked: the run was cancelled before it was " +
                        "reached. Nothing was written to it."),
                ],
                RequestedMode = request.RequestedMode,
                StartedUtc = now,
                CompletedUtc = now,
            });
        }

        var summary = new ValidationRunSummary
        {
            RunGuid = runGuid,
            Outcomes = outcomes,
            StartedUtc = startedUtc,
            CompletedUtc = _time.GetUtcNow(),
            LogDirectory = request.LogDirectory,
            WasCancelled = cancellationToken.IsCancellationRequested,
        };

        await log.WriteSummaryAsync(summary, CancellationToken.None).ConfigureAwait(false);
        await log.WriteResultsCsvAsync(summary, CancellationToken.None).ConfigureAwait(false);

        return summary;
    }

    private async Task<ValidationOutcome> RunTargetAsync(
        TargetValidator validator,
        DeploymentTarget target,
        ValidationRequest request,
        SiteConcurrencyGuard siteGuard,
        RunState state,
        SemaphoreSlim globalSlots,
        string stagingDirectory,
        ValidationLogWriter log,
        IProgress<ValidationProgress>? progress)
    {
        // Yield so the caller's loop continues acquiring slots rather than running the first
        // target inline.
        await Task.Yield();

        try
        {
            Report(progress, target, TargetProgressState.WaitingForSlot, null, state, globalSlots, request);

            // R7.2: a secondary gate, acquired after the global semaphore.
            using var siteSlot = await siteGuard.AcquireAsync(target.SiteName, CancellationToken.None)
                .ConfigureAwait(false);

            Report(progress, target, TargetProgressState.Running, "checking", state, globalSlots, request);
            await log.WriteLineAsync($"{target.Fqdn}: checking", CancellationToken.None)
                .ConfigureAwait(false);

            // Bounded by its own timeout rather than by the operator's cancel, so a target
            // already in flight finishes and cleans up after itself (R7.5).
            using var timeoutSource = new CancellationTokenSource(request.PerTargetTimeout, _time);

            ValidationOutcome outcome;
            var startedUtc = _time.GetUtcNow();

            try
            {
                outcome = await validator.ValidateAsync(
                    target, request.Msi, request.RequestedMode, stagingDirectory,
                    request.PerTargetTimeout, startedUtc, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                outcome = new ValidationOutcome
                {
                    Target = target,
                    Checks =
                    [
                        new ValidationCheck(
                            "Responsive",
                            false,
                            $"{target.Fqdn} did not answer within " +
                            $"{request.PerTargetTimeout.TotalMinutes:0} minutes. A deployment " +
                            "would time out on this host too. Confirm the domain controller is " +
                            "responsive and that WinRM is not blocked by a slow or filtered path."),
                    ],
                    RequestedMode = request.RequestedMode,
                    ErrorCategory = ErrorCategory.Timeout,
                    StartedUtc = startedUtc,
                    CompletedUtc = _time.GetUtcNow(),
                };
            }

            state.Record(outcome);
            await log.WriteOutcomeAsync(outcome, CancellationToken.None).ConfigureAwait(false);

            if (!outcome.Passed)
            {
                _log.LogWarning("{Dc} failed validation: {Detail}", target.Fqdn, outcome.Summary);
            }

            Report(progress, target, TargetProgressState.Completed, null, state, globalSlots, request);
            return outcome;
        }
        finally
        {
            // R7.1: released only after the whole sequence, including the probe's removal.
            globalSlots.Release();
        }
    }

    private static void Report(
        IProgress<ValidationProgress>? progress,
        DeploymentTarget target,
        TargetProgressState progressState,
        string? checkName,
        RunState state,
        SemaphoreSlim globalSlots,
        ValidationRequest request)
    {
        if (progress is null)
        {
            return;
        }

        var (completed, ready, problems) = state.Snapshot();

        progress.Report(new ValidationProgress(
            target,
            progressState,
            checkName,
            completed,
            state.TotalCount,
            ready,
            problems,
            request.MaxParallel - globalSlots.CurrentCount));
    }

    /// <summary>Counters shared across concurrent operations.</summary>
    private sealed class RunState(int totalCount)
    {
        private readonly Lock _gate = new();
        private int _completed;
        private int _ready;
        private int _problems;

        public int TotalCount { get; } = totalCount;

        public void Record(ValidationOutcome outcome)
        {
            lock (_gate)
            {
                _completed++;
                if (outcome.Passed)
                {
                    _ready++;
                }
                else
                {
                    _problems++;
                }
            }
        }

        public (int Completed, int Ready, int Problems) Snapshot()
        {
            lock (_gate)
            {
                return (_completed, _ready, _problems);
            }
        }
    }
}
