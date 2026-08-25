using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>
/// Whether the deployment screen may start a run, and why not when it may not.
/// </summary>
/// <remarks>
/// <para>
/// The rules live here rather than scattered across event handlers so that there is one answer
/// to "can this deploy?" — and so the answer can be tested. An enablement bug that let a run
/// start with no MSI, no Org ID, or no targets would be caught by Core's own validation, but a
/// bug in the other direction, letting a <em>second</em> run start while one is in flight,
/// would silently double the concurrency ceiling against domain controllers (R7.1).
/// </para>
/// <para>
/// This is a presentation-level gate, not the safety mechanism. PRD 11 pre-flight still runs,
/// and the confirmation dialog still stands between the operator and a multi-target push.
/// </para>
/// </remarks>
public sealed record DeploymentFormState
{
    /// <summary>Null until an MSI has been selected and read successfully (PRD 8.3).</summary>
    public MsiPackageInfo? Msi { get; init; }

    /// <summary>Whatever is currently typed in the Org ID box.</summary>
    public string? OrgId { get; init; }

    /// <summary>
    /// The cloud-mode checkbox. Decides which product the agent is pointed at, and therefore
    /// what an Org ID is expected to look like.
    /// </summary>
    public bool CloudMode { get; init; } = true;

    public int SelectedTargetCount { get; init; }

    /// <summary>
    /// What is already running against domain controllers — "deployment" or "validation" — or
    /// null when nothing is. Blocks starting anything else.
    /// </summary>
    public string? ActivityInFlight { get; init; }

    /// <summary>Set when an alternate account is chosen but no password has been entered.</summary>
    public bool AlternateCredentialIncomplete { get; init; }

    // Fully qualified: the OrgId property above shadows the OrgId type in this scope.
    public OrgIdValidation OrgIdValidation => Core.Deployment.OrgId.Validate(OrgId, CloudMode);

    public bool CanStart => BlockingReason is null;

    /// <summary>
    /// Why the Start button is disabled, phrased for the operator, or null when it is enabled.
    /// </summary>
    /// <remarks>
    /// Shown next to the button rather than left implicit. A greyed-out control with no
    /// explanation is the most common way an operator tool wastes someone's afternoon.
    /// </remarks>
    public string? BlockingReason
    {
        get
        {
            if (SharedBlockingReason is { } shared)
            {
                return shared;
            }

            if (Msi is null)
            {
                // PRD 8.3: if extraction fails, block deployment.
                return "Select an installer package. Its version and identity must be readable " +
                       "before it can be deployed.";
            }

            var orgId = OrgIdValidation;
            if (!orgId.IsValid)
            {
                return orgId.Error;
            }

            if (SelectedTargetCount == 0)
            {
                return "Select at least one domain controller on the Inventory tab.";
            }

            if (AlternateCredentialIncomplete)
            {
                return "Enter the password for the alternate account, or switch back to the " +
                       "current Windows identity.";
            }

            return null;
        }
    }

    public bool CanValidate => ValidationBlockingReason is null;

    /// <summary>
    /// Why the Validate button is disabled, or null when it is enabled.
    /// </summary>
    /// <remarks>
    /// Deliberately a shorter list than <see cref="BlockingReason"/>: validation does not
    /// require an Org ID, because no installer runs and there is nothing for an Org ID to
    /// configure. Requiring one would make an operator invent a value to find out whether
    /// their domain controllers are reachable — and an invented value is exactly what you do
    /// not want sitting in the box when they later press Start.
    /// </remarks>
    public string? ValidationBlockingReason
    {
        get
        {
            if (SharedBlockingReason is { } shared)
            {
                return shared;
            }

            if (Msi is null)
            {
                // Required because validation reports what deploying this package would do to
                // each target. Without one there is nothing to compare the installed agent
                // against, and the most useful half of the answer disappears.
                return "Select an installer package. Validation compares its version against " +
                       "the agent already installed on each domain controller.";
            }

            if (SelectedTargetCount == 0)
            {
                return "Select at least one domain controller on the Inventory tab.";
            }

            if (AlternateCredentialIncomplete)
            {
                return "Enter the password for the alternate account, or switch back to the " +
                       "current Windows identity.";
            }

            return null;
        }
    }

    /// <summary>
    /// The reasons that stop both buttons.
    /// </summary>
    /// <remarks>
    /// Only one run of either kind at a time. Two orchestrators would each permit five
    /// concurrent targets, so validating while deploying would silently turn the R7.1 ceiling
    /// of five into ten against domain controllers.
    /// </remarks>
    /// <summary>The product this run points the agent at, named as the operator knows it.</summary>
    public string ModeDescription => ValidationOutcome.Describe(
        CloudMode ? AgentMode.IdentityDefense : AgentMode.ChangeAuditor);

    private string? SharedBlockingReason =>
        ActivityInFlight is { } activity
            ? $"A {activity} is already running. Wait for it to finish before starting another."
            : null;

