using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Msi;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>One item of the PRD 11 pre-flight checklist.</summary>
/// <param name="Detail">
/// On failure, what is wrong and what to do about it (PRD 10.4). On success, a short
/// confirmation the operator can read back — a checklist that only says "OK" invites the
/// operator to stop reading it.
/// </param>
public sealed record PreflightCheck(string Name, bool Passed, string Detail);

/// <summary>
/// The outcome of pre-flight validation.
/// </summary>
/// <param name="Msi">
/// The inspected package, when extraction succeeded. Returned so the caller does not open the
/// MSI a second time, and so it cannot deploy something different from what was validated.
/// </param>
/// <param name="Warnings">
/// Non-blocking observations: an unexpected product name (PRD 5.5), an Org ID containing
/// characters worth a second look (PRD 8.3), or a stale inventory.
/// </param>
public sealed record PreflightReport(
    IReadOnlyList<PreflightCheck> Checks,
    MsiPackageInfo? Msi,
    string? OrgId,
    IReadOnlyList<string> Warnings)
{
    public bool Passed => Checks.All(c => c.Passed);

    public IReadOnlyList<PreflightCheck> Failures => [.. Checks.Where(c => !c.Passed)];
}

/// <summary>
/// The blocking checklist run before any deployment starts (PRD 11).
/// </summary>
/// <remarks>
/// <para>
/// Every check runs even after one fails, so an operator correcting a scripted invocation
/// sees everything wrong with it at once rather than discovering the problems one run at a
/// time.
/// </para>
/// <para>
/// Per-target reachability — TCP 445, the WinRM port, C$ — is deliberately absent. PRD 11 is
/// explicit that it belongs in each target's own pre-flight stage: probing sixty DCs serially
/// before starting would be slow, and the answer can change before the target is reached.
/// What is checked here is only what must be true for the run to make sense at all.
/// </para>
/// </remarks>
public sealed class PreflightValidator
{
    private readonly IMsiInspector _inspector;
    private readonly IHostResolver _resolver;

    public PreflightValidator(IMsiInspector inspector, IHostResolver resolver)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <param name="cloudMode">
    /// Whether the run will pass <c>SG=1</c>. Only affects the Org ID warnings — a GUID is
    /// expected for Identity Defense, a short installation name for Change Auditor.
    /// </param>
    public async Task<PreflightReport> ValidateAsync(
        string msiPath,
        string? orgId,
        IReadOnlyList<DeploymentTarget> targets,
        string logDirectory,
        CancellationToken ct,
        bool cloudMode = true)
    {
        var checks = new List<PreflightCheck>();
        var warnings = new List<string>();

        // 1. The MSI exists, is readable, and its properties extract.
        MsiPackageInfo? msi = null;
        try
        {
            msi = await _inspector.InspectAsync(msiPath, ct).ConfigureAwait(false);
            checks.Add(new PreflightCheck(
                "Installer package",
                true,
                $"{msi.FileName} — {msi.ProductName ?? "(no product name)"} {msi.ProductVersion}, " +
                $"SHA-256 {msi.Sha256[..16]}…"));

            if (!msi.LooksLikeExpectedProduct)
            {
                // PRD 5.5: warn prominently, never hard-block on a name that may change.
                warnings.Add(
                    $"'{msi.ProductName ?? "(no product name)"}' does not look like a Quest Change " +
                    "Auditor agent package. Confirm you selected the right MSI before continuing.");
            }
        }
        catch (MsiInspectionException ex)
        {
            checks.Add(new PreflightCheck("Installer package", false, ex.Message));
        }

        // 2. The Org ID is present.
        var orgIdValidation = OrgId.Validate(orgId, cloudMode);
        checks.Add(new PreflightCheck(
            "Org ID",
            orgIdValidation.IsValid,
            orgIdValidation.IsValid
                ? $"'{orgIdValidation.Value}' will be passed as INSTALLATION_NAME"
                : orgIdValidation.Error!));
        warnings.AddRange(orgIdValidation.Warnings);

        // 3. At least one target.
        checks.Add(new PreflightCheck(
            "Targets",
            targets.Count > 0,
            targets.Count > 0
                ? $"{targets.Count} domain controller(s) selected"
                : "No domain controllers were selected. Choose targets by tag, by host, or with " +
                  "--all, and confirm the inventory has been enumerated from Active Directory."));

        // 4. The log directory is writable. Checked by writing, not by inspecting permissions:
        //    an ACL that looks right and a volume that is full look identical until you try.
        checks.Add(CheckLogDirectory(logDirectory));

        // 5. Every target resolves in DNS. A name that does not resolve cannot be deployed to,
        //    and finding that out now beats finding it out per target during the run.
        if (targets.Count > 0)
        {
            var unresolved = new List<string>();
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();

                var resolution = await _resolver.ResolveAsync(target.Fqdn, ct).ConfigureAwait(false);
                if (!resolution.Succeeded)
                {
                    unresolved.Add(target.Fqdn);
                }
            }

            checks.Add(new PreflightCheck(
                "DNS resolution",
                unresolved.Count == 0,
                unresolved.Count == 0
                    ? $"all {targets.Count} target(s) resolve"
                    : $"{unresolved.Count} target(s) did not resolve: {string.Join(", ", unresolved)}. " +
                      "Confirm the names are correct and that this workstation uses a DNS server " +
                      "authoritative for the domain, then re-enumerate from Active Directory if a " +
                      "domain controller has been decommissioned."));
        }

