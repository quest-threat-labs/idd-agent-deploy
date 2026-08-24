namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Builds the msiexec command run on each target (PRD 5.4 step 3).
/// </summary>
/// <remarks>
/// The paths handed in are always local to the target. Running
/// <c>msiexec /i \\server\share\agent.msi</c> inside a remoting session fails in most
/// environments because the session cannot delegate the operator's Kerberos ticket to a
/// third host — the double-hop problem. Staging to the DC's local disk and executing entirely
/// locally sidesteps delegation, needs no CredSSP, and requires no configuration change on a
/// Tier 0 host. PRD 5.4 calls this the single most important design decision in the tool.
/// Do not "optimise" it into a UNC execution.
/// </remarks>
public static class MsiCommandBuilder
{
    public const string Executable = "msiexec";

    /// <summary>
    /// Builds the install command.
    /// </summary>
    /// <param name="stagedMsiPath">
    /// The MSI's path <em>on the target</em>, as returned by staging. Never a UNC path.
    /// </param>
    /// <param name="logPath">The verbose log path on the target, retrieved afterwards.</param>
    /// <param name="orgId">
    /// Already validated and trimmed by <see cref="OrgId.Validate"/>. Occupies exactly one
    /// argument slot, so its content cannot affect the structure of the command (SEC10).
    /// </param>
    /// <param name="cloudMode">
    /// Emits <c>SG=1</c> when set. PRD-OPEN-Q: Q1 — exposed as a checkbox defaulting to on.
    /// If PM confirms cloud mode is the only mode this utility targets, simplify to a
    /// hardcoded SG=1 and remove the control.
    /// </param>
    public static RemoteCommand BuildInstallCommand(
        string stagedMsiPath,
        string logPath,
        string orgId,
        bool cloudMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedMsiPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);

        if (IsUncPath(stagedMsiPath))
        {
            // A defect that produced a UNC path here would silently reintroduce the
            // double-hop failure this design exists to avoid, and would do so in a way that
            // only reproduces in a customer environment. Fail loudly instead.
            throw new ArgumentException(
                $"The staged MSI path '{stagedMsiPath}' is a UNC path. msiexec must be run " +
                "against a path local to the target; see PRD 5.4 on the Kerberos double-hop " +
                "problem.",
                nameof(stagedMsiPath));
        }

        var arguments = new List<string>(8) { "/i", stagedMsiPath };

        if (cloudMode)
        {
            arguments.Add("SG=1");
        }

        arguments.Add($"INSTALLATION_NAME={orgId}");
        arguments.Add("INSTALLATION_NAME_VALID=1");
        arguments.Add("/qn");
        arguments.Add("/l*v");
        arguments.Add(logPath);

        return new RemoteCommand(Executable, arguments);
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);
}
