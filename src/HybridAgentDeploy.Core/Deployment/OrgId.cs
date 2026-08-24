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
/// PRD-OPEN-Q: Q2 — the valid format of the Org ID is not yet known. Until it is, the
/// documented default applies: require non-empty, trim, and warn on characters that would
/// need quoting. Add strict validation once PM confirms the format.
/// </para>
/// <para>
/// Because Q2 is open, this deliberately does <em>not</em> reject shell metacharacters.
/// Rejecting them would risk refusing a legitimate Org ID whose format nobody has confirmed,
/// with no way for the operator to override. The injection concern in SEC10 is instead
/// answered structurally by <see cref="RemoteCommand"/>: the value occupies one argument slot
/// and nothing downstream re-parses it, so a metacharacter in the Org ID is inert rather than
/// merely escaped. The warnings below exist so the operator still notices a value that looks
/// like a mistake.
/// </para>
/// </remarks>
public static class OrgId
{
    /// <summary>
    /// Characters that would need quoting in a shell, and that no plausible identifier
    /// format contains. Their presence is worth flagging to the operator even though it is
    /// harmless here.
    /// </summary>
    private static readonly char[] ShellSignificantCharacters =
        ['"', '\'', ';', '&', '|', '`', '$', '<', '>', '^', '%', '(', ')', '{', '}'];

    public static OrgIdValidation Validate(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new OrgIdValidation(
                IsValid: false,
                Value: null,
                Error: "An Org ID is required. It identifies the Identity Defense tenant the " +
                       "agent reports to, and is passed to the installer as INSTALLATION_NAME.",
                Warnings: []);
        }

        var trimmed = rawValue.Trim();
        var warnings = new List<string>();

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
