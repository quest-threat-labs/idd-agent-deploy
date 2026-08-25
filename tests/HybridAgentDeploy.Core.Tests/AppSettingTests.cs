using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Inventory;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// The values the tool remembers between sessions.
/// </summary>
/// <remarks>
/// Introduced for the Org ID. An operator upgrading a forest opens this tool repeatedly over
/// days, and retyping a tenant GUID each time is both tedious and a way to get it wrong.
/// </remarks>
public sealed class AppSettingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_value_survives_being_written_and_read_back()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.SetAsync("some.key", "a value", Ct);

        Assert.Equal("a value", await settings.GetAsync("some.key", Ct));
    }

    [Fact]
    public async Task An_unknown_key_reads_as_null_rather_than_throwing()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        Assert.Null(await settings.GetAsync("never.written", Ct));
    }

    [Fact]
    public async Task Writing_a_key_twice_replaces_rather_than_duplicates()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.SetAsync("some.key", "first", Ct);
        await settings.SetAsync("some.key", "second", Ct);

        Assert.Equal("second", await settings.GetAsync("some.key", Ct));
    }

    /// <summary>
    /// Clearing the box and starting a run must not leave an empty string behind, or the next
    /// session would prefill emptiness and behave differently from a fresh database for no
    /// reason the operator could see.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_value_removes_the_key(string? blank)
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.SetAsync("some.key", "a value", Ct);
        await settings.SetAsync("some.key", blank, Ct);

        Assert.Null(await settings.GetAsync("some.key", Ct));
    }

    [Fact]
    public async Task A_stored_value_is_trimmed()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.SetAsync("some.key", "  padded  ", Ct);

        Assert.Equal("padded", await settings.GetAsync("some.key", Ct));
    }

    // ---------------------------------------------------------------------------------------
    // The Org ID specifically.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One remembered value per mode.
    /// </summary>
    /// <remarks>
    /// The two modes take different kinds of identifier — a tenant GUID for Identity Defense,
    /// a short installation name for Change Auditor — so a single remembered value would
    /// prefill the wrong kind every time the operator switched the cloud-mode checkbox, and
    /// the mismatch warning would then fire on a value the tool itself had supplied.
    /// </remarks>
    [Fact]
    public async Task Each_mode_remembers_its_own_org_id()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.RememberOrgIdAsync(
            AgentMode.IdentityDefense, "c3a22555-da90-4a57-8042-c543d0c32bc3", Ct);
        await settings.RememberOrgIdAsync(AgentMode.ChangeAuditor, "DEFAULT", Ct);

        Assert.Equal(
            "c3a22555-da90-4a57-8042-c543d0c32bc3",
            await settings.GetLastOrgIdAsync(AgentMode.IdentityDefense, Ct));

        Assert.Equal("DEFAULT", await settings.GetLastOrgIdAsync(AgentMode.ChangeAuditor, Ct));
    }

    /// <summary>
    /// Remembering one mode's value must not disturb the other's — the failure that a single
    /// shared key would produce.
    /// </summary>
    [Fact]
    public async Task Remembering_one_mode_leaves_the_other_untouched()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        await settings.RememberOrgIdAsync(AgentMode.ChangeAuditor, "DEFAULT", Ct);
        await settings.RememberOrgIdAsync(AgentMode.IdentityDefense, "a-guid", Ct);
        await settings.RememberOrgIdAsync(AgentMode.IdentityDefense, "a-different-guid", Ct);

        Assert.Equal("DEFAULT", await settings.GetLastOrgIdAsync(AgentMode.ChangeAuditor, Ct));
        Assert.Equal("a-different-guid", await settings.GetLastOrgIdAsync(AgentMode.IdentityDefense, Ct));
    }

    [Fact]
    public async Task Nothing_is_remembered_in_a_fresh_database()
    {
        await using var inventory = await TemporaryInventory.CreateAsync(Ct);
        var settings = new AppSettingRepository(inventory.Connections);

        Assert.Null(await settings.GetLastOrgIdAsync(AgentMode.IdentityDefense, Ct));
        Assert.Null(await settings.GetLastOrgIdAsync(AgentMode.ChangeAuditor, Ct));
    }

    /// <summary>The two modes cannot collide on one key.</summary>
    [Fact]
    public void The_key_differs_per_mode()
    {
        Assert.NotEqual(
            AppSettingRepository.OrgIdKey(AgentMode.IdentityDefense),
            AppSettingRepository.OrgIdKey(AgentMode.ChangeAuditor));
    }
}
