using HybridAgentDeploy.Core.Msi;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1 and Phase 1 acceptance: MSI extraction against a real sample MSI.
/// </summary>
/// <remarks>
/// The fixture is located through the <c>HAD_TEST_MSI_PATH</c> environment variable. An MSI
/// is never committed to this repository — size and licensing both prohibit it (CLAUDE.md) —
/// so when the variable is unset these tests skip with a message naming it rather than
/// failing on a machine that simply has no fixture.
/// </remarks>
public sealed class MsiInspectorTests
{
    private const string FixtureVariable = "HAD_TEST_MSI_PATH";

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// The fixture path, or null when it is unset or points at a file that is not there.
    /// </summary>
    private static string? FixturePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(FixtureVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            // The variable is often quoted, since the agent MSI's name contains spaces.
            var path = configured.Trim().Trim('"');
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Returns the fixture path, or skips the calling test with a message naming the
    /// variable to set (CLAUDE.md).
    /// </summary>
    private static string RequireFixture()
    {
        var path = FixturePath;
        Skip.If(path is null, SkipReason);
        return path!;
    }

    private static string SkipReason =>
        $"Set {FixtureVariable} to the path of any valid MSI to run this test. " +
        "The real Change Auditor agent MSI is only needed to sanity-check property values; " +
        "any MSI exercises the parser.";

    [SkippableFact]
    public async Task A_real_msi_yields_a_product_version()
    {
        var path = RequireFixture();

        var info = await new MsiInspector().InspectAsync(path, Ct);

        Assert.False(string.IsNullOrWhiteSpace(info.ProductVersion));
        Assert.Equal(Path.GetFileName(path), info.FileName);
        Assert.True(info.FileSizeBytes > 0);
    }

    [SkippableFact]
    public async Task A_real_msi_yields_the_identity_recorded_in_deployment_history()
    {
        var path = RequireFixture();

        var info = await new MsiInspector().InspectAsync(path, Ct);

        // PRD 5.5: ProductCode and UpgradeCode are recorded in history, so they must come
        // back in the GUID form the Windows Installer database stores them in.
        Assert.NotNull(info.ProductCode);
        Assert.StartsWith("{", info.ProductCode, StringComparison.Ordinal);
        Assert.EndsWith("}", info.ProductCode, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(info.ProductCode, out _));

        Assert.NotNull(info.UpgradeCode);
        Assert.True(Guid.TryParse(info.UpgradeCode, out _));

        Assert.False(string.IsNullOrWhiteSpace(info.ProductName));
    }

    /// <summary>
    /// SEC6: the staged copy on each target is verified against this hash before msiexec
    /// runs, so it must be a stable, correctly formatted SHA-256 of the file.
    /// </summary>
    [SkippableFact]
    public async Task The_file_hash_is_a_stable_sha256()
    {
        var path = RequireFixture();

        var inspector = new MsiInspector();
        var first = await inspector.InspectAsync(path, Ct);
        var second = await inspector.InspectAsync(path, Ct);

        Assert.Equal(64, first.Sha256.Length);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Matches("^[0-9A-F]{64}$", first.Sha256);

        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, Ct)));
        Assert.Equal(expected, first.Sha256);
    }

    /// <summary>
    /// PRD 5.5: a leaked Installer object holds a file lock on the MSI. This is the test
    /// that the handle-owning design actually releases it — the operator must be able to
    /// replace the package after inspecting it without restarting the tool.
    /// </summary>
    [SkippableFact]
    public async Task Inspecting_an_msi_does_not_leave_it_locked()
    {
        var source = RequireFixture();

        var copy = Path.Combine(Path.GetTempPath(), $"had-lock-{Guid.NewGuid():N}.msi");
        File.Copy(source, copy);

        try
        {
            await new MsiInspector().InspectAsync(copy, Ct);

            // Deleting is the sharpest available test: on Windows it fails outright while
            // any handle to the file remains open.
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy))
            {
                File.Delete(copy);
            }
        }
    }

    [Fact]
    public async Task A_missing_file_is_reported_with_its_path_and_a_next_step()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"had-missing-{Guid.NewGuid():N}.msi");

        var ex = await Assert.ThrowsAsync<MsiInspectionException>(
            () => new MsiInspector().InspectAsync(missing, Ct));

        // PRD 10.4: name the file and say what to check next.
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.Contains("read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PRD 8.3 blocks deployment when extraction fails, so a file that is not an MSI must
    /// throw rather than return a hollow result a caller might deploy from.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_an_msi_is_rejected()
    {
        var notAnMsi = Path.Combine(Path.GetTempPath(), $"had-bogus-{Guid.NewGuid():N}.msi");
        await File.WriteAllTextAsync(notAnMsi, "This is not a Windows Installer package.", Ct);

        try
        {
            var ex = await Assert.ThrowsAsync<MsiInspectionException>(
                () => new MsiInspector().InspectAsync(notAnMsi, Ct));

            Assert.Contains(Path.GetFileName(notAnMsi), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(notAnMsi);
        }
    }

    [Fact]
    public async Task An_empty_path_is_rejected_before_any_file_access()
    {
        await Assert.ThrowsAsync<MsiInspectionException>(
            () => new MsiInspector().InspectAsync("   ", Ct));
    }

    /// <summary>
    /// PRD 5.5: warn prominently on an unexpected product name, but never hard-block — the
    /// name may change between releases.
    /// </summary>
    [Theory]
    [InlineData("Quest Change Auditor Agent (x64)", true)]
    [InlineData("Identity Defense Hybrid Audit Agent", true)]
    [InlineData("quest change auditor agent", true)]
    [InlineData("Notepad++", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_product_name_sanity_check_recognises_the_expected_agent(string? productName, bool expected)
    {
        Assert.Equal(expected, MsiInspector.LooksLikeExpectedProduct(productName));
    }
}
