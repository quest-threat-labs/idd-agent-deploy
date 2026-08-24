using System.Text;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// A command to run on a target, held as an executable plus a list of already-separated
/// arguments.
/// </summary>
/// <remarks>
/// <para>
/// SEC10 requires the msiexec command line to be built with proper argument quoting rather
/// than string interpolation, because the Org ID is operator-supplied free text that ends up
/// in a command executed with elevated rights on a Tier 0 host.
/// </para>
/// <para>
/// Keeping the arguments as a list rather than a pre-joined string is what makes that
/// guarantee structural. An argument never has to escape its own boundary, so there is no
/// quoting scheme to get wrong and no shell to re-parse it. This is a deviation from the
/// <c>string commandLine</c> parameter in PRD 5.3, taken deliberately: the alternative makes
/// the safety of a Tier 0 command depend on a quoter being correct and on every future
/// transport honouring an unwritten contract.
/// </para>
/// <para>
/// <b>Contract for any <see cref="ITargetTransport"/> implementation:</b> pass
/// <see cref="Arguments"/> to the target process through a mechanism that preserves argument
/// boundaries — PowerShell native argument passing, or <c>ProcessStartInfo.ArgumentList</c>.
/// Never join them into a string and never evaluate them through <c>Invoke-Expression</c>,
/// <c>cmd /c</c>, or any other shell.
/// </para>
/// </remarks>
/// <param name="Executable">The program to run, with no arguments embedded.</param>
/// <param name="Arguments">
/// One element per argument, already separated. Values are literal: they are not escaped,
/// quoted, or otherwise interpreted here, because nothing downstream is permitted to
/// re-parse them.
/// </param>
public sealed record RemoteCommand(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The command rendered as a single readable line, for the operator preview (PRD 8.3)
    /// and the run log (PRD 12.2).
    /// </summary>
    /// <remarks>
    /// Display only. PRD 12.2 requires the exact command to be recorded so a customer can
    /// show a change-control board what ran against their domain controllers, and PRD 8.3
    /// requires the operator to see it before it executes. It is never the thing that is
    /// executed — <see cref="Arguments"/> is.
    /// </remarks>
    public string ToDisplayString()
    {
        var builder = new StringBuilder(Quote(Executable));

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Wraps a value in quotes when it contains anything that would make the rendered line
    /// ambiguous to read. Presentation only; see the remarks on
    /// <see cref="ToDisplayString"/>.
    /// </summary>
    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '&' or '|' or '`');
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public override string ToString() => ToDisplayString();
}
