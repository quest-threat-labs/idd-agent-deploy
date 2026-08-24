namespace HybridAgentDeploy.Core;

/// <summary>
/// The single definition of how an instant is written to and read from the inventory
/// database and the run logs.
/// </summary>
/// <remarks>
/// PRD 6 stores every timestamp as SQLite TEXT, and the <c>dc_last_deployment</c> view
/// orders on <c>completed_utc</c>. Ordering a TEXT column is lexicographic, so the format
/// must be fixed-width and zero-padded or the view silently returns the wrong row. This
/// format is round-trippable ISO 8601 with an explicit Z, which satisfies that.
///
/// CLAUDE.md: UTC in storage and logs; local time only in the UI, and labelled when shown.
/// </remarks>
public static class UtcTimestamp
{
    /// <summary>Fixed-width so that lexicographic order equals chronological order.</summary>
    public const string FormatString = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string Now() => Format(DateTimeOffset.UtcNow);

    public static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(FormatString, System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(
            text, FormatString,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseOrNull(string? text) =>
        string.IsNullOrEmpty(text) ? null : Parse(text);

    /// <summary>
    /// Renders a stored UTC timestamp for display alongside local time. CLAUDE.md requires
    /// local time to be labelled wherever it is shown.
    /// </summary>
    public static string ToDisplayString(string storedUtc)
    {
        var instant = Parse(storedUtc);
        return $"{instant:yyyy-MM-dd HH:mm:ss} UTC ({instant.ToLocalTime():yyyy-MM-dd HH:mm:ss} local)";
    }
}
