using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Conversion between the enums in <see cref="Models"/> and the TEXT values stored in
/// SQLite.
/// </summary>
/// <remarks>
/// Written out explicitly rather than using <c>Enum.Parse</c> so that renaming a C# enum
/// member is a compile-time event here instead of a silent change to the on-disk format
/// that older rows no longer match. The stored spellings are fixed by PRD 6, 10.2 and 10.3.
/// </remarks>
internal static class DbEnums
{
    public static string ToDb(DeploymentOutcome value) => value switch
    {
        DeploymentOutcome.Success => "Success",
        DeploymentOutcome.SuccessRebootRequired => "SuccessRebootRequired",
        DeploymentOutcome.Failure => "Failure",
        DeploymentOutcome.Timeout => "Timeout",
        DeploymentOutcome.Cancelled => "Cancelled",
        DeploymentOutcome.Skipped => "Skipped",
        _ => throw UnmappedValue(value),
    };

    public static DeploymentOutcome ToOutcome(string value) => value switch
    {
        "Success" => DeploymentOutcome.Success,
        "SuccessRebootRequired" => DeploymentOutcome.SuccessRebootRequired,
        "Failure" => DeploymentOutcome.Failure,
        "Timeout" => DeploymentOutcome.Timeout,
        "Cancelled" => DeploymentOutcome.Cancelled,
        "Skipped" => DeploymentOutcome.Skipped,
        _ => throw UnreadableValue(nameof(DeploymentOutcome), value),
    };

    public static string ToDb(ErrorCategory value) => value switch
    {
        ErrorCategory.Authentication => "Authentication",
        ErrorCategory.Connectivity => "Connectivity",
        ErrorCategory.Staging => "Staging",
        ErrorCategory.InstallFailure => "InstallFailure",
        ErrorCategory.Contended => "Contended",
        ErrorCategory.Policy => "Policy",
        ErrorCategory.AlreadyInstalled => "AlreadyInstalled",
        ErrorCategory.Timeout => "Timeout",
        ErrorCategory.Internal => "Internal",
        _ => throw UnmappedValue(value),
    };

    public static ErrorCategory ToErrorCategory(string value) => value switch
    {
        "Authentication" => ErrorCategory.Authentication,
        "Connectivity" => ErrorCategory.Connectivity,
        "Staging" => ErrorCategory.Staging,
        "InstallFailure" => ErrorCategory.InstallFailure,
        "Contended" => ErrorCategory.Contended,
        "Policy" => ErrorCategory.Policy,
        "AlreadyInstalled" => ErrorCategory.AlreadyInstalled,
        "Timeout" => ErrorCategory.Timeout,
        "Internal" => ErrorCategory.Internal,
        _ => throw UnreadableValue(nameof(ErrorCategory), value),
    };

    public static string ToDb(DeploymentStage value) => value switch
    {
        DeploymentStage.Preflight => "preflight",
        DeploymentStage.Stage => "stage",
        DeploymentStage.Execute => "execute",
        DeploymentStage.Retrieve => "retrieve",
        DeploymentStage.Cleanup => "cleanup",
        _ => throw UnmappedValue(value),
    };

    public static DeploymentStage ToStage(string value) => value switch
    {
        "preflight" => DeploymentStage.Preflight,
        "stage" => DeploymentStage.Stage,
        "execute" => DeploymentStage.Execute,
        "retrieve" => DeploymentStage.Retrieve,
        "cleanup" => DeploymentStage.Cleanup,
        _ => throw UnreadableValue(nameof(DeploymentStage), value),
    };

    public static string ToDb(DiscoverySource value) => value switch
    {
        DiscoverySource.AdEnumeration => "ad_enumeration",
        DiscoverySource.FileImport => "file_import",
        DiscoverySource.Manual => "manual",
        _ => throw UnmappedValue(value),
    };

    public static DiscoverySource ToSource(string value) => value switch
    {
        "ad_enumeration" => DiscoverySource.AdEnumeration,
        "file_import" => DiscoverySource.FileImport,
        "manual" => DiscoverySource.Manual,
        _ => throw UnreadableValue(nameof(DiscoverySource), value),
    };

    private static ArgumentOutOfRangeException UnmappedValue<T>(T value) where T : struct, Enum =>
        new(nameof(value), value, $"No database representation is defined for {typeof(T).Name}.{value}.");

    private static InvalidDataException UnreadableValue(string enumName, string value) =>
        new($"The inventory database contains '{value}', which is not a recognised {enumName}. " +
            "The database may have been written by a newer version of this utility.");
}
