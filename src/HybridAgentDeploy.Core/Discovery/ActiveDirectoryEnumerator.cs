using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.Runtime.Versioning;
using HybridAgentDeploy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HybridAgentDeploy.Core.Discovery;

/// <summary>
/// Discovers domain controllers from Active Directory.
/// </summary>
public interface IDomainControllerEnumerator
{
    /// <summary>
    /// Enumerates domain controllers.
    /// </summary>
    /// <param name="domainName">
    /// Narrows enumeration to one domain. Null enumerates every domain in the current
    /// forest. Multi-forest is out of scope (PRD 3.2).
    /// </param>
    Task<EnumerationResult> EnumerateAsync(
        string? domainName,
        OperatorCredential? credential,
        CancellationToken ct);
}

/// <param name="DomainControllers">Everything discovered, ready to upsert.</param>
/// <param name="DomainsSearched">
/// Which domains were reached. Reported so an operator seeing a short list can tell a small
/// forest from a partially failed enumeration.
/// </param>
/// <param name="Warnings">
/// Non-fatal problems, such as a child domain that could not be contacted. Enumeration
/// continues past these: returning the DCs that were found beats failing the whole operation
/// because one domain in the forest is unreachable.
/// </param>
public sealed record EnumerationResult(
    IReadOnlyList<DomainControllerRecord> DomainControllers,
    IReadOnlyList<string> DomainsSearched,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Enumerates domain controllers forest-wide through
/// <c>System.DirectoryServices.ActiveDirectory</c>, enriching each with attributes only an
/// LDAP read exposes.
/// </summary>
/// <remarks>
/// <para>
/// Two passes are needed. <see cref="DomainController"/> supplies the name, site, OS version
/// and global-catalog role, but has no read-only property at all — RODC status lives on the
/// computer object in the directory, in <c>msDS-isRODC</c> or the
/// <c>PARTIAL_SECRETS_ACCOUNT</c> bit of <c>userAccountControl</c>. The inventory grid shows
/// an RODC column (PRD 8.1) and RODCs are one of the subsets operators most often want to
/// target separately, so leaving the flag permanently false is not an option.
/// </para>
/// <para>
/// Read-only throughout. This class queries the directory and never writes to it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryEnumerator : IDomainControllerEnumerator
{
    /// <summary>userAccountControl bit set on the computer object of a read-only DC.</summary>
    private const int UserAccountControlPartialSecretsAccount = 0x04000000;

    private readonly ILogger<ActiveDirectoryEnumerator> _log;

    public ActiveDirectoryEnumerator(ILogger<ActiveDirectoryEnumerator>? log = null)
    {
        _log = log ?? NullLogger<ActiveDirectoryEnumerator>.Instance;
    }

    public Task<EnumerationResult> EnumerateAsync(
        string? domainName,
        OperatorCredential? credential,
        CancellationToken ct)
    {
        // System.DirectoryServices is entirely synchronous and blocking. Pushing it to the
        // thread pool keeps NFR2's "no blocking calls on the UI thread" true for the caller.
        return Task.Run(() => Enumerate(domainName, credential, ct), ct);
    }

    private EnumerationResult Enumerate(
        string? domainName,
        OperatorCredential? credential,
        CancellationToken ct)
    {
        var discovered = new List<DomainControllerRecord>();
        var domainsSearched = new List<string>();
        var warnings = new List<string>();

        foreach (var domain in GetDomains(domainName, credential, warnings))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var rodcFlags = ReadReadOnlyFlags(domain.Name, credential, warnings, ct);

                foreach (DomainController dc in domain.DomainControllers)
                {
                    ct.ThrowIfCancellationRequested();

                    using (dc)
                    {
                        discovered.Add(ToRecord(dc, domain.Name, rodcFlags, warnings));
                    }
                }

                domainsSearched.Add(domain.Name);
            }
            catch (ActiveDirectoryOperationException ex)
            {
                warnings.Add(
                    $"Domain '{domain.Name}' could not be enumerated: {ex.Message} " +
                    "Its domain controllers are absent from these results. Confirm this " +
                    "workstation can reach a domain controller in that domain.");
            }
            finally
            {
                domain.Dispose();
            }
        }

        _log.LogInformation(
            "Enumerated {Count} domain controller(s) across {Domains} domain(s) with {Warnings} warning(s).",
            discovered.Count,
            domainsSearched.Count,
            warnings.Count);

        return new EnumerationResult(discovered, domainsSearched, warnings);
    }

    private static List<Domain> GetDomains(
        string? domainName,
        OperatorCredential? credential,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(domainName))
        {
            return [Domain.GetDomain(CreateContext(DirectoryContextType.Domain, domainName, credential))];
        }

        // Forest-wide by default: a forest root plus child domains is one administrative
        // boundary as far as this tool is concerned, and an operator asked to enumerate
        // expects every DC they are responsible for. Still a single forest — PRD 3.2 rules
        // out multi-forest.
        using var forest = Forest.GetForest(
            CreateContext(DirectoryContextType.Forest, forestOrDomainName: null, credential));

        var domains = new List<Domain>();
        foreach (Domain domain in forest.Domains)
        {
            domains.Add(domain);
        }

        if (domains.Count == 0)
        {
            warnings.Add(
                $"Forest '{forest.Name}' reported no domains. Confirm this workstation is " +
                "joined to the forest, or supply an explicit domain name.");
        }

        return domains;
    }

    private static DirectoryContext CreateContext(
        DirectoryContextType type,
        string? forestOrDomainName,
        OperatorCredential? credential)
    {
        // SEC2: the current Windows identity is the default. An explicit credential is
        // supplied only when the operator chose one.
        if (credential is null)
        {
            return forestOrDomainName is null
                ? new DirectoryContext(type)
                : new DirectoryContext(type, forestOrDomainName);
        }

        // DirectoryContext accepts only a plaintext password, so the value is materialised
        // inside OperatorCredential.UsePassword, which zeroes it immediately afterwards.
        // The context itself is never logged (SEC1, PRD 12.3).
        return credential.UsePassword(password => forestOrDomainName is null
            ? new DirectoryContext(type, credential.AccountName, password)
            : new DirectoryContext(type, forestOrDomainName, credential.AccountName, password));
    }

    /// <summary>
    /// Reads the read-only flag for every DC computer object in a domain, in one query.
    /// </summary>
    /// <remarks>
    /// One search rather than a per-DC lookup: NFR3 budgets a second for 500 DCs, and a
    /// round trip per controller would not fit. A failure here degrades to "not read-only"
    /// with a warning rather than failing enumeration — an inventory missing an RODC flag is
    /// far more useful than no inventory.
    /// </remarks>
    private static Dictionary<string, bool> ReadReadOnlyFlags(
        string domainName,
        OperatorCredential? credential,
        List<string> warnings,
        CancellationToken ct)
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var root = CreateSearchRoot(domainName, credential);
            using var searcher = new DirectorySearcher(root)
            {
                // Domain controllers are the computer objects holding the DC account type.
                Filter = "(&(objectCategory=computer)(userAccountControl:1.2.840.113556.1.4.803:=8192))",
                PageSize = 500,
                SearchScope = SearchScope.Subtree,
            };

            searcher.PropertiesToLoad.Add("dNSHostName");
            searcher.PropertiesToLoad.Add("userAccountControl");
            searcher.PropertiesToLoad.Add("msDS-isRODC");

            using var results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                ct.ThrowIfCancellationRequested();

                var dnsHostName = GetSingleValue(result, "dNSHostName") as string;
                if (string.IsNullOrWhiteSpace(dnsHostName))
                {
                    continue;
                }

                flags[dnsHostName] = IsReadOnly(result);
            }
        }
        catch (Exception ex) when (
            ex is DirectoryServicesCOMException or
                  System.Runtime.InteropServices.COMException or
                  UnauthorizedAccessException)
        {
            warnings.Add(
                $"The read-only (RODC) flag could not be read for domain '{domainName}': " +
                $"{ex.Message} Those controllers are recorded as writable; re-run enumeration " +
                "with an account that can read computer objects in that domain to correct it.");
        }

        return flags;
    }

    private static bool IsReadOnly(SearchResult result)
    {
        // Server 2008+ exposes msDS-isRODC directly. Where the constructed attribute is
        // unavailable, the userAccountControl bit gives the same answer.
        if (GetSingleValue(result, "msDS-isRODC") is bool isRodc)
        {
            return isRodc;
        }

        return GetSingleValue(result, "userAccountControl") is int uac &&
               (uac & UserAccountControlPartialSecretsAccount) != 0;
    }

    private static object? GetSingleValue(SearchResult result, string propertyName) =>
        result.Properties.Contains(propertyName) && result.Properties[propertyName].Count > 0
            ? result.Properties[propertyName][0]
            : null;

    private static DirectoryEntry CreateSearchRoot(string domainName, OperatorCredential? credential)
    {
        var path = $"LDAP://{domainName}";

        if (credential is null)
        {
            return new DirectoryEntry(path, null, null, AuthenticationTypes.Secure);
        }

        return credential.UsePassword(password =>
            new DirectoryEntry(path, credential.AccountName, password, AuthenticationTypes.Secure));
    }

    private static DomainControllerRecord ToRecord(
        DomainController dc,
        string domainName,
        IReadOnlyDictionary<string, bool> rodcFlags,
        List<string> warnings)
    {
        string? siteName = null;
        string? osVersion = null;
        var isGlobalCatalog = false;

        // Each of these is a live directory call that can fail independently, and none is
        // worth losing the whole record over — a DC with an unknown site is still a DC that
        // needs the agent.
        try
        {
            siteName = dc.SiteName;
        }
        catch (ActiveDirectoryObjectNotFoundException)
        {
            warnings.Add($"The site of '{dc.Name}' could not be read; it is recorded as unknown.");
        }

        try
        {
            osVersion = dc.OSVersion;
        }
        catch (ActiveDirectoryOperationException)
        {
            warnings.Add($"The OS version of '{dc.Name}' could not be read.");
        }

        try
        {
            isGlobalCatalog = dc.IsGlobalCatalog();
        }
        catch (ActiveDirectoryOperationException)
        {
            warnings.Add($"The global-catalog role of '{dc.Name}' could not be read.");
        }

        var fqdn = dc.Name;
        var dot = fqdn.IndexOf('.');

        return new DomainControllerRecord
        {
            Fqdn = fqdn,
            NetbiosName = (dot > 0 ? fqdn[..dot] : fqdn).ToUpperInvariant(),
            Domain = domainName,
            SiteName = siteName,
            OsVersion = osVersion,
            IsReadOnly = rodcFlags.GetValueOrDefault(fqdn),
            IsGlobalCatalog = isGlobalCatalog,
            Source = DiscoverySource.AdEnumeration,
        };
    }
}
