using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.IntegrationTests;

/// <summary>
/// Real Active Directory enumeration against a live forest (PRD 15.2).
/// </summary>
/// <remarks>
/// <para>
/// Every test here carries <c>[Trait("Category","Integration")]</c> and is excluded from the
/// default run. They require a domain-joined machine that can reach a domain controller;
/// they will fail on a standalone workstation, and those failures are environmental, not
/// bugs (CLAUDE.md).
/// </para>
/// <para>
/// Run with: <c>dotnet test --filter "Category=Integration"</c>
/// </para>
/// <para>
/// Read-only throughout. These tests query the directory and never write to it — nothing
/// here modifies a forest.
/// </para>
/// </remarks>
public sealed class ActiveDirectoryEnumerationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumeration_finds_at_least_one_domain_controller()
    {
        var result = await new ActiveDirectoryEnumerator()
            .EnumerateAsync(domainName: null, credential: null, Ct);

        Assert.NotEmpty(result.DomainControllers);
        Assert.NotEmpty(result.DomainsSearched);
    }

    /// <summary>
    /// The FQDN is the identity of a DC in the inventory and its <c>UNIQUE</c> key, so
    /// enumeration must never produce a blank or duplicated one.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Every_enumerated_controller_has_a_usable_fqdn()
    {
        var result = await new ActiveDirectoryEnumerator()
            .EnumerateAsync(domainName: null, credential: null, Ct);

        Assert.All(result.DomainControllers, dc =>
        {
            Assert.False(string.IsNullOrWhiteSpace(dc.Fqdn));
            Assert.Contains('.', dc.Fqdn);
            Assert.Equal(DiscoverySource.AdEnumeration, dc.Source);
        });

        var fqdns = result.DomainControllers.Select(dc => dc.Fqdn).ToList();
        Assert.Equal(fqdns.Count, fqdns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The site drives the PRD R7.2 guard, so an enumeration that returns no sites would
    /// silently disable it.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerated_controllers_carry_a_site_name()
    {
        var result = await new ActiveDirectoryEnumerator()
            .EnumerateAsync(domainName: null, credential: null, Ct);

        Assert.Contains(result.DomainControllers, dc => !string.IsNullOrWhiteSpace(dc.SiteName));
    }

    /// <summary>
    /// The LDAP enrichment pass. A forest with no RODCs legitimately yields all-false, so
    /// what is asserted is that the pass completed without warning — a warning here means
    /// the flag was not read at all and every DC is recorded as writable.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_read_only_flag_is_read_without_falling_back()
    {
        var result = await new ActiveDirectoryEnumerator()
            .EnumerateAsync(domainName: null, credential: null, Ct);

        Assert.DoesNotContain(
            result.Warnings,
            w => w.Contains("read-only (RODC) flag could not be read", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumeration_can_be_narrowed_to_a_single_domain()
    {
        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        Assert.False(
            string.IsNullOrWhiteSpace(domain),
            "USERDNSDOMAIN is unset; this machine is not domain-joined.");

        var result = await new ActiveDirectoryEnumerator()
            .EnumerateAsync(domain, credential: null, Ct);

        Assert.NotEmpty(result.DomainControllers);
        Assert.Single(result.DomainsSearched);
        Assert.All(
            result.DomainControllers,
            dc => Assert.EndsWith(domain!, dc.Fqdn, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// PRD R7.5 and NFR2 both depend on long operations honouring cancellation.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumeration_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ActiveDirectoryEnumerator().EnumerateAsync(null, null, cts.Token));
    }
}
