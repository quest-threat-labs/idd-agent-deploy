using System.Runtime.Versioning;
using System.Security.Cryptography;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Msi;

/// <summary>
/// Reads the identity of an MSI so the operator can see what they are about to deploy
/// and so the deployment history records it (PRD 5.5, 8.3).
/// </summary>
public interface IMsiInspector
{
    /// <summary>
    /// Opens the MSI read-only, reads its <c>Property</c> table, and hashes the file.
    /// </summary>
    /// <exception cref="MsiInspectionException">
    /// The file is missing, is not a readable MSI, or has no ProductVersion. PRD 8.3 blocks
    /// deployment when extraction fails, so this throws rather than returning a partial
    /// result that a caller might deploy from.
    /// </exception>
    Task<MsiPackageInfo> InspectAsync(string msiPath, CancellationToken ct);
}

/// <summary>
/// Raised when an MSI cannot be read well enough to deploy from it.
/// </summary>
/// <remarks>
/// PRD 10.4: the message names the file and the next diagnostic step, because this is
/// surfaced directly to an operator.
/// </remarks>
public sealed class MsiInspectionException : Exception
{
    public MsiInspectionException(string message) : base(message) { }

    public MsiInspectionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads MSI properties through the Windows Installer C API.
/// </summary>
/// <remarks>
/// Windows-only, as is the whole utility. See <see cref="MsiNativeMethods"/> for why this
/// uses <c>msi.dll</c> directly rather than the COM automation layer PRD 5.1 nominates.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MsiInspector : IMsiInspector
{
    /// <summary>
    /// Substrings that mark a package as the agent this tool is meant to deploy.
    /// </summary>
    /// <remarks>
    /// PRD-OPEN-Q: Q6 — the utility is named for the Identity Defense Hybrid Audit Agent
    /// while the package ships as "Quest Change Auditor Agent (x64).msi". Both spellings are
    /// accepted until PM confirms whether they are the same binary. A mismatch only warns:
    /// PRD 5.5 is explicit that the tool must not hard-block on a name string that may
    /// change between releases.
    /// </remarks>
    private static readonly string[] ExpectedProductNameFragments =
    [
        "Change Auditor",
        "Hybrid Audit Agent",
    ];

    public async Task<MsiPackageInfo> InspectAsync(string msiPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(msiPath))
        {
            throw new MsiInspectionException("No MSI file was selected.");
        }

        var fullPath = Path.GetFullPath(msiPath);
        var file = new FileInfo(fullPath);

        if (!file.Exists)
        {
            throw new MsiInspectionException(
                $"The MSI '{fullPath}' does not exist. Confirm the path is correct and that " +
                "the account running this utility can read it — a file on a UNC share may be " +
                "visible to you but not to a service account.");
        }

        // Hashing streams the file, so it is the slow part; run it off the calling thread.
        // NFR2 forbids blocking the UI thread, and the operator picks the MSI from a dialog.
        var sha256 = await ComputeSha256Async(fullPath, ct).ConfigureAwait(false);

        var properties = await Task.Run(() => ReadProperties(fullPath), ct).ConfigureAwait(false);

        properties.TryGetValue("ProductVersion", out var productVersion);
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            throw new MsiInspectionException(
                $"'{file.Name}' opened as a Windows Installer package but has no ProductVersion " +
                "property. This is not a deployable agent package — confirm you selected the " +
                "agent MSI rather than a patch, a transform, or a merge module.");
        }

        properties.TryGetValue("ProductName", out var productName);

