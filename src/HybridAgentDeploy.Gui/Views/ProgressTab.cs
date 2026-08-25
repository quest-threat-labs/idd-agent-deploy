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

        _main.SetDeploying(true);
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
            _cancel.Enabled = false;
            _openLogs.Enabled = _lastLogDirectory is not null;
            _retryFailed.Enabled = _lastSummary?.FailedTargets.Count > 0;

            credential?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;

            _main.SetDeploying(false);

            // The inventory grid's last-deployed columns are now stale.
            await _main.RefreshInventoryAsync();
            _main.History.Reload();
        }
    }

    private void PrepareGrid(DeploymentRequest request)
    {
        _grid.Rows.Clear();
        _rowByDcId.Clear();
        _startedAt.Clear();

        foreach (var target in request.Targets)
        {
            var index = _grid.Rows.AddRow(target.Fqdn, "waiting", string.Empty, string.Empty, string.Empty);
            _rowByDcId[target.DcId] = index;
        }

        _overall.Text = $"0 / {request.Targets.Count} complete";
        _slots.Text = $"0 of {request.MaxParallel} slots busy";
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

        var confirmed = MessageBox.Show(
            this,
            "Stop starting new domain controllers?" + Environment.NewLine + Environment.NewLine +
            "Targets already in progress will finish or time out, and their staging directories " +
            "will be cleaned up. Nothing already installed is undone.",
            "Cancel deployment",
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
