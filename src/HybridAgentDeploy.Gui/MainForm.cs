using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Inventory;
using HybridAgentDeploy.Core.Msi;
using HybridAgentDeploy.Gui.Presentation;
using HybridAgentDeploy.Gui.Views;
using Microsoft.Extensions.Logging;

namespace HybridAgentDeploy.Gui;

/// <summary>
/// The application window: five tabs matching PRD 8.1 to 8.5.
/// </summary>
/// <remarks>
/// <para>
/// Holds the state the tabs share — the loaded inventory, the operator's selection, and
/// whether a deployment is in flight — because a selection made on the Inventory tab has to be
/// visible to the Deployment tab, and both have to know when a run is running.
/// </para>
/// <para>
/// The single-run guard lives here rather than in the deployment tab. Two orchestrators would
/// each permit five concurrent targets, so a second run would silently turn the R7.1 ceiling
/// of five into ten against domain controllers.
/// </para>
/// </remarks>
internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly InventoryTab _inventoryTab;
    private readonly TagsTab _tagsTab;
    private readonly DeployTab _deployTab;
    private readonly ProgressTab _progressTab;
    private readonly HistoryTab _historyTab;

    private readonly TabPage _progressPage;

    public MainForm(
        AppConfiguration configuration,
        SqliteConnectionFactory connections,
        ILoggerFactory loggerFactory)
    {
        Configuration = configuration;
        LoggerFactory = loggerFactory;

        DomainControllers = new DomainControllerRepository(connections);
        Tags = new TagRepository(connections);
        Deployments = new DeploymentRepository(connections);
        TargetSelector = new TargetSelector(DomainControllers, Tags);
        Resolver = new DnsHostResolver();
        MsiInspector = new MsiInspector();

        AutoScaleMode = AutoScaleMode.None;

        Text = "Hybrid Audit Agent Deployment Utility";
        StartPosition = FormStartPosition.CenterScreen;

        _inventoryTab = new InventoryTab(this);
        _tagsTab = new TagsTab(this);
        _deployTab = new DeployTab(this);
        _progressTab = new ProgressTab(this);
        _historyTab = new HistoryTab(this);

        _tabs.TabPages.Add(NewPage("Inventory", _inventoryTab));
        _tabs.TabPages.Add(NewPage("Tags", _tagsTab));
        _tabs.TabPages.Add(NewPage("Deploy", _deployTab));

        _progressPage = NewPage("Progress", _progressTab);
        _tabs.TabPages.Add(_progressPage);

        _tabs.TabPages.Add(NewPage("History", _historyTab));

        Controls.Add(_tabs);

        Load += async (_, _) => await RefreshInventoryAsync();
    }

    public AppConfiguration Configuration { get; }

    public ILoggerFactory LoggerFactory { get; }

    public DomainControllerRepository DomainControllers { get; }

    public TagRepository Tags { get; }

    public DeploymentRepository Deployments { get; }

    public TargetSelector TargetSelector { get; }

    public IHostResolver Resolver { get; }

    public IMsiInspector MsiInspector { get; }

    /// <summary>Every domain controller currently in the inventory, shared by the tabs.</summary>
    public IReadOnlyList<InventoryRow> Inventory { get; private set; } = [];

    /// <summary>
    /// Every tag that exists, including tags applied to no domain controllers.
    /// </summary>
    /// <remarks>
    /// Loaded from the tag table rather than derived from the tags present on inventory rows.
    /// A tag applied to nothing is still a tag, and deriving the list from the inventory made
    /// a newly created one invisible — so creating a tag looked like it had failed.
    /// </remarks>
    public IReadOnlyList<Core.Models.TagRecord> AllTags { get; private set; } = [];

    /// <summary>What the operator has ticked, tracked by id so filtering cannot drop it.</summary>
    public SelectionState Selection { get; } = new();

    /// <summary>True while a deployment is running. Gates the Start button (R7.1).</summary>
    public bool IsDeploying { get; private set; }

    public ILogger<T> Logger<T>() => LoggerFactory.CreateLogger<T>();

    /// <summary>Reloads the inventory from the database and refreshes every tab that shows it.</summary>
    public async Task RefreshInventoryAsync()
    {
        var records = await DomainControllers.GetAllAsync(includeInactive: false, CancellationToken.None);
        var tags = await Tags.GetTagsByDcAsync(CancellationToken.None);
        var lastDeployments = await DomainControllers.GetLastDeploymentsAsync(CancellationToken.None);

        AllTags = await Tags.GetAllAsync(CancellationToken.None);

        Inventory =
        [
            .. records.Select(record => InventoryRow.From(
                record,
                tags.GetValueOrDefault(record.Id, []),
                lastDeployments.GetValueOrDefault(record.Id)))
        ];

        // A DC deactivated by a re-enumeration must not linger in the selection, where it
        // would be deployed to without appearing on the grid.
        Selection.Prune(Inventory);

        _inventoryTab.BindInventory();
        _tagsTab.BindTags();
        _deployTab.RefreshSelectionSummary();
    }

    /// <summary>Called by the inventory grid whenever the operator's ticks change.</summary>
    public void OnSelectionChanged() => _deployTab.RefreshSelectionSummary();

    /// <summary>
    /// Marks a run as started or finished, and moves the operator to the Progress tab.
    /// </summary>
    /// <remarks>
    /// The flag is set here and read by <see cref="DeployTab"/>, so there is exactly one place
    /// that decides whether a second run may begin.
    /// </remarks>
    public void SetDeploying(bool deploying)
    {
        IsDeploying = deploying;
        _deployTab.RefreshSelectionSummary();

        if (deploying)
        {
            _tabs.SelectedTab = _progressPage;
        }
    }

    public ProgressTab Progress => _progressTab;

    public HistoryTab History => _historyTab;

    /// <summary>Switches to the Tags tab, for the Inventory tab's "Manage tags" button.</summary>
    public void ShowTagsTab() => _tabs.SelectedIndex = 1;

    /// <summary>
    /// Sizes the window once the form exists, from the space actually available.
    /// </summary>
    /// <remarks>
    /// Sized from the working area rather than to fixed pixels, because these tools get run
    /// dimensions set there, which turned a requested 1280x820 into an 853x547 window on a
    /// 100% DPI display and cropped the inventory grid's right-hand columns. Values assigned
    /// after load are not rescaled, and deriving them from the working area means the window
    /// is sensible on a laptop and on the very wide displays these tools tend to be run on.
    /// </remarks>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var work = Screen.FromControl(this).WorkingArea;

        MinimumSize = new Size(
            Math.Min(1000, work.Width - 40),
            Math.Min(640, work.Height - 40));

        Size = new Size(
            Math.Clamp((int)(work.Width * 0.55), MinimumSize.Width, 1500),
            Math.Clamp((int)(work.Height * 0.80), MinimumSize.Height, 950));

        // CenterScreen is applied before this resize, so re-centre on the chosen size.
        Location = new Point(
            work.Left + ((work.Width - Size.Width) / 2),
            work.Top + ((work.Height - Size.Height) / 2));
    }

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    /// <summary>
    /// Refuses to close while a deployment is in flight.
    /// </summary>
    /// <remarks>
    /// Closing the window would tear down the orchestrator mid-run, abandoning an msiexec on a
    /// domain controller without cleanup — exactly what PRD 8.4 forbids. The operator is told
    /// to cancel instead, which stops new targets and lets in-flight ones finish properly.
    /// </remarks>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsDeploying)
        {
            MessageBox.Show(
                this,
                "A deployment is still running. Cancel it on the Progress tab and wait for the " +
                "targets in flight to finish — closing now would abandon an installation on a " +
                "domain controller without cleaning up after it.",
                "Deployment in progress",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }
}
