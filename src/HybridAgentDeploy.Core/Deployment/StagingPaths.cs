namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Where this tool writes on a target, and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// SEC7: under <c>C:\Windows\Temp</c>, which inherits admin-only ACLs. Never a world-readable
/// location, and the ACLs on the staging path are never loosened.
/// </para>
/// <para>
/// Shared by the deployment orchestrator and by validation so there is one answer to "what did
/// this tool leave on my domain controller". A directory found under this root must be
/// traceable to the run that created it without ambiguity (NFR7), which is why both names
/// carry the full run GUID rather than a short form.
/// </para>
/// </remarks>
public static class StagingPaths
{
    public const string Root = @"C:\Windows\Temp\HybridAgentDeploy";

    /// <summary>The per-run staging directory for a deployment, as PRD 5.4 specifies.</summary>
    public static string ForRun(Guid runGuid) => Path.Combine(Root, runGuid.ToString("D"));

    /// <summary>
    /// The per-run probe directory for a validation.
    /// </summary>
    /// <remarks>
    /// Prefixed so that an operator who finds a leftover directory can tell at a glance
    /// whether an installation was attempted in it. Validation never stages an MSI and never
    /// runs msiexec; a directory named this way held a few kilobytes of random bytes.
    /// </remarks>
    public static string ForValidation(Guid runGuid) => Path.Combine(Root, $"validate-{runGuid:D}");
}
