using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace HybridAgentDeploy.Core.Msi;

/// <summary>
/// The subset of <c>msi.dll</c> needed to read an MSI's <c>Property</c> table.
/// </summary>
/// <remarks>
/// <para>
/// PRD 5.1 nominates late-bound <c>WindowsInstaller</c> COM. This uses the underlying
/// Windows Installer C API instead, and the reason is the hazard PRD 5.5 itself flags: a
/// leaked <c>Installer</c> object holds a file lock on the MSI, so the operator cannot move
/// or replace the file until the process exits. With COM, releasing correctly is a matter
/// of discipline at every call site. With a <see cref="MsiSafeHandle"/> the release is a
/// property of the handle's lifetime and the runtime enforces it, including on the
/// exception paths. Same API underneath, one less thing to get right by hand.
/// </para>
/// <para>
/// Classic <c>DllImport</c> rather than source-generated <c>LibraryImport</c>: the latter
/// requires <c>AllowUnsafeBlocks</c> across the whole assembly, and enabling unsafe code in
/// the library that drives deployments to domain controllers is a poor trade for marshalling
/// this simple. Every parameter here is a <see cref="uint"/>, a string, or a char buffer.
/// </para>
/// <para>
/// MSIHANDLE is a 32-bit unsigned integer on every architecture, x64 included. It must NOT
/// be marshalled as a pointer-sized value or as <c>out SafeHandle</c>, either of which would
/// write eight bytes into a four-byte location and corrupt the stack. Every signature here
/// takes and returns <see cref="uint"/>; handles are wrapped immediately on return.
/// </para>
/// <para>
/// <c>DefaultDllImportSearchPaths(System32)</c> is load-bearing, not decoration. NFR4 allows
/// this tool to be run from a UNC path or removable media, and without it a <c>msi.dll</c>
/// planted beside the executable on that share would be loaded in preference to the real
/// one — into a process that holds domain administrator rights.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MsiNativeMethods
{
    private const string MsiDll = "msi.dll";

    internal const uint ErrorSuccess = 0;
    internal const uint ErrorMoreData = 234;
    internal const uint ErrorNoMoreItems = 259;

    /// <summary>
    /// MSIDBOPEN_READONLY. Documented as the literal pointer value 0, not a string.
    /// Read-only is not merely a preference: opening for update would let a defect in this
    /// tool modify the operator's installer package.
    /// </summary>
    internal static readonly IntPtr MsiDbOpenReadOnly = IntPtr.Zero;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MsiDatabaseOpenView(uint hDatabase, string szQuery, out uint phView);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiViewExecute", ExactSpelling = true)]
    internal static extern uint MsiViewExecute(uint hView, uint hRecord);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiViewFetch", ExactSpelling = true)]
    internal static extern uint MsiViewFetch(uint hView, out uint phRecord);

    /// <summary>
    /// Two-call pattern: pass a null buffer with a zero size to learn the required length,
    /// then call again with a buffer of that length plus one for the terminator.
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MsiRecordGetString(
        uint hRecord,
        uint iField,
        [Out] char[]? szValueBuf,
        ref uint pcchValueBuf);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(MsiDll, EntryPoint = "MsiCloseHandle", ExactSpelling = true)]
    internal static extern uint MsiCloseHandle(uint hAny);
}

/// <summary>
/// Owns an MSIHANDLE and closes it exactly once, including when an exception unwinds past
/// the caller.
/// </summary>
/// <remarks>
/// This is what keeps the file lock on the MSI from outliving the read. See the remarks on
/// <see cref="MsiNativeMethods"/> for why a handle-owning type is used here in place of the
/// COM approach PRD 5.1 nominates.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MsiSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MsiSafeHandle(uint handle)
        : base(ownsHandle: true)
    {
        SetHandle((IntPtr)handle);
    }

    /// <summary>The raw MSIHANDLE, for passing to the native API.</summary>
    public uint Value => (uint)(long)handle;

    protected override bool ReleaseHandle() =>
        MsiNativeMethods.MsiCloseHandle((uint)(long)handle) == MsiNativeMethods.ErrorSuccess;
}