        return new MsiPackageInfo
        {
            FilePath = fullPath,
            FileName = file.Name,
            FileSizeBytes = file.Length,
            Sha256 = sha256,
            ProductName = productName,
            ProductVersion = productVersion,
            ProductCode = properties.GetValueOrDefault("ProductCode"),
            UpgradeCode = properties.GetValueOrDefault("UpgradeCode"),
            LooksLikeExpectedProduct = LooksLikeExpectedProduct(productName),
        };
    }

    internal static bool LooksLikeExpectedProduct(string? productName) =>
        !string.IsNullOrWhiteSpace(productName) &&
        ExpectedProductNameFragments.Any(fragment =>
            productName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Reads the whole <c>Property</c> table in one pass.
    /// </summary>
    /// <remarks>
    /// PRD 5.5 gives a per-property query. Reading the table once and indexing it costs one
    /// database open instead of four, and more importantly means the MSI is held open for
    /// one short scope rather than four — the file lock is the thing worth minimising here.
    /// </remarks>
    private static Dictionary<string, string> ReadProperties(string msiPath)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var openResult = MsiNativeMethods.MsiOpenDatabase(
            msiPath,
            MsiNativeMethods.MsiDbOpenReadOnly,
            out var rawDatabase);

        if (openResult != MsiNativeMethods.ErrorSuccess)
        {
            throw new MsiInspectionException(
                $"'{Path.GetFileName(msiPath)}' could not be opened as a Windows Installer " +
                $"package (msi.dll returned {openResult}). Confirm the file is a complete, " +
                "uncorrupted MSI — a partial copy from a network share is the usual cause.");
        }

        using var database = new MsiSafeHandle(rawDatabase);

        var viewResult = MsiNativeMethods.MsiDatabaseOpenView(
            database.Value,
            "SELECT `Property`, `Value` FROM `Property`",
            out var rawView);

        if (viewResult != MsiNativeMethods.ErrorSuccess)
        {
            throw new MsiInspectionException(
                $"'{Path.GetFileName(msiPath)}' has no readable Property table " +
                $"(msi.dll returned {viewResult}). Confirm you selected an installer package " +
                "rather than a patch (.msp) or a transform (.mst).");
        }

        using var view = new MsiSafeHandle(rawView);

        var executeResult = MsiNativeMethods.MsiViewExecute(view.Value, 0);
        if (executeResult != MsiNativeMethods.ErrorSuccess)
        {
            throw new MsiInspectionException(
                $"The Property table of '{Path.GetFileName(msiPath)}' could not be queried " +
                $"(msi.dll returned {executeResult}).");
        }

        while (true)
        {
            var fetchResult = MsiNativeMethods.MsiViewFetch(view.Value, out var rawRecord);
            if (fetchResult == MsiNativeMethods.ErrorNoMoreItems)
            {
                break;
            }

            if (fetchResult != MsiNativeMethods.ErrorSuccess)
            {
                throw new MsiInspectionException(
                    $"Reading the Property table of '{Path.GetFileName(msiPath)}' failed part " +
                    $"way through (msi.dll returned {fetchResult}).");
            }

            using var record = new MsiSafeHandle(rawRecord);
            var name = ReadRecordString(record.Value, field: 1);
            var value = ReadRecordString(record.Value, field: 2);

            if (!string.IsNullOrEmpty(name))
            {
                properties[name] = value;
            }
        }

        return properties;
    }

    private static string ReadRecordString(uint record, uint field)
    {
        uint length = 0;
        var sizeResult = MsiNativeMethods.MsiRecordGetString(record, field, null, ref length);

        // A zero-length buffer is reported as ERROR_MORE_DATA with the required size; an
        // empty field returns success and leaves length at zero.
        if (sizeResult == MsiNativeMethods.ErrorSuccess && length == 0)
        {
            return string.Empty;
        }

        if (sizeResult != MsiNativeMethods.ErrorMoreData && sizeResult != MsiNativeMethods.ErrorSuccess)
        {
            throw new MsiInspectionException(
                $"A Property table field could not be sized (msi.dll returned {sizeResult}).");
        }

        // MsiRecordGetString wants room for the terminator on the second call.
        var buffer = new char[length + 1];
        var capacity = length + 1;
        var readResult = MsiNativeMethods.MsiRecordGetString(record, field, buffer, ref capacity);

        if (readResult != MsiNativeMethods.ErrorSuccess)
        {
            throw new MsiInspectionException(
                $"A Property table field could not be read (msi.dll returned {readResult}).");
        }

        return new string(buffer, 0, (int)capacity);
    }
}