        return new PreflightReport(checks, msi, orgIdValidation.Value, warnings);
    }

    /// <summary>
    /// Confirms the run log directory can be written, without creating it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writability is proved by writing, not by inspecting permissions: an ACL that looks
    /// correct and a volume that is full look identical until you try.
    /// </para>
    /// <para>
    /// The run directory itself is deliberately not created here. Each invocation generates a
    /// fresh timestamped directory name, so creating it during validation would leave an empty
    /// orphan behind on every refused or failed invocation — and a `deploy` refused for want of
    /// `--confirm` is a normal, expected event. The probe therefore runs in the nearest
    /// ancestor that already exists, and the run directory is created by
    /// <c>RunLogWriter</c> only once a run actually starts.
    /// </para>
    /// </remarks>
    private static PreflightCheck CheckLogDirectory(string logDirectory)
    {
        try
        {
            var probeLocation = NearestExistingAncestor(Path.GetFullPath(logDirectory));

            if (probeLocation is null)
            {
                return new PreflightCheck(
                    "Run log directory",
                    false,
                    $"No part of the path '{logDirectory}' exists, so it cannot be created. " +
                    "Check the drive letter or share name, then choose a location with --log-dir.");
            }

            var probe = Path.Combine(probeLocation, $".had-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return new PreflightCheck("Run log directory", true, $"{logDirectory} is writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PreflightCheck(
                "Run log directory",
                false,
                $"'{logDirectory}' is not writable: {ex.Message} The run log is the only durable " +
                "record of what happened, so a deployment does not start without somewhere to " +
                "write it. Choose another location with --log-dir.");
        }
    }

    /// <summary>
    /// Walks up from a path to the first directory that exists, or null if none does.
    /// </summary>
    /// <remarks>
    /// A path that is not a directory at all — a file, or a path beneath one — has no existing
    /// ancestor that could contain it, and is reported as unusable rather than silently probing
    /// somewhere further up that happens to be writable.
    /// </remarks>
    private static string? NearestExistingAncestor(string path)
    {
        for (var candidate = path; !string.IsNullOrEmpty(candidate);
             candidate = Path.GetDirectoryName(candidate))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(candidate))
            {
                // A file sits where a directory would have to be, so the run directory can
                // never be created here.
                return null;
            }
        }

        return null;
    }
}
