using System.Runtime.InteropServices;
using HybridAgentDeploy.Core.Deployment;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// Verifies the command-line quoter against the real Windows parser.
/// </summary>
/// <remarks>
/// Asserting the quoter's output by eye would only prove it produces the string I expected.
/// What matters is that Windows reads it back as the arguments we meant, so every case here
/// round-trips through the actual <c>CommandLineToArgvW</c> — the function whose inverse this
/// algorithm claims to be. A quoter that is subtly wrong about backslash runs is worse than
/// no quoter at all, and only the real parser can settle that.
/// </remarks>
public sealed class ArgumentQuotingTests
{
    [Theory]
    // The ordinary case: nothing needing quotes passes through untouched.
    [InlineData("msiexec")]
    [InlineData("/i")]
    [InlineData("/qn")]
    [InlineData("SG=1")]
    [InlineData("INSTALLATION_NAME=org-abc-123")]
    // Spaces, which is why quoting exists at all.
    [InlineData(@"C:\Program Files\agent.msi")]
    [InlineData("INSTALLATION_NAME=my org")]
    // Backslash runs, where naive quoters break.
    [InlineData(@"C:\Windows\Temp\")]
    [InlineData(@"C:\path with space\")]
    [InlineData(@"trailing\\")]
    [InlineData(@"a\\\b")]
    [InlineData(@"\\server\share\file.msi")]
    // Characters a shell would treat as special, which CreateProcess does not.
    [InlineData("org;semicolon")]
    [InlineData("org&ampersand")]
    [InlineData("org|pipe")]
    [InlineData("$(Stop-Service NTDS)")]
    [InlineData("; Stop-Service NTDS; ")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_single_argument_round_trips_through_the_windows_parser(string argument)
    {
        AssertRoundTrips([argument]);
    }

    [Fact]
    public void A_full_msiexec_command_round_trips()
    {
        AssertRoundTrips(
        [
            "/i",
            @"C:\Windows\Temp\HybridAgentDeploy\6df6429a-0000-0000-0000-000000000000\agent.msi",
            "SG=1",
            "INSTALLATION_NAME=org-abc-123",
            "INSTALLATION_NAME_VALID=1",
            "/qn",
            "/l*v",
            @"C:\Windows\Temp\HybridAgentDeploy\6df6429a-0000-0000-0000-000000000000\install.log",
        ]);
    }

    /// <summary>
    /// The SEC10 case, now asserted against the real parser: the payload must come back as
    /// exactly one argument, not as a command that could run.
    /// </summary>
    [Fact]
    public void An_injection_attempt_round_trips_as_one_argument()
    {
        string[] arguments =
        [
            "/i",
            @"C:\Windows\Temp\agent.msi",
            "INSTALLATION_NAME=; Stop-Service NTDS; ",
            "/qn",
        ];

        var parsed = AssertRoundTrips(arguments);

        Assert.Equal("INSTALLATION_NAME=; Stop-Service NTDS; ", parsed[2]);
        Assert.DoesNotContain(parsed, a => a.Equals("Stop-Service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_argument_with_a_space_is_quoted_as_a_whole_token()
    {
        // The consequence of standard argv quoting, recorded so the behaviour is deliberate:
        // msiexec documents PROPERTY="value with spaces", whereas argv rules produce
        // "PROPERTY=value with spaces". Windows parses both back to the same argument; whether
        // msiexec's own property parser agrees is the open part, which is why OrgId warns on
        // an embedded space. PRD-OPEN-Q: Q2 will settle whether an Org ID can contain one.
        Assert.Equal("\"INSTALLATION_NAME=my org\"", ArgumentQuoting.Quote("INSTALLATION_NAME=my org"));
    }

    [Fact]
    public void An_empty_argument_survives_as_an_empty_argument()
    {
        Assert.Equal("\"\"", ArgumentQuoting.Quote(string.Empty));
    }

    /// <summary>
    /// A trailing backslash immediately before the closing quote would otherwise escape it and
    /// swallow the following argument. Staged paths are directories, so this is not exotic.
    /// </summary>
    [Fact]
    public void A_trailing_backslash_does_not_escape_the_closing_quote()
    {
        var parsed = AssertRoundTrips([@"C:\Windows\Temp\Hybrid Agent\", "/qn"]);

        Assert.Equal(@"C:\Windows\Temp\Hybrid Agent\", parsed[0]);
        Assert.Equal("/qn", parsed[1]);
    }

    /// <summary>
    /// Runs the quoter's output through the real Windows command-line parser and asserts the
    /// arguments come back unchanged.
    /// </summary>
    private static string[] AssertRoundTrips(string[] arguments)
    {
        var commandLine = ArgumentQuoting.Join(arguments);

        // CommandLineToArgvW treats the first token as a program name and parses it by
        // different rules, so a placeholder is prepended and dropped from the comparison.
        var parsed = ParseCommandLine("program.exe " + commandLine).Skip(1).ToArray();

        Assert.Equal(arguments, parsed);
        return parsed;
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CommandLineToArgvW rejected '{commandLine}': " +
                Marshal.GetLastWin32Error());
        }

        try
        {
            var results = new string[count];
            for (var i = 0; i < count; i++)
            {
                var element = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                results[i] = Marshal.PtrToStringUni(element) ?? string.Empty;
            }

            return results;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CommandLineToArgvW")]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
