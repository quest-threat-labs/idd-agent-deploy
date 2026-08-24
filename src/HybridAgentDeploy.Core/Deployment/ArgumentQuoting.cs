using System.Text;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Joins separated arguments into the single command-line string that
/// <c>CreateProcess</c> requires, using the standard Windows quoting rules.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no argument-array API: <c>CreateProcess</c> takes one string, and the callee
/// parses it. <c>ProcessStartInfo.ArgumentList</c> is a client-side quoter over that same
/// string, nothing more. String construction is therefore unavoidable, and the only real
/// question is whose quoter performs it.
/// </para>
/// <para>
/// It has to be ours. The remote session is Windows PowerShell 5.1 on .NET Framework 4.x on
/// every supported domain controller OS — verified on Server 2019 and Server 2025 in the lab,
/// neither of which offers a PowerShell 7 endpoint — and <c>ProcessStartInfo.ArgumentList</c>
/// does not exist in .NET Framework.
/// </para>
/// <para>
/// What SEC10 still buys us, and what it does not. The process is launched with
/// <c>UseShellExecute = false</c>, so no shell ever sees this string: <c>;</c>, <c>&amp;</c>,
/// <c>|</c> and backticks are ordinary characters to <c>CreateProcess</c> and cannot start a
/// second process. The only parser that reads the Org ID is msiexec's own property parser.
/// The one character that can still escape a property value is a double quote, which is why
/// <see cref="OrgId"/> rejects it outright rather than warning.
/// </para>
/// <para>
/// The algorithm is the documented inverse of <c>CommandLineToArgvW</c>. It is verified by
/// round-tripping through the real <c>CommandLineToArgvW</c> in the test suite rather than by
/// inspection, because a quoter that is subtly wrong about backslash runs is worse than no
/// quoter at all.
/// </para>
/// </remarks>
public static class ArgumentQuoting
{
    /// <summary>Joins arguments into a command line, quoting each as needed.</summary>
    public static string Join(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();

        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            AppendArgument(builder, arguments[i]);
        }

        return builder.ToString();
    }

    /// <summary>Quotes one argument.</summary>
    public static string Quote(string argument)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, argument);
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        // An argument with nothing special in it is passed through untouched, which keeps the
        // command line readable in the run log for the common case.
        if (argument.Length > 0 && !NeedsQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];

            if (c == '\\')
            {
                // A run of backslashes is only special when it precedes a quote, or the end
                // of the argument (where the closing quote we add makes it precede one).
                var backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[index] == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    index++;
                }
                else
                {
                    builder.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                builder.Append('\\').Append('"');
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    private static bool NeedsQuoting(string argument)
    {
        foreach (var c in argument)
        {
            if (c is ' ' or '\t' or '\n' or '\v' or '"')
            {
                return true;
            }
        }

        return false;
    }
}
