using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// How <see cref="WinRmSmbTransport"/> reaches a target.
/// </summary>
public sealed class TransportOptions
{
    public const int DefaultHttpPort = 5985;
    public const int DefaultHttpsPort = 5986;

    /// <summary>
    /// WinRM over HTTPS on 5986 (SEC5).
    /// </summary>
    /// <remarks>
    /// Off by default. Negotiate over HTTP applies message-level encryption to the payload, so
    /// 5985 is not plaintext — but customers whose policy requires transport-level TLS can
    /// turn this on. Note that no lab this has been tested against listens on 5986; the code
    /// path is written to the documented behaviour and is not yet verified end to end.
    /// </remarks>
    public bool UseHttps { get; init; }

    /// <summary>Defaults to 5985, or 5986 when <see cref="UseHttps"/> is set.</summary>
    public int Port { get; init; }

    /// <summary>
    /// Explicit alternate credentials, or null to use the operator's current Windows identity.
    /// </summary>
    /// <remarks>
    /// SEC2: integrated authentication is the default; explicit credentials are an option.
    /// The credential is held for the lifetime of the transport, which is one run, and is
    /// never written anywhere (SEC1).
    /// </remarks>
    public OperatorCredential? Credential { get; init; }

    /// <summary>How long to wait for a TCP probe during pre-flight.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a WinRM session to open.</summary>
    public TimeSpan SessionOpenTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Where the MSI is staged on the target. SEC7: under Windows\Temp, which inherits
    /// admin-only ACLs, and never a world-readable location.
    /// </summary>
    public string StagingRoot { get; init; } = @"C:\Windows\Temp\HybridAgentDeploy";

    public int EffectivePort => Port > 0 ? Port : (UseHttps ? DefaultHttpsPort : DefaultHttpPort);

    /// <summary>The administrative share path for a target's system drive.</summary>
    public static string AdminShare(string targetHost) => $@"\\{targetHost}\C$";

    /// <summary>Converts a target-local path such as C:\Windows\Temp\x to its UNC form.</summary>
    public static string ToAdminSharePath(string targetHost, string localPath)
    {
        if (localPath.Length < 2 || localPath[1] != ':')
        {
            throw new ArgumentException(
                $"'{localPath}' is not a rooted local path on the target.", nameof(localPath));
        }

        return $@"\\{targetHost}\{localPath[0]}${localPath[2..]}";
    }
}
