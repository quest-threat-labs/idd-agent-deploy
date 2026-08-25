using System.Diagnostics;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Live deployment progress (PRD 8.4).
/// </summary>
/// <remarks>
/// The orchestrator runs on the thread pool and reports through <see cref="Progress{T}"/>,
/// which was constructed on the UI thread and therefore marshals every callback back to it.
/// No I/O happens on the UI thread (NFR2).
/// </remarks>
internal sealed class ProgressTab : UserControl
{
    private readonly MainForm _main;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window,
    };

    private readonly Label _overall = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly Label _slots = new() { AutoSize = true, Padding = new Padding(24, 8, 0, 0) };

    /// <summary>
    /// How the run is actually paced (PRD R7.1, R7.2).
    /// </summary>
    /// <remarks>
    /// The site guard is otherwise invisible: a selection concentrated in one two-DC site runs
    /// one at a time however max-parallel is set, and an operator who cannot see why concludes
    /// the tool has hung. Stated here rather than only in the run log, which is the wrong place
    /// to answer a question someone has while watching the screen.
    /// </remarks>
    private readonly Label _pacing = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(8, 2, 8, 6),
        ForeColor = Color.FromArgb(90, 90, 90),
    };

    private readonly Button _cancel = Ui.Button("Cancel");
    private readonly Button _openLogs = Ui.Button("Open log folder");
    private readonly Button _retryFailed = Ui.Button("Retry failed targets");

    private readonly Dictionary<long, int> _rowByDcId = [];
    private readonly Dictionary<long, DateTimeOffset> _startedAt = [];
    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 1000 };

    private CancellationTokenSource? _cancellation;
    private DeploymentRunSummary? _lastSummary;
    private string? _lastLogDirectory;

    public ProgressTab(MainForm main)
    {
        _main = main;

        // The same five columns serve a deployment and a validation. The operator's question is
        // the same shape either way — which domain controllers are fine and which are not — and
        // a second grid elsewhere would be a second thing to learn and a second place to look.
        _grid.Columns.Add("fqdn", "Domain controller");
        _grid.Columns.Add("stage", "Stage");
        _grid.Columns.Add("elapsed", "Elapsed");
        _grid.Columns.Add("outcome", "Outcome");
        _grid.Columns.Add("detail", "Detail");

        _grid.Columns["fqdn"]!.FillWeight = 80;
        _grid.Columns["stage"]!.FillWeight = 45;
        _grid.Columns["elapsed"]!.FillWeight = 30;
        _grid.Columns["outcome"]!.FillWeight = 55;
        _grid.Columns["detail"]!.FillWeight = 160;

        _cancel.Click += (_, _) => RequestCancellation();
        _openLogs.Click += (_, _) => OpenLogFolder();
        _retryFailed.Click += async (_, _) => await RetryFailedAsync();

        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        _cancel.Enabled = false;
        _openLogs.Enabled = false;
        _retryFailed.Enabled = false;

        var buttons = Ui.Bar(_cancel, _openLogs, _retryFailed, _overall, _slots);

        Controls.Add(_grid);
        Controls.Add(_pacing);
        Controls.Add(buttons);
    }

    /// <summary>Runs a deployment and streams its progress into the grid.</summary>
    public async Task RunAsync(DeploymentRequest request, OperatorCredential? credential)
    {
        PrepareGrid(request);

        _cancellation = new CancellationTokenSource();
        _cancel.Enabled = true;
        _openLogs.Enabled = false;
        _retryFailed.Enabled = false;
        _lastLogDirectory = request.LogDirectory;

        _pacing.Text = "Pacing: " + new SiteConcurrencyGuard(request.ActiveDcsPerSite)
            .DescribePacing(request.Targets, request.MaxParallel);

        _main.SetActivity("deployment");
        _elapsedTimer.Start();

        try
        {
            var transport = new WinRmSmbTransport(
                new TransportOptions { Credential = credential },
                _main.Logger<WinRmSmbTransport>());

            var orchestrator = new DeploymentOrchestrator(
                transport, _main.Deployments, _main.Logger<DeploymentOrchestrator>());

            // Constructed here, on the UI thread, so callbacks marshal back to it.
            var progress = new Progress<DeploymentProgress>(OnProgress);

            var summary = await orchestrator.DeployAsync(request, progress, _cancellation.Token);

            _lastSummary = summary;
            RenderFinalOutcomes(summary);
            ShowCompletionMessage(summary);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Deployment failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _elapsedTimer.Stop();
            MarkAllSlotsFree();
            _cancel.Enabled = false;
            _openLogs.Enabled = _lastLogDirectory is not null;
            _retryFailed.Enabled = _lastSummary?.FailedTargets.Count > 0;

            credential?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;

            _main.SetActivity(null);

            // The inventory grid's last-deployed columns are now stale.
            await _main.RefreshInventoryAsync();
            _main.History.Reload();
        }
    }

    /// <summary>
    /// Checks a set of targets without deploying to them, and streams the result into the same
    /// grid (PRD 11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs through <see cref="ValidationRunner"/>, which contains no code able to execute an
    /// installer. Nothing here can start an msiexec, whatever is wrong with it.
    /// </para>
    /// <para>
    /// Deliberately leaves no deployment history: <c>Retry failed targets</c> is disabled
    /// afterwards, the inventory's last-deployed columns are untouched, and the History tab is
    /// not reloaded, because nothing was deployed and none of them have changed.
    /// </para>
    /// </remarks>
    public async Task RunValidationAsync(ValidationRequest request, OperatorCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(request);

        PrepareGrid(request.Targets, request.MaxParallel, "checks");

        _cancellation = new CancellationTokenSource();
        _cancel.Enabled = true;
        _openLogs.Enabled = false;

        // A validation produces nothing to retry: the failures it reports are conditions on the
        // domain controllers, not attempts that might succeed if repeated.
        _retryFailed.Enabled = false;
        _lastSummary = null;
        _lastLogDirectory = request.LogDirectory;

        _pacing.Text = "Pacing: " + new SiteConcurrencyGuard(request.ActiveDcsPerSite)
            .DescribePacing(request.Targets, request.MaxParallel);

        _main.SetActivity("validation");
        _elapsedTimer.Start();

        try
        {
            var transport = new WinRmSmbTransport(
                new TransportOptions { Credential = credential },
                _main.Logger<WinRmSmbTransport>());

            var runner = new ValidationRunner(transport, _main.Logger<ValidationRunner>());

            // Constructed here, on the UI thread, so callbacks marshal back to it.
            var progress = new Progress<ValidationProgress>(OnValidationProgress);

            var summary = await runner.ValidateAsync(request, progress, _cancellation.Token);

            RenderValidationOutcomes(summary);
            ShowValidationMessage(summary);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Validation failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _elapsedTimer.Stop();
            MarkAllSlotsFree();
            _cancel.Enabled = false;
            _openLogs.Enabled = _lastLogDirectory is not null;

            credential?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;

            _main.SetActivity(null);
        }
    }

    /// <summary>
    /// Reports that nothing is in flight any more.
    /// </summary>
    /// <remarks>
    /// The last progress notification a target sends is emitted while it still holds its
    /// concurrency slot, so the counter never reaches zero on its own and a finished run sat
    /// there reading "1 slot(s) busy". On a tool whose whole point is that an operator can see
    /// how many domain controllers it is touching, that is not a cosmetic detail.
    /// </remarks>
    private void MarkAllSlotsFree() => _slots.Text = "0 slot(s) busy";

    private void PrepareGrid(DeploymentRequest request) =>
        PrepareGrid(request.Targets, request.MaxParallel, "complete");

    private void PrepareGrid(IReadOnlyList<DeploymentTarget> targets, int maxParallel, string completionNoun)
    {
        _grid.Rows.Clear();
        _rowByDcId.Clear();
        _startedAt.Clear();

        foreach (var target in targets)
        {
            var index = _grid.Rows.AddRow(target.Fqdn, "waiting", string.Empty, string.Empty, string.Empty);
            _rowByDcId[target.DcId] = index;
        }

        _overall.Text = $"0 / {targets.Count} {completionNoun}";
        _slots.Text = $"0 of {maxParallel} slots busy";
    }

    private void OnProgress(DeploymentProgress progress)
    {
        if (!_rowByDcId.TryGetValue(progress.Target.DcId, out var index))
        {
            return;
        }

        var row = _grid.Rows[index];

        if (progress.State == TargetProgressState.Running)
        {
            _startedAt.TryAdd(progress.Target.DcId, DateTimeOffset.UtcNow);
            row.Cells["stage"].Value = OutcomeStyle.ForStage(progress.Stage).Text;
        }
        else if (progress.State == TargetProgressState.WaitingForSlot)
        {
            row.Cells["stage"].Value = "waiting for a slot";
        }
        else if (progress.State == TargetProgressState.Completed)
        {
            row.Cells["stage"].Value = "done";
        }

        _overall.Text =
            $"{progress.CompletedCount} / {progress.TotalCount} complete — " +
            $"{progress.SuccessCount} succeeded, {progress.FailureCount} failed";

        // PRD 8.4: a visible indicator of how many slots are currently occupied.
        _slots.Text = $"{progress.OccupiedSlots} slot(s) busy";
    }

    private void OnValidationProgress(ValidationProgress progress)
    {
        if (!_rowByDcId.TryGetValue(progress.Target.DcId, out var index))
        {
            return;
        }

        var row = _grid.Rows[index];

        row.Cells["stage"].Value = progress.State switch
        {
            TargetProgressState.Running => progress.CheckName ?? "checking",
            TargetProgressState.WaitingForSlot => "waiting for a slot",
            TargetProgressState.Completed => "done",
            _ => row.Cells["stage"].Value,
        };

        if (progress.State == TargetProgressState.Running)
        {
            _startedAt.TryAdd(progress.Target.DcId, DateTimeOffset.UtcNow);
        }

        _overall.Text =
            $"{progress.CompletedCount} / {progress.TotalCount} checked — " +
            $"{progress.ReadyCount} ready, {progress.ProblemCount} with problems";

        _slots.Text = $"{progress.OccupiedSlots} slot(s) busy";
    }

    /// <summary>
    /// Fills in the grid once validation has finished.
    /// </summary>
    /// <remarks>
    /// A target that is reachable but already carries a newer agent is shown amber, not green.
    /// Nothing is wrong with the domain controller, but deploying to it would return 1638, and
    /// a green row invites the operator to press Start and find that out the hard way.
    /// </remarks>
    private void RenderValidationOutcomes(ValidationRunSummary summary)
    {
        foreach (var outcome in summary.Outcomes)
        {
            if (!_rowByDcId.TryGetValue(outcome.Target.DcId, out var index))
            {
                continue;
            }

            var row = _grid.Rows[index];
            var appearance = OutcomeStyle.ForValidation(outcome);

            row.Cells["stage"].Value = outcome.Passed
                ? $"{outcome.Checks.Count} checks"
                : outcome.Failures[0].Name.ToLowerInvariant();
            row.Cells["elapsed"].Value = outcome.Duration.ToString(@"mm\:ss");
            row.Cells["outcome"].Value = appearance.Text;
            row.Cells["detail"].Value = outcome.Summary;

            row.Cells["outcome"].Style.BackColor = appearance.BackColor;
            row.Cells["outcome"].Style.ForeColor = appearance.ForeColor;
        }
    }

    private void ShowValidationMessage(ValidationRunSummary summary)
    {
        var lines = new List<string>
        {
            $"{summary.ReadyCount} ready, {summary.ProblemCount} with problems.",
            string.Empty,
            "Nothing was installed. No deployment history was recorded.",
        };

        if (summary.WouldBeRefusedCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"{summary.WouldBeRefusedCount} domain controller(s) are reachable but already " +
                "carry a newer agent than this package. msiexec would refuse the install with " +
                "exit code 1638.");
        }

        if (summary.WasCancelled)
        {
            lines.Add(string.Empty);
            lines.Add(
                "The run was cancelled. Targets already in flight finished and their probe " +
                "files were removed; targets not yet started were not touched.");
        }

        lines.Add(string.Empty);
        lines.Add($"Logs: {summary.LogDirectory}");

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            "Validation complete",
            MessageBoxButtons.OK,
            summary.ProblemCount > 0 || summary.WouldBeRefusedCount > 0
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information);
    }

    private void UpdateElapsed()
    {
        foreach (var (dcId, started) in _startedAt)
        {
            if (_rowByDcId.TryGetValue(dcId, out var index) &&
                _grid.Rows[index].Cells["outcome"].Value is null or "")
            {
                _grid.Rows[index].Cells["elapsed"].Value =
                    (DateTimeOffset.UtcNow - started).ToString(@"mm\:ss");
            }
        }
    }

    private void RenderFinalOutcomes(DeploymentRunSummary summary)
    {
        foreach (var outcome in summary.Outcomes)
        {
            if (!_rowByDcId.TryGetValue(outcome.Target.DcId, out var index))
            {
                continue;
            }

            var row = _grid.Rows[index];
            var appearance = OutcomeStyle.For(outcome.Outcome);

            row.Cells["stage"].Value = outcome.Stage?.ToString().ToLowerInvariant() ?? string.Empty;
            row.Cells["elapsed"].Value = outcome.Duration.ToString(@"mm\:ss");
            row.Cells["outcome"].Value = appearance.Text;
            row.Cells["detail"].Value = outcome.ErrorDetail ??
                (outcome.ExitCode is not null ? $"exit code {outcome.ExitCode}" : string.Empty);

            row.Cells["outcome"].Style.BackColor = appearance.BackColor;
            row.Cells["outcome"].Style.ForeColor = appearance.ForeColor;
        }
    }

    private void ShowCompletionMessage(DeploymentRunSummary summary)
    {
        var lines = new List<string>
        {
            $"{summary.SuccessCount} succeeded, {summary.FailureCount} failed, " +
            $"{summary.SkippedCount} not attempted.",
        };

        if (summary.WasHalted)
        {
            lines.Add(string.Empty);
            lines.Add("THE RUN WAS HALTED BY THE CIRCUIT BREAKER.");
            lines.Add(summary.HaltReason ?? string.Empty);
        }

        if (summary.WasCancelled)
        {
            lines.Add(string.Empty);
            lines.Add(
                "The run was cancelled. Targets already in flight were allowed to finish and " +
                "their staging directories were cleaned up.");
        }

        // PRD 10.1: a reboot initiated on a domain controller is significant and must not be
        // left sitting quietly inside a success count.
        foreach (var loud in summary.Outcomes.Where(o => o.RequiresProminentWarning))
        {
            lines.Add(string.Empty);
            lines.Add($"{loud.Target.Fqdn}: A REBOOT WAS INITIATED on this domain controller " +
                      $"(exit code {loud.ExitCode}).");
        }

        lines.Add(string.Empty);
        lines.Add($"Logs: {summary.LogDirectory}");

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            summary.WasHalted ? "Deployment halted" : "Deployment complete",
            MessageBoxButtons.OK,
            summary.WasHalted || summary.FailureCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>
    /// PRD 8.4: cancelling stops new targets from starting and lets in-flight ones complete or
    /// time out. An msiexec already running on a domain controller is never abandoned.
    /// </summary>
    private void RequestCancellation()
    {
        if (_cancellation is null)
        {
            return;
        }

        var validating = _main.ActivityInFlight == "validation";

        var confirmed = MessageBox.Show(
            this,
            "Stop starting new domain controllers?" + Environment.NewLine + Environment.NewLine +
            (validating
                ? "Targets already in progress will finish or time out, and their probe files " +
                  "will be removed. Nothing was installed on any of them."
                : "Targets already in progress will finish or time out, and their staging " +
                  "directories will be cleaned up. Nothing already installed is undone."),
            validating ? "Cancel validation" : "Cancel deployment",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        if (confirmed)
        {
            _cancel.Enabled = false;
            _cancellation.Cancel();
        }
    }

    private void OpenLogFolder()
    {
        if (_lastLogDirectory is null || !Directory.Exists(_lastLogDirectory))
        {
            MessageBox.Show(this, "The log folder no longer exists.", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(_lastLogDirectory) { UseShellExecute = true });
    }

    /// <summary>
    /// Re-selects the failed targets so the operator can run them again (PRD 8.4).
    /// </summary>
    /// <remarks>
    /// A retry is a new run rather than an append: it gets its own run GUID and its own log
    /// directory, which keeps the history honest about what was attempted when. Targets that
    /// were never attempted because the breaker halted the run are excluded — retrying those
    /// without fixing the cause would simply re-trip it.
    /// </remarks>
    private async Task RetryFailedAsync()
    {
        if (_lastSummary is null || _lastSummary.FailedTargets.Count == 0)
        {
            return;
        }

        _main.Selection.Clear();
        foreach (var target in _lastSummary.FailedTargets)
        {
            _main.Selection.Set(target.DcId, true);
        }

        await _main.RefreshInventoryAsync();
        _main.OnSelectionChanged();

        MessageBox.Show(
            this,
            $"{_lastSummary.FailedTargets.Count} failed target(s) are now selected on the " +
            "Inventory tab. Review them on the Deploy tab, then start the run again." +
            Environment.NewLine + Environment.NewLine +
            "This will be recorded as a new deployment run.",
            "Failed targets selected",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
