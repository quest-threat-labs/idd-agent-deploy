using HybridAgentDeploy.Core.Discovery;

namespace HybridAgentDeploy.Core.Testing;

/// <summary>
/// An <see cref="IHostResolver"/> with a fixed answer per host.
/// </summary>
/// <remarks>
/// PRD 15.1 requires the import-parsing tests — including the unresolvable-host case — to
/// pass without a domain. Real DNS cannot provide a reliably unresolvable name or a reliably
/// resolvable one on an arbitrary developer machine, so resolution is stubbed.
/// </remarks>
public sealed class StubHostResolver : IHostResolver
{
    private readonly Dictionary<string, HostResolution> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What to return for a host with no explicit answer. Defaults to failure, so a test
    /// that forgets to configure a host sees an obvious result rather than a silent success.
    /// </summary>
    public HostResolution DefaultAnswer { get; set; } =
        HostResolution.Failed("No resolution was configured for this host.");

    public StubHostResolver Resolves(string input, string fqdn)
    {
        _answers[input] = HostResolution.Success(fqdn);
        return this;
    }

    /// <summary>Convenience for a host that resolves to the name it was written as.</summary>
    public StubHostResolver ResolvesToItself(params string[] fqdns)
    {
        foreach (var fqdn in fqdns)
        {
            _answers[fqdn] = HostResolution.Success(fqdn);
        }

        return this;
    }

    public StubHostResolver FailsToResolve(string input, string reason)
    {
        _answers[input] = HostResolution.Failed(reason);
        return this;
    }

    public Task<HostResolution> ResolveAsync(string host, CancellationToken ct) =>
        Task.FromResult(_answers.GetValueOrDefault(host.Trim(), DefaultAnswer));
}
