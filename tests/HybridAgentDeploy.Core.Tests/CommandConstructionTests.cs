using HybridAgentDeploy.Core.Deployment;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: command construction — correct quoting, injection attempt neutralised, SG=1
/// present when cloud mode is on and absent when off.
/// </summary>
public sealed class CommandConstructionTests
{
    private const string StagedMsi = @"C:\Windows\Temp\HybridAgentDeploy\a1b2c3d4\agent.msi";
    private const string LogPath = @"C:\Windows\Temp\HybridAgentDeploy\a1b2c3d4\install.log";

    [Fact]
    public void The_command_matches_the_specified_msiexec_invocation()
    {
        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org-abc-123", cloudMode: true);

        Assert.Equal("msiexec", command.Executable);
        Assert.Equal(
            [
                "/i",
                StagedMsi,
                "SG=1",
                "INSTALLATION_NAME=org-abc-123",
                "INSTALLATION_NAME_VALID=1",
                "/qn",
                "/l*v",
                LogPath,
            ],
            command.Arguments);
    }

    [Fact]
    public void Cloud_mode_on_emits_the_sg_switch()
    {
        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org", cloudMode: true);

        Assert.Contains("SG=1", command.Arguments);
    }

    /// <summary>
    /// PRD-OPEN-Q: Q1 — until PM confirms whether cloud mode is the only supported mode, the
    /// switch must genuinely disappear rather than being emitted as SG=0.
    /// </summary>
    [Fact]
    public void Cloud_mode_off_omits_the_sg_switch_entirely()
    {
        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org", cloudMode: false);

        Assert.DoesNotContain("SG=1", command.Arguments);
        Assert.DoesNotContain(command.Arguments, a => a.StartsWith("SG=", StringComparison.Ordinal));
    }

    /// <summary>
    /// SEC10, verbatim: feed it <c>"; Stop-Service NTDS; "</c> and assert the resulting
    /// command is inert.
    /// </summary>
    /// <remarks>
    /// The assertion is structural rather than textual. What makes the value harmless is that
    /// it occupies exactly one argument slot and nothing downstream re-parses it — so the test
    /// checks the argument list, not an escaped rendering. Stopping NTDS on a domain
    /// controller takes Active Directory down with it, which is why this case is called out
    /// by name in the specification.
    /// </remarks>
    [Fact]
    public void An_injection_attempt_in_the_org_id_stays_inside_one_argument()
    {
        const string Malicious = "\"; Stop-Service NTDS; \"";

        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, Malicious, cloudMode: true);

        var installationName = Assert.Single(
            command.Arguments,
            a => a.StartsWith("INSTALLATION_NAME=", StringComparison.Ordinal));

        // The whole payload is the property value and nothing more.
        Assert.Equal($"INSTALLATION_NAME={Malicious}", installationName);

        // No argument is the injected command, and the argument count is exactly what a
        // benign Org ID produces — nothing split out into a second command.
        Assert.DoesNotContain(command.Arguments, a => a.Contains("Stop-Service", StringComparison.OrdinalIgnoreCase)
            && !a.StartsWith("INSTALLATION_NAME=", StringComparison.Ordinal));

        var benign = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org", cloudMode: true);
        Assert.Equal(benign.Arguments.Count, command.Arguments.Count);
    }

    [Theory]
    [InlineData("org & whoami")]
    [InlineData("org | Get-Process")]
    [InlineData("org`nStop-Service NTDS")]
    [InlineData("$(Stop-Service NTDS)")]
    [InlineData("org\"quote")]
    [InlineData("org'quote")]
    [InlineData("org;semicolon")]
    public void No_metacharacter_can_add_an_argument(string orgId)
    {
        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, orgId, cloudMode: true);
        var benign = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org", cloudMode: true);

        Assert.Equal(benign.Arguments.Count, command.Arguments.Count);
        Assert.Contains($"INSTALLATION_NAME={orgId}", command.Arguments);
    }

    /// <summary>
    /// The most important design decision in the tool (PRD 5.4). A defect that produced a UNC
    /// path here would silently reintroduce the Kerberos double-hop failure, in a way that
    /// only reproduces in a customer environment.
    /// </summary>
    [Theory]
    [InlineData(@"\\DC01.corp.local\C$\Windows\Temp\agent.msi")]
    [InlineData("//DC01.corp.local/C$/Windows/Temp/agent.msi")]
    public void A_unc_staged_path_is_refused(string uncPath)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MsiCommandBuilder.BuildInstallCommand(uncPath, LogPath, "org", cloudMode: true));

        Assert.Contains("double-hop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PRD 8.3 and 12.2: the operator previews this before deploying, and a customer may need
    /// to show a change-control board exactly what ran.
    /// </summary>
    [Fact]
    public void The_display_form_renders_a_readable_command_line()
    {
        var command = MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, "org-abc-123", cloudMode: true);

        var display = command.ToDisplayString();

        Assert.StartsWith("msiexec /i ", display, StringComparison.Ordinal);
        Assert.Contains("SG=1", display, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME=org-abc-123", display, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME_VALID=1", display, StringComparison.Ordinal);
        Assert.Contains("/qn", display, StringComparison.Ordinal);
        Assert.Contains("/l*v", display, StringComparison.Ordinal);
    }

    [Fact]
    public void The_display_form_quotes_values_that_would_otherwise_be_ambiguous()
    {
        var command = new RemoteCommand("msiexec", ["/i", @"C:\Program Files\agent.msi"]);

        Assert.Contains("\"C:\\Program Files\\agent.msi\"", command.ToDisplayString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_org_id_is_refused_by_the_builder(string orgId)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => MsiCommandBuilder.BuildInstallCommand(StagedMsi, LogPath, orgId, cloudMode: true));
    }
}

/// <summary>
/// Org ID validation. PRD-OPEN-Q: Q2 — the format is unknown, so the documented default
/// applies: non-empty, trimmed, warn on characters needing quoting.
/// </summary>
public sealed class OrgIdValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_empty_org_id_is_invalid(string? value)
    {
        var result = OrgId.Validate(value);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("INSTALLATION_NAME", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_org_id_validates_without_warnings()
    {
        var result = OrgId.Validate("org-abc-123");

        Assert.True(result.IsValid);
        Assert.Equal("org-abc-123", result.Value);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_and_reported()
    {
        var result = OrgId.Validate("  org-abc-123  ");

        Assert.True(result.IsValid);
        Assert.Equal("org-abc-123", result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("whitespace", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// PRD 8.3 asks for a warning on characters that would need quoting. Because Q2 leaves
    /// the format unknown, this must warn rather than block — rejecting could refuse a
    /// legitimate Org ID with no operator override.
    /// </summary>
    [Fact]
    public void Shell_metacharacters_warn_but_do_not_block()
    {
        var result = OrgId.Validate("\"; Stop-Service NTDS; \"");

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("cannot affect the command", StringComparison.Ordinal));
    }

    [Fact]
    public void An_embedded_space_is_flagged_as_a_likely_paste_error()
    {
        var result = OrgId.Validate("org abc");

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("space", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_control_character_is_flagged()
    {
        var result = OrgId.Validate("org\u0007abc");

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("control character", StringComparison.OrdinalIgnoreCase));
    }
}
