using System.Security;
using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Deployment setup (PRD 8.3): pick the package, enter the Org ID, review what will run, and
/// start.
/// </summary>
internal sealed class DeployTab : UserControl
{
    private readonly MainForm _main;

    /// <summary>
    /// A representative path, used to size the package path box.
    /// </summary>
    /// <remarks>
    /// A UNC path to a package on a share, which is where these tend to live — NFR4 expects the
    /// tool itself to be run from one. A path the operator cannot read in full is a path they
    /// cannot check before it is installed on a domain controller.
    /// </remarks>
    private const string RepresentativePath =
        @"\\fileserver.corp.local\software$\quest\agents\Quest Change Auditor Agent (x64).msi";

    /// <summary>
    /// The widest line the details box will ever hold: the SHA-256, 64 hex characters after
    /// its padded label.
    /// </summary>
    private const string WidestDetailLine =
        "Product version : 0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Lines shown in the details box; see the text built in BrowseForMsiAsync.</summary>
    private const int DetailLineCount = 6;

    private readonly TextBox _msiPath = new() { ReadOnly = true };

    /// <summary>
    /// The selected package's identity (PRD 8.3).
    /// </summary>
    /// <remarks>
    /// Fixed-width, because the lines are written with padded labels so their colons line up —
    /// which silently did nothing while this used the proportional default font. Sized to show
    /// all six lines at once with no scrollbar: the operator is meant to read the version and
    /// the hash before deploying, and a box that needs scrolling to reveal half its content
    /// invites them not to. Still a text box rather than a label so the hash can be selected
    /// and copied.
    /// </remarks>
    private readonly TextBox _msiDetails = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.None,
        WordWrap = false,
        BackColor = SystemColors.Control,
        Font = Ui.Monospace(9.5f),
    };

    private readonly TextBox _orgId = new() { Width = 380 };
    private readonly CheckBox _cloudMode = new() { Text = "Cloud mode (SG=1)", Checked = true, AutoSize = true };
    private readonly NumericUpDown _maxParallel = new()
    {
        Minimum = DeploymentLimits.MinConcurrency,
        Maximum = DeploymentLimits.MaxConcurrencyCeiling,
        Value = DeploymentLimits.MaxConcurrencyCeiling,
        Width = 60,
    };

    private readonly NumericUpDown _timeout = new()
    {
        Minimum = DeploymentLimits.MinTimeoutMinutes,
        Maximum = DeploymentLimits.MaxTimeoutMinutes,
        Value = DeploymentLimits.DefaultTimeoutMinutes,
        Width = 60,
    };

    private readonly RadioButton _currentIdentity = new()
    {
        Text = "Current Windows identity",
        Checked = true,
        AutoSize = true,
    };

    private readonly RadioButton _alternateIdentity = new() { Text = "Alternate account:", AutoSize = true };
    private readonly TextBox _userName = new() { Width = 200, Enabled = false, PlaceholderText = @"DOMAIN\user" };
    private readonly TextBox _password = new() { Width = 160, Enabled = false, UseSystemPasswordChar = true };

    /// <summary>
    /// Lines the command preview shows before it needs to scroll.
    /// </summary>
    /// <remarks>
    /// The real command runs to roughly two hundred characters once a staging path and an Org
    /// ID are in it, so four lines covers it with the box wrapping rather than scrolling. This
    /// is the text PRD 8.3 asks the operator to check before anything runs on a domain
    /// controller, so it should be readable in one go.
    /// </remarks>
    private const int CommandPreviewLines = 4;

    private readonly TextBox _commandPreview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Control,
        Font = Ui.Monospace(9.5f),
    };

    private readonly Label _targetSummary = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private readonly Label _blockingReason = new() { ForeColor = Color.FromArgb(140, 20, 30) };
    private readonly Button _start = Ui.Button("Start deployment");

    private MsiPackageInfo? _msi;

    public DeployTab(MainForm main)
    {
        _main = main;

        _cloudMode.Tag = "When set, the agent is configured to report to the Identity Defense cloud tenant.";
        var tips = new ToolTip();
        tips.SetToolTip(_cloudMode,
            "Passes SG=1 to the installer, configuring the agent to report to the Identity " +
            "Defense cloud tenant. Leave this on unless you have been told otherwise.");
        tips.SetToolTip(_maxParallel,
            $"Domain controllers deployed to at once. Hard ceiling of " +
            $"{DeploymentLimits.MaxConcurrencyCeiling}; within a single Active Directory site, " +
            "never more than half its controllers.");

        _orgId.TextChanged += (_, _) => RefreshSelectionSummary();
        _cloudMode.CheckedChanged += (_, _) => RefreshSelectionSummary();
        _password.TextChanged += (_, _) => RefreshSelectionSummary();
        _userName.TextChanged += (_, _) => RefreshSelectionSummary();

        _currentIdentity.CheckedChanged += (_, _) => OnIdentityModeChanged();
        _alternateIdentity.CheckedChanged += (_, _) => OnIdentityModeChanged();

        _start.Click += async (_, _) => await StartAsync();

        // Sized from the fonts these controls actually render with, not from pixel counts
        // guessed here. See Ui.SizeToContent.
        Ui.SizeToContent(_msiDetails, DetailLineCount, WidestDetailLine);
        Ui.SizeToContent(_commandPreview, CommandPreviewLines, WidestDetailLine);

        _msiPath.Width = TextRenderer.MeasureText(RepresentativePath, _msiPath.Font).Width + 24;
        _commandPreview.Width = Math.Max(_commandPreview.Width, _msiDetails.Width);

        Controls.Add(BuildLayout());
        RefreshSelectionSummary();
    }

    private Control BuildLayout()
    {
        var browse = Ui.Button("Browse...");
        browse.Click += async (_, _) => await BrowseForMsiAsync();

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };

        layout.Controls.Add(Ui.Heading("Installer package"));
        layout.Controls.Add(PackageRow(browse));
        layout.Controls.Add(_msiDetails);

        layout.Controls.Add(Ui.Heading("Org ID"));
        layout.Controls.Add(Ui.Row(_orgId));

        layout.Controls.Add(Ui.Heading("Options"));
        layout.Controls.Add(Ui.Row(_cloudMode));
        layout.Controls.Add(Ui.Row(Ui.Label("Max parallel:"), _maxParallel,
            Ui.Label("Per-target timeout (minutes):"), _timeout));

        layout.Controls.Add(Ui.Heading("Run as"));
        layout.Controls.Add(RunAsPanel(_currentIdentity, _alternateIdentity, _userName, _password));

        layout.Controls.Add(Ui.Heading("Targets"));
        layout.Controls.Add(_targetSummary);

        layout.Controls.Add(Ui.Heading("Command that will run on each domain controller"));
        layout.Controls.Add(_commandPreview);

        // Start is docked to the bottom rather than left at the end of the scrolling form, so
        // the primary action is always reachable however short the window is. The reason a run
        // cannot start sits beside it and fills the remaining width, wrapping as needed: a
        // fixed width truncated it mid-sentence ("...must be readable before it can be"), which
        // leaves the operator with a disabled button and half an explanation.
        _blockingReason.AutoSize = false;
        _blockingReason.Dock = DockStyle.Fill;
        _blockingReason.TextAlign = ContentAlignment.MiddleLeft;
        _blockingReason.Padding = new Padding(16, 0, 8, 0);

        _start.Dock = DockStyle.Left;
        _start.Margin = new Padding(0);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(0, 8, 0, 4) };

        // Fill is added first so the docked button claims its space from the left edge.
        actions.Controls.Add(_blockingReason);
        actions.Controls.Add(_start);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(layout);
        host.Controls.Add(actions);

        return host;
    }

    /// <summary>
    /// The "Run as" choice: both options and the alternate account's fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both radio buttons must be direct children of <em>one</em> container. WinForms scopes
    /// mutual exclusion to the immediate parent, so putting each in its own row panel — as this
    /// screen originally did — made them two groups of one. Selecting "Alternate account" left
    /// "Current Windows identity" selected as well.
    /// </para>
    /// <para>
    /// That was not merely untidy. Whether alternate credentials are used is decided by
    /// <c>_alternateIdentity.Checked</c>, so an operator who filled in an alternate account and
    /// then clicked back to their current identity would have deployed under the alternate
    /// account anyway, while the screen showed otherwise. Running against a domain controller
    /// as an account other than the one the operator believes they chose is exactly the kind of
    /// surprise this tool exists to avoid.
    /// </para>
    /// </remarks>
    internal static Control RunAsPanel(
        RadioButton currentIdentity,
        RadioButton alternateIdentity,
        Control userName,
        Control password)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0),
        };

        for (var column = 0; column < 4; column++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        panel.Controls.Add(currentIdentity, 0, 0);
        panel.SetColumnSpan(currentIdentity, 4);

        panel.Controls.Add(alternateIdentity, 0, 1);
        panel.Controls.Add(userName, 1, 1);
        panel.Controls.Add(Ui.Label("Password:"), 2, 1);
        panel.Controls.Add(password, 3, 1);

        // Vertically centre the fields against the radio button on the same row.
        userName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        password.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        return panel;
    }

    /// <summary>
    /// The package path beside its Browse button, with the path centred against it.
    /// </summary>
    /// <remarks>
    /// A table rather than a flow panel. The button is taller than the text box, and a flow
    /// panel top-aligns its children, which left the path riding against the button's top
    /// edge. Anchoring the box left and right — but neither top nor bottom — makes the table
    /// stretch it across the column and centre it vertically in the row.
    /// </remarks>
    private Control PackageRow(Button browse)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _msiPath.Width));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _msiPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _msiPath.Margin = new Padding(0, 3, 8, 3);

        row.Controls.Add(_msiPath, 0, 0);
        row.Controls.Add(browse, 1, 0);

        return row;
    }

    private void OnIdentityModeChanged()
    {
        _userName.Enabled = _alternateIdentity.Checked;
        _password.Enabled = _alternateIdentity.Checked;
        RefreshSelectionSummary();
    }

    private async Task BrowseForMsiAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the agent MSI",
            Filter = "Windows Installer packages (*.msi)|*.msi",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _msiPath.Text = dialog.FileName;

        try
        {
            _msi = await _main.MsiInspector.InspectAsync(dialog.FileName, CancellationToken.None);
        }
        catch (MsiInspectionException ex)
        {
            // PRD 8.3: if extraction fails, deployment is blocked.
            _msi = null;
            _msiDetails.Text = ex.Message;
            RefreshSelectionSummary();

            MessageBox.Show(this, ex.Message, "Package could not be read",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _msiDetails.Text = string.Join(Environment.NewLine,
        [
            $"Product name    : {_msi.ProductName ?? "(not present)"}",
            $"Product version : {_msi.ProductVersion}",
            $"Product code    : {_msi.ProductCode ?? "(not present)"}",
            $"Upgrade code    : {_msi.UpgradeCode ?? "(not present)"}",
            $"Size            : {_msi.FileSizeBytes:N0} bytes",
            $"SHA-256         : {_msi.Sha256}",
        ]);

        RefreshSelectionSummary();

        if (!_msi.LooksLikeExpectedProduct)
        {
            // PRD 5.5: warn prominently, but never hard-block on a name that may change.
            MessageBox.Show(
                this,
                $"'{_msi.ProductName ?? "(no product name)"}' does not look like a Quest Change " +
                "Auditor agent package." + Environment.NewLine + Environment.NewLine +
                "You can still deploy it, but confirm this is the right file first.",
                "Unexpected product name",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>Recomputes the target summary, the command preview, and the Start button state.</summary>
    public void RefreshSelectionSummary()
    {
        var selected = _main.Inventory.Where(r => _main.Selection.IsSelected(r.DcId)).ToList();

        var state = new DeploymentFormState
        {
            Msi = _msi,
            OrgId = _orgId.Text,
            SelectedTargetCount = selected.Count,
            IsDeploying = _main.IsDeploying,
            AlternateCredentialIncomplete = _alternateIdentity.Checked &&
                (string.IsNullOrWhiteSpace(_userName.Text) || _password.Text.Length == 0),
        };

        _targetSummary.Text = selected.Count == 0
            ? "No domain controllers selected. Tick them on the Inventory tab."
            : $"{selected.Count} selected — " +
              string.Join(" · ", DeploymentFormState.SummariseBySite(selected));

        _commandPreview.Text = state.CommandPreview(_cloudMode.Checked);

        _start.Enabled = state.CanStart;
        _blockingReason.Text = state.BlockingReason ?? string.Empty;

        // Warnings on the Org ID are advisory (PRD 8.3) and shown without blocking.
        var orgId = state.OrgIdValidation;
        if (orgId.IsValid && orgId.Warnings.Count > 0)
        {
            _blockingReason.ForeColor = Color.FromArgb(122, 74, 0);
            _blockingReason.Text = string.Join(Environment.NewLine, orgId.Warnings);
        }
        else
        {
            _blockingReason.ForeColor = Color.FromArgb(140, 20, 30);
        }
    }

    private async Task StartAsync()
    {
        var selected = _main.Inventory.Where(r => _main.Selection.IsSelected(r.DcId)).ToList();

        var state = new DeploymentFormState
        {
            Msi = _msi,
            OrgId = _orgId.Text,
            SelectedTargetCount = selected.Count,
            IsDeploying = _main.IsDeploying,
        };

        if (!state.CanStart)
        {
            return;
        }

        var targets = selected.Select(r => new DeploymentTarget(r.DcId, r.Fqdn, r.SiteName)).ToList();
        var logDirectory = AppConfigurationLoader.RunLogDirectory(
            _main.Configuration.LogRootPath, Guid.NewGuid(), DateTimeOffset.UtcNow);

        // PRD 11: the blocking checklist, before the confirmation dialog — there is no point
        // asking the operator to confirm a run that cannot start.
        var preflight = await new PreflightValidator(_main.MsiInspector, _main.Resolver)
            .ValidateAsync(_msi!.FilePath, _orgId.Text, targets, logDirectory, CancellationToken.None);

        if (!preflight.Passed)
        {
            MessageBox.Show(
                this,
                "Pre-flight validation failed; nothing was deployed." +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine,
                    preflight.Failures.Select(f => $"{f.Name}: {f.Detail}")),
                "Cannot start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // PRD 8.3: gated on a confirmation dialog that restates the target count and the Org ID.
        var hidden = _main.Selection.HiddenSelectedCount(
            _main.Inventory.Where(r => _main.Selection.IsSelected(r.DcId)));

        var confirmed = MessageBox.Show(
            this,
            state.ConfirmationPrompt([.. targets.Select(t => t.Fqdn)], hidden),
            "Confirm deployment",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        if (!confirmed)
        {
            return;
        }

        OperatorCredential? credential = null;
        if (_alternateIdentity.Checked)
        {
            var secure = new SecureString();
            foreach (var c in _password.Text)
            {
                secure.AppendChar(c);
            }

            credential = new OperatorCredential(_userName.Text, secure);

            // SEC1: the password does not linger in a control's buffer once it is held
            // securely for the run.
            _password.Clear();
        }

        var selection = await _main.TargetSelector.ResolveAsync([], [], all: true, CancellationToken.None);

        var request = DeploymentRequest.Create(
            msi: preflight.Msi!,
            orgId: preflight.OrgId!,
            targets: targets,
            operatorAccount: credential?.AccountName ?? OperatorCredential.CurrentWindowsAccountName(),
            logDirectory: logDirectory,
            cloudMode: _cloudMode.Checked,
            maxParallel: (int)_maxParallel.Value,
            timeoutMinutes: (int)_timeout.Value,
            activeDcsPerSite: selection.ActiveDcsPerSite,
            inventoryStatus: await _main.DomainControllers.GetStatusAsync(CancellationToken.None));

        await _main.Progress.RunAsync(request, credential);
    }

}
