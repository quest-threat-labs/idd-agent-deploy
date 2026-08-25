using System.Drawing;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>How one outcome should be shown in a grid cell.</summary>
/// <param name="Text">What the operator reads. Never an enum name where plain English is clearer.</param>
public sealed record OutcomeAppearance(string Text, Color BackColor, Color ForeColor);

/// <summary>
/// Colour-codes the outcome column (PRD 8.1).
/// </summary>
/// <remarks>
/// <para>
/// Four states are called for: success, reboot-pending, failure, and never deployed. They are
/// distinguished by text as well as by colour — an operator with colour-vision deficiency, or
/// one reading a screenshot pasted into a ticket, must still be able to tell a failure from a
/// success. Colour is the fast path, not the only path.
/// </para>
/// <para>
/// Reboot-pending is amber rather than green even though PRD 10.1 counts it as a success. The
/// install worked, but a domain controller reporting a pending reboot is something an operator
/// should look at, and a green cell invites them not to.
/// </para>
/// </remarks>
public static class OutcomeStyle
{
    private static readonly Color SuccessBack = Color.FromArgb(223, 246, 221);
    private static readonly Color SuccessFore = Color.FromArgb(11, 79, 25);

    private static readonly Color WarningBack = Color.FromArgb(255, 244, 206);
    private static readonly Color WarningFore = Color.FromArgb(122, 74, 0);

    private static readonly Color FailureBack = Color.FromArgb(253, 231, 233);
    private static readonly Color FailureFore = Color.FromArgb(140, 20, 30);

    private static readonly Color NeutralBack = Color.FromArgb(245, 245, 245);
    private static readonly Color NeutralFore = Color.FromArgb(90, 90, 90);

    public static OutcomeAppearance For(DeploymentOutcome? outcome) => outcome switch
    {
        null => new OutcomeAppearance("never deployed", NeutralBack, NeutralFore),

        DeploymentOutcome.Success =>
            new OutcomeAppearance("success", SuccessBack, SuccessFore),

        DeploymentOutcome.SuccessRebootRequired =>
            new OutcomeAppearance("success - reboot pending", WarningBack, WarningFore),

        DeploymentOutcome.Failure =>
            new OutcomeAppearance("failed", FailureBack, FailureFore),

        DeploymentOutcome.Timeout =>
            new OutcomeAppearance("timed out", FailureBack, FailureFore),

        DeploymentOutcome.Cancelled =>
            new OutcomeAppearance("cancelled", WarningBack, WarningFore),

        DeploymentOutcome.Skipped =>
            new OutcomeAppearance("not attempted", NeutralBack, NeutralFore),

        _ => new OutcomeAppearance(outcome.Value.ToString(), NeutralBack, NeutralFore),
    };

    /// <summary>
    /// Appearance for one target's validation verdict (PRD 11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three states, not two. A domain controller that is reachable and writable but already
    /// carries a newer agent than the selected package is amber: nothing is wrong with the
    /// host, but deploying to it would return 1638, and a green row invites the operator to
    /// press Start and discover that the hard way.
    /// </para>
    /// <para>
    /// Distinguished by text as well as colour, for the same reason the deployment outcomes
    /// are — a screenshot pasted into a ticket has to remain readable.
    /// </para>
    /// </remarks>
    public static OutcomeAppearance ForValidation(ValidationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (!outcome.Passed)
        {
            return new OutcomeAppearance("not ready", FailureBack, FailureFore);
        }

        // Outranks the version comparison: an upgrade that crosses products fails regardless
        // of which version is newer, so "ready - upgrade" would be actively misleading.
        if (outcome.HasModeMismatch)
        {
            return new OutcomeAppearance("wrong mode", WarningBack, WarningFore);
        }

        return outcome.Predicted switch
        {
            PredictedAction.DowngradeBlocked =>
                new OutcomeAppearance("package refused", WarningBack, WarningFore),

            PredictedAction.Unknown =>
                new OutcomeAppearance("ready - version unclear", WarningBack, WarningFore),

            PredictedAction.Upgrade => new OutcomeAppearance("ready - upgrade", SuccessBack, SuccessFore),
            PredictedAction.Reinstall => new OutcomeAppearance("ready - reinstall", SuccessBack, SuccessFore),
            _ => new OutcomeAppearance("ready - install", SuccessBack, SuccessFore),
        };
    }

    /// <summary>
    /// Appearance for a target that is mid-deployment, for the progress grid (PRD 8.4).
    /// </summary>
    public static OutcomeAppearance ForStage(DeploymentStage? stage) => stage switch
    {
        null => new OutcomeAppearance("waiting", NeutralBack, NeutralFore),
        DeploymentStage.Preflight => new OutcomeAppearance("pre-flight", NeutralBack, NeutralFore),
        DeploymentStage.Stage => new OutcomeAppearance("staging", NeutralBack, NeutralFore),
        DeploymentStage.Execute => new OutcomeAppearance("installing", NeutralBack, NeutralFore),
        DeploymentStage.Retrieve => new OutcomeAppearance("retrieving log", NeutralBack, NeutralFore),
        DeploymentStage.Cleanup => new OutcomeAppearance("cleaning up", NeutralBack, NeutralFore),
        _ => new OutcomeAppearance(stage.Value.ToString(), NeutralBack, NeutralFore),
    };
}
