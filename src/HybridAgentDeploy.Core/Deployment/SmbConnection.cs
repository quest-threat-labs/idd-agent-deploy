using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// An authenticated SMB connection to a target's administrative share, released on dispose.
/// </summary>
/// <remarks>
/// <para>
/// Ordinary file I/O authenticates with the process token, so explicit alternate credentials
/// (SEC2) would otherwise be honoured for WinRM execution and silently ignored for SMB
/// staging — the sort of split behaviour that produces a confusing access-denied on one DC of
/// many. <c>WNetAddConnection2</c> establishes the session under the supplied credentials
/// first, after which normal streamed file I/O uses it.
/// </para>
/// <para>
/// With integrated authentication no connection is established at all: the operator's
/// existing token already works, and adding a redundant mapping would only create something
/// else to leak.
/// </para>
/// <para>
/// SEC1: the password is materialised only inside
/// <see cref="OperatorCredential.UsePassword{T}"/>, for the duration of the single
/// <c>WNetAddConnection2</c> call, and is never stored on this object.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SmbConnection : IDisposable
{
    private const int ResourceTypeDisk = 0x00000001;
    private const int ConnectTemporary = 0x00000004;

    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetName = 67;
    private const int ErrorNetworkUnreachable = 1231;

    private readonly string? _remoteName;
    private bool _disposed;

    private SmbConnection(string? remoteName) => _remoteName = remoteName;

    /// <summary>
    /// Connects to <c>\\host\C$</c> under the supplied credentials, or returns a no-op handle
    /// when integrated authentication is in use.
    /// </summary>
    /// <exception cref="SmbConnectionException">
    /// The share could not be reached or the credentials were rejected. The message names the
    /// host and the next diagnostic step (PRD 10.4).
    /// </exception>
    public static SmbConnection Connect(string targetHost, OperatorCredential? credential)
    {
        if (credential is null)
        {
            return new SmbConnection(remoteName: null);
        }

        var remoteName = TransportOptions.AdminShare(targetHost);

        var resource = new NetResource
        {
            Scope = 0,
            Type = ResourceTypeDisk,
            DisplayType = 0,
            Usage = 0,
            LocalName = null,
            RemoteName = remoteName,
            Comment = null,
            Provider = null,
        };

        var result = credential.UsePassword(password =>
            WNetAddConnection2(ref resource, password, credential.AccountName, ConnectTemporary));

        return result switch
        {
            NoError => new SmbConnection(remoteName),

            // Windows permits only one credential set per server at a time. This usually means
            // the operator already has a session to that DC as themselves.
            ErrorSessionCredentialConflict => throw new SmbConnectionException(
                $"Windows already holds a connection to {targetHost} under different " +
                $"credentials, so '{credential.AccountName}' cannot be used for staging. " +
                $"Close the existing connection (`net use \\\\{targetHost}\\C$ /delete`) and " +
                "retry, or run the utility as the account you intend to deploy with.",
                result),

            ErrorLogonFailure => throw new SmbConnectionException(
                $"The credentials for '{credential.AccountName}' were rejected by {targetHost}. " +
                "Confirm the account name and password, and that the account is not locked out.",
                result),

            ErrorAccessDenied => throw new SmbConnectionException(
                $"'{credential.AccountName}' was denied access to {TransportOptions.AdminShare(targetHost)}. " +
                "Confirm the account holds local administrator rights on this domain controller.",
                result),

            ErrorBadNetName => throw new SmbConnectionException(
                $"{TransportOptions.AdminShare(targetHost)} does not exist or is not shared. " +
                "Confirm the administrative shares have not been disabled on this domain controller.",
                result),

            ErrorNetworkUnreachable => throw new SmbConnectionException(
                $"{targetHost} was unreachable over the network when connecting to its " +
                "administrative share. Confirm routing and that TCP 445 is permitted.",
                result),

            _ => throw new SmbConnectionException(
                $"Connecting to {TransportOptions.AdminShare(targetHost)} as " +
                $"'{credential.AccountName}' failed: {new Win32Exception(result).Message} " +
                $"(Windows error {result}).",
                result),
        };
    }

    public void Dispose()
    {
        if (_disposed || _remoteName is null)
        {
            return;
        }

        _disposed = true;

        // Best effort. A connection that outlives the run is untidy but harmless, and
        // throwing from Dispose would mask whatever the caller was already handling.
        _ = WNetCancelConnection2(_remoteName, dwFlags: 0, fForce: true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection2(
        ref NetResource lpNetResource,
        string? lpPassword,
        string? lpUserName,
        int dwFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);
}

/// <summary>Raised when an SMB session to a target's administrative share cannot be made.</summary>
public sealed class SmbConnectionException(string message, int windowsErrorCode) : Exception(message)
{
    public int WindowsErrorCode { get; } = windowsErrorCode;
}
