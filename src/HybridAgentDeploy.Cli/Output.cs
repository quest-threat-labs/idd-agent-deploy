using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HybridAgentDeploy.Cli;

/// <summary>Output shape for commands that support <c>--format</c> (PRD 9).</summary>
internal enum OutputFormat
{
    Table,
    Csv,
    Json,
}

/// <summary>
/// Writes command output, keeping the two streams strictly separated.
/// </summary>
/// <remarks>
/// <para>
/// PRD 9: all output to stdout, diagnostics to stderr, and <c>--format json</c> output must be
/// machine-parseable with no interleaved human text. That is the whole reason this type
/// exists rather than calls to <c>Console.WriteLine</c> scattered through the commands — one
/// stray progress message on stdout breaks every script parsing the output, and it breaks it
/// intermittently, only on the runs that had something to report.
/// </para>
/// <para>
/// <see cref="Diagnostic"/> and <see cref="Warning"/> always go to stderr regardless of
/// format, so a scripted caller can watch progress on one stream and parse the other.
/// </para>
/// </remarks>
internal sealed class Output
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    public Output(TextWriter stdout, TextWriter stderr)
    {
        _stdout = stdout;
        _stderr = stderr;
    }

    public static Output Console() => new(System.Console.Out, System.Console.Error);

    /// <summary>Serialiser settings shared by every JSON document the CLI emits.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Writes a line of primary output to stdout.</summary>
    public void Line(string text = "") => _stdout.WriteLine(text);

    /// <summary>
    /// Writes progress or explanatory text to stderr.
    /// </summary>
    /// <remarks>
    /// Never stdout, even in table mode. Keeping the rule unconditional means there is no
    /// format-dependent branch anywhere that could put human text in a JSON document by
    /// accident.
    /// </remarks>
    public void Diagnostic(string text) => _stderr.WriteLine(text);

    public void Warning(string text) => _stderr.WriteLine($"warning: {text}");

    public void Error(string text) => _stderr.WriteLine($"error: {text}");

    /// <summary>Writes an object as the command's JSON result document.</summary>
    public void Json<T>(T value) => _stdout.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>
    /// Writes rows as an aligned table.
    /// </summary>
    /// <remarks>
    /// Column widths are measured across the whole result rather than guessed, because an
    /// operator scanning sixty domain controllers needs the columns to line up. NFR3 allows a
    /// second for 500 rows, which this comfortably fits.
    /// </remarks>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count && i < row.Count; i++)
            {
                widths[i] = Math.Max(widths[i], (row[i] ?? string.Empty).Length);
            }
        }

        Line(FormatRow(headers.Select(h => (string?)h).ToList(), widths));
        Line(FormatRow(widths.Select(w => (string?)new string('-', w)).ToList(), widths));

        foreach (var row in rows)
        {
            Line(FormatRow(row, widths));
        }
    }

    /// <summary>Writes rows as RFC 4180 CSV.</summary>
    public void Csv(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        Line(string.Join(',', headers.Select(CsvField)));

        foreach (var row in rows)
        {
            Line(string.Join(',', row.Select(CsvField)));
        }
    }

    private static string FormatRow(IReadOnlyList<string?> cells, int[] widths)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("  ");
            }

            var cell = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;

            // The final column is not padded, so lines have no trailing whitespace to confuse
            // anything downstream that trims or diffs the output.
            builder.Append(i == widths.Length - 1 ? cell : cell.PadRight(widths[i]));
        }

        return builder.ToString().TrimEnd();
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>Formats a stored UTC timestamp for a table or CSV cell.</summary>
    public static string? Timestamp(string? storedUtc) =>
        string.IsNullOrEmpty(storedUtc)
            ? null
            : Core.UtcTimestamp.Parse(storedUtc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
