namespace HybridAgentDeploy.Core.Deployment;

/// <param name="IsValid">False only when the Org ID is unusable and deployment must not start.</param>
/// <param name="Value">The trimmed value to use. Null when invalid.</param>
/// <param name="Error">Why it was rejected. Null when valid.</param>
/// <param name="Warnings">
/// Non-blocking observations shown to the operator. PRD 8.3 asks for a warning on whitespace
/// or characters that would need quoting.
/// </param>
public sealed record OrgIdValidation(
    bool IsValid,
    string? Value,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates the operator-supplied Org ID (<c>INSTALLATION_NAME</c>).
/// </summary>
/// <remarks>
/// <para>
/// PRD Q2, answered: the expected format depends on which product the agent is being pointed
/// at. In Identity Defense (cloud) mode — <c>SG=1</c> — it is the tenant GUID. In Change
/// Auditor (on-premises) mode — where <c>SG</c> is absent — it is a short installation name
/// such as <c>DEFAULT</c>.
/// </para>
/// <para>
/// A value that disagrees with the selected mode is <em>warned about, not rejected</em>. The
/// two are independent controls and either could be the one that is wrong, so the operator is
/// told they disagree and left to decide which to change. Rejecting would also mean this
/// utility deciding the format of an identifier issued by another product, which is not a
/// judgement it is in a position to make correctly for every customer.
/// </para>
/// <para>
/// Shell metacharacters are likewise not rejected. The injection concern in SEC10 is answered
/// structurally by <see cref="RemoteCommand"/>: the value occupies one argument slot and
/// nothing downstream re-parses it, so a metacharacter is inert rather than merely escaped.
/// The warnings exist so the operator still notices a value that looks like a mistake. The
/// one exception is the double quote — see below.
/// </para>
/// </remarks>
public static class OrgId
{
    /// <summary>
    /// Characters that would need quoting in a shell, and that no plausible identifier
    /// format contains. Their presence is worth flagging to the operator even though it is
    /// harmless here.
    /// </summary>
    /// <remarks>
    /// The double quote is absent because it is rejected outright above rather than warned
    /// about. These remaining characters are inert — nothing downstream is a shell — but are
    /// still worth flagging, since an identifier containing one is more likely a paste error
    /// than a real Org ID.
    /// </remarks>
    private static readonly char[] ShellSignificantCharacters =
        ['\'', ';', '&', '|', '`', '$', '<', '>', '^', '%', '(', ')', '{', '}'];

    /// <param name="cloudMode">
    /// True when the run will pass <c>SG=1</c> and the agent will report to Identity Defense.
    /// False for Change Auditor, where <c>SG</c> is omitted entirely. Only affects the
    /// warnings: the same values are accepted either way.
    /// </param>
    public static OrgIdValidation Validate(string? rawValue, bool cloudMode = true)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new OrgIdValidation(
                IsValid: false,
                Value: null,
                Error: cloudMode
                    ? "An Org ID is required. In cloud mode it is the GUID identifying the " +
                      "Identity Defense tenant the agent reports to, and is passed to the " +
                      "installer as INSTALLATION_NAME."
                    : "An Org ID is required. With cloud mode off it is the Change Auditor " +
                      "installation name — a short value such as DEFAULT — and is passed to " +
                      "the installer as INSTALLATION_NAME.",
                Warnings: []);
        }

        var trimmed = rawValue.Trim();

        // The one character that is rejected rather than warned about (SEC10, which permits
        // "reject or escape quote characters"). Every other metacharacter is inert: the
        // command is launched with UseShellExecute = false, so no shell reads it. A double
        // quote is different — it can close the INSTALLATION_NAME property value and begin a
        // second property assignment inside msiexec's own parser, altering what is installed
        // on a domain controller. No plausible tenant identifier contains one.
        if (trimmed.Contains('"', StringComparison.Ordinal))
        {
            return new OrgIdValidation(
                IsValid: false,
                Value: null,
                Error: "The Org ID contains a double-quote character, which cannot be passed " +
                       "safely to the installer: it would end the INSTALLATION_NAME value and " +
                       "be read as the start of another installer property. Remove the quote. " +
                       "If a double-quote is genuinely part of your Org ID, raise it before " +
                       "deploying — it is not something this utility can pass through safely.",
                Warnings: []);
        }

        var warnings = new List<string>();

        // The two controls disagreeing is the mistake most likely to reach a domain controller
        // unnoticed: both a GUID and a short name are perfectly valid Org IDs, so nothing else
        // in the tool can tell that the wrong one was pasted. Warned in both directions,
        // because either control could be the one that is wrong.
        var looksLikeGuid = Guid.TryParse(trimmed, out _);

        if (cloudMode && !looksLikeGuid)
        {
            warnings.Add(
                $"Cloud mode is on, which points the agent at Identity Defense, but '{trimmed}' " +
                "is not a GUID. An Identity Defense Org ID is the tenant GUID. If you meant to " +
                "deploy against on-premises Change Auditor, turn cloud mode off.");
        }
        else if (!cloudMode && looksLikeGuid)
        {
            warnings.Add(
                "Cloud mode is off, which points the agent at on-premises Change Auditor, but " +
                $"'{trimmed}' is a GUID. Change Auditor uses a short installation name such as " +
                "DEFAULT. If you meant to deploy against Identity Defense, turn cloud mode on.");
        }

        if (!string.Equals(rawValue, trimmed, StringComparison.Ordinal))
        {
            warnings.Add(
                "The Org ID had leading or trailing whitespace, which has been removed. " +
                $"The value used will be '{trimmed}'.");
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            warnings.Add(
                "The Org ID contains a space. Confirm this is correct before deploying — a " +
                "pasted value that picked up a line break or a stray space is a common mistake " +
                "and the agent would register under the wrong tenant name.");
        }

        var flagged = trimmed.Where(c => ShellSignificantCharacters.Contains(c)).Distinct().ToArray();
        if (flagged.Length > 0)
        {
            warnings.Add(
                $"The Org ID contains {string.Join(" ", flagged.Select(c => $"'{c}'"))}, which " +
                "would need quoting in a command line. It is passed to the installer as a single " +
                "argument and cannot affect the command, but confirm the value is what you intended.");
        }

        if (trimmed.Any(char.IsControl))
        {
            warnings.Add(
                "The Org ID contains a control character, which is almost certainly a paste " +
                "artefact. Confirm the value before deploying.");
        }

        return new OrgIdValidation(IsValid: true, Value: trimmed, Error: null, Warnings: warnings);
    }
}
