using System.Runtime.InteropServices;
using System.Security;

namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// Explicit alternate credentials for a single run.
/// </summary>
/// <remarks>
/// <para>
/// SEC2: the default is the operator's current Windows identity, represented throughout
/// this codebase by a <see langword="null"/> credential. This type exists only for the
/// case where the operator supplies a different account.
/// </para>
/// <para>
/// SEC1: no persistence. The password is held as a <see cref="SecureString"/> for the
/// duration of a run and cleared on dispose. It is never written to the database, to
/// configuration, or to any log. <see cref="ToString"/> is overridden to return the
/// account name so that an accidental interpolation into a log message cannot leak it
/// (PRD 12.3).
/// </para>
/// </remarks>
public sealed class OperatorCredential : IDisposable
{
    private readonly SecureString _password;
    private bool _disposed;

    public OperatorCredential(string accountName, SecureString password)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException("An account name is required.", nameof(accountName));
        }

        AccountName = accountName.Trim();
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _password.MakeReadOnly();
    }

    /// <summary>The account name only. Safe to log (PRD 12.3).</summary>
    public string AccountName { get; }

    /// <summary>
    /// Runs <paramref name="action"/> with the plaintext password, then zeroes the
    /// unmanaged copy.
    /// </summary>
    /// <remarks>
    /// Some Windows APIs this tool must call — DirectoryContext in particular — accept only
    /// a plaintext string. Materialising it inside a narrow, self-clearing scope is the
    /// closest achievable equivalent, and confining it to this one method means there is a
    /// single place to audit.
    /// </remarks>
    public T UsePassword<T>(Func<string, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(_password);
            var plaintext = Marshal.PtrToStringBSTR(bstr);
            return action(plaintext);
        }
        finally
        {
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the password still protected.
    /// </summary>
    /// <remarks>
    /// Preferred over <see cref="UsePassword{T}"/> wherever the consuming API accepts a
    /// <see cref="SecureString"/> — <c>PSCredential</c> does — because it never materialises
    /// the plaintext at all. The instance handed over is read-only; callers must not dispose
    /// it, since this object owns its lifetime.
    /// </remarks>
    public T UseSecurePassword<T>(Func<SecureString, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return action(_password);
    }

    /// <summary>Returns the account name, never the password.</summary>
    public override string ToString() => AccountName;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _password.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// The account the tool is currently running as, in DOMAIN\user form, for the
    /// <c>operator_account</c> column and the run log.
    /// </summary>
    public static string CurrentWindowsAccountName()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}
