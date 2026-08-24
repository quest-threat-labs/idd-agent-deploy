using System.Net;
using System.Net.Sockets;

namespace HybridAgentDeploy.Core.Discovery;

/// <summary>
/// Turns an FQDN, NetBIOS name, or IP address into a fully qualified domain name.
/// </summary>
/// <remarks>
/// An interface rather than a static call so the import tests required by PRD 15.1 —
/// including the unresolvable-host case — run without a domain or a working DNS server.
/// </remarks>
public interface IHostResolver
{
    Task<HostResolution> ResolveAsync(string host, CancellationToken ct);
}

/// <param name="Fqdn">The resolved name, when resolution succeeded.</param>
/// <param name="FailureReason">
/// Operator-facing, per PRD 10.4: says what was tried and what to do next, not just "failed".
/// </param>
public sealed record HostResolution(bool Succeeded, string? Fqdn, string? FailureReason)
{
    public static HostResolution Success(string fqdn) => new(true, fqdn, null);

    public static HostResolution Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Resolves through the operating system's DNS resolver.
/// </summary>
public sealed class DnsHostResolver : IHostResolver
{
    public async Task<HostResolution> ResolveAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return HostResolution.Failed("The entry is empty.");
        }

        var value = host.Trim();

        try
        {
            var entry = await Dns.GetHostEntryAsync(value, ct).ConfigureAwait(false);

            // GetHostEntry on an IP performs the reverse lookup and returns the PTR name;
            // on a bare NetBIOS name it applies the DNS suffix search list. Both cases land
            // here with HostName already fully qualified.
            if (!string.IsNullOrWhiteSpace(entry.HostName))
            {
                return HostResolution.Success(entry.HostName);
            }

            return HostResolution.Failed(
                $"DNS returned no host name for '{value}'.");
        }
        catch (SocketException ex)
        {
            return HostResolution.Failed(
                $"'{value}' did not resolve in DNS ({ex.SocketErrorCode}). " +
                "Confirm the name is spelled correctly and that this workstation uses a DNS " +
                "server authoritative for the target domain — a host listed by NetBIOS name " +
                "also needs that domain in this machine's DNS suffix search list.");
        }
        catch (ArgumentException)
        {
            return HostResolution.Failed(
                $"'{value}' is not a valid host name or IP address.");
        }
    }
}