    /// <summary>
    /// The confirmation text shown before a run starts (PRD 8.3).
    /// </summary>
    /// <remarks>
    /// Restates the target count and the Org ID, because those are the two things a mistyped
    /// invocation gets wrong and the two that matter most once msiexec is running on a domain
    /// controller.
    /// </remarks>
    public string ConfirmationPrompt(IReadOnlyList<string> targetFqdns, int hiddenSelectedCount)
    {
        var lines = new List<string>
        {
            $"Deploy {Msi?.ProductName ?? "this package"} {Msi?.ProductVersion} to " +
            $"{SelectedTargetCount} domain controller{(SelectedTargetCount == 1 ? string.Empty : "s")}?",
            string.Empty,

            // Which product the agent will report to afterwards. Restated because it is set by
            // a checkbox that is easy to leave at its last value, and because the installer
            // refuses to move an existing agent from one product to the other.
            $"Reporting to: {ModeDescription}",
            $"Org ID: {OrgIdValidation.Value}",
            string.Empty,
        };

        lines.AddRange(targetFqdns.Take(12).Select(f => $"  {f}"));

        if (targetFqdns.Count > 12)
        {
            lines.Add($"  ... and {targetFqdns.Count - 12} more");
        }

        if (hiddenSelectedCount > 0)
        {
            // The operator cannot see these on the grid right now, so the dialog says so
            // rather than letting the count quietly disagree with what is on screen.
            lines.Add(string.Empty);
            lines.Add(
                $"{hiddenSelectedCount} of these are hidden by the current filter.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The confirmation text shown before a validation run.
    /// </summary>
    /// <remarks>
    /// Validation installs nothing, so this is not the safety gate the deployment prompt is.
    /// It is here because the operator is about to have a tool open sessions to Tier 0 hosts
    /// and write files to them, and they are entitled to read what that involves in one screen
    /// before it happens rather than reconstruct it from a log afterwards.
    /// </remarks>
    public string ValidationPrompt(IReadOnlyList<string> targetFqdns, int hiddenSelectedCount)
    {
        var lines = new List<string>
        {
            $"Check {SelectedTargetCount} domain controller" +
            $"{(SelectedTargetCount == 1 ? string.Empty : "s")} against " +
            $"{Msi?.ProductName ?? "this package"} {Msi?.ProductVersion}?",
            string.Empty,
            $"Checked as a deployment reporting to: {ModeDescription}",
            string.Empty,
            "On each one this will:",
            "  - confirm DNS, SMB, and WinRM answer",
            "  - write a small test file to C:\\Windows\\Temp and read it back",
            "  - open a remoting session and run a command that does nothing",
            "  - read which agent is installed, and which product it reports to",
            "  - read the Windows version, which the agent requires to be 2016 or later",
            "  - delete the test file",
            string.Empty,
            "Nothing is installed, changed, or restarted. The package is not copied to any " +
            "domain controller and msiexec is not run.",
            string.Empty,
        };

        lines.AddRange(targetFqdns.Take(12).Select(f => $"  {f}"));

        if (targetFqdns.Count > 12)
        {
            lines.Add($"  ... and {targetFqdns.Count - 12} more");
        }

        if (hiddenSelectedCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{hiddenSelectedCount} of these are hidden by the current filter.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The exact msiexec command line the run will use (PRD 8.3).
    /// </summary>
    /// <remarks>
    /// Built through <see cref="MsiCommandBuilder"/> rather than assembled for display, so the
    /// preview cannot drift from what actually executes. Operators deploying to Tier 0 want to
    /// see the command before it runs, and a preview that is merely similar would be worse
    /// than none.
    /// </remarks>
    public string CommandPreview()
    {
        if (Msi is null)
        {
            return "(select an installer package to see the command)";
        }

        var orgId = OrgIdValidation;
        if (!orgId.IsValid)
        {
            return "(enter a valid Org ID to see the command)";
        }

        // A representative run GUID: the real one is generated when the run starts.
        var directory = Path.Combine(
            @"C:\Windows\Temp\HybridAgentDeploy", "{run-guid}");

        return MsiCommandBuilder.BuildInstallCommand(
            Path.Combine(directory, Msi.FileName),
            Path.Combine(directory, "install.log"),
            orgId.Value!,
            CloudMode).ToDisplayString();
    }

    /// <summary>
    /// Selected targets broken down by site, for the target summary (PRD 8.3).
    /// </summary>
    /// <remarks>
    /// The site breakdown is not decoration: R7.2 limits concurrency within a site to half its
    /// domain controllers, so a selection concentrated in one small site will run far more
    /// slowly than the max-parallel setting suggests, and the operator should be able to see
    /// why before they start rather than wonder during.
    /// </remarks>
    public static IReadOnlyList<string> SummariseBySite(IEnumerable<InventoryRow> selected)
    {
        return
        [
            .. selected
                .GroupBy(r => string.IsNullOrWhiteSpace(r.SiteName) ? "(no site recorded)" : r.SiteName!,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}: {g.Count()}")
        ];
    }
}
