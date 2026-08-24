using System.Net;

namespace HybridAgentDeploy.Core.Discovery;

/// <summary>How an entry in an import file was written.</summary>
public enum HostEntryKind
{
    Fqdn,
    NetBiosName,
    IpAddress,
}

/// <summary>
/// One usable line from an import file.
/// </summary>
/// <param name="LineNumber">
/// 1-based, and carried through to the import report so an operator correcting a 60-line
/// file is told which line to look at rather than just which host failed.
/// </param>
public sealed record HostEntry(int LineNumber, string RawValue, HostEntryKind Kind);

/// <summary>
/// Parses a plain-text list of hosts (PRD 6.2).
/// </summary>
/// <remarks>
/// Pure text handling with no I/O and no DNS, which is what lets the parsing tests required
/// by PRD 15.1 run on a machine with no domain. Resolution is a separate concern behind
/// <see cref="IHostResolver"/>.
/// </remarks>
public static class HostListParser
{
    /// <summary>
    /// Parses lines into entries, dropping blanks and comments.
    /// </summary>
    /// <remarks>
    /// One host per line; blank lines and lines beginning with <c>#</c> are ignored
    /// (PRD 6.2). Duplicates within a file are collapsed case-insensitively, keeping the
    /// first occurrence, and reported separately rather than silently — a host listed twice
    /// is usually a copy-paste error the operator wants to know about.
    /// </remarks>
    public static HostListParseResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<HostEntry>();
        var duplicates = new List<HostEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;

            var line = rawLine?.Trim() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Tolerate a trailing comment on a host line; operators annotate these files.
            var commentAt = line.IndexOf('#');
            if (commentAt >= 0)
            {
                line = line[..commentAt].Trim();
                if (line.Length == 0)
                {
                    continue;
                }
            }

            var entry = new HostEntry(lineNumber, line, Classify(line));

            if (seen.Add(line))
            {
                entries.Add(entry);
            }
            else
            {
                duplicates.Add(entry);
            }
        }

        return new HostListParseResult(entries, duplicates);
    }

    public static HostListParseResult ParseText(string text) =>
        Parse((text ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')));

    /// <summary>
    /// Classifies an entry as an IP address, an FQDN, or a bare NetBIOS name.
    /// </summary>
    /// <remarks>
    /// All three are accepted (PRD 6.2). The distinction drives how the resolver turns the
    /// entry into an FQDN: an IP needs a reverse lookup, a bare name needs the DNS suffix
    /// search list applied, an FQDN needs only confirming.
    /// </remarks>
    internal static HostEntryKind Classify(string value)
    {
        if (IPAddress.TryParse(value, out _))
        {
            return HostEntryKind.IpAddress;
        }

        return value.Contains('.', StringComparison.Ordinal)
            ? HostEntryKind.Fqdn
            : HostEntryKind.NetBiosName;
    }
}

/// <summary>
/// What a parse produced: the entries to resolve, and the duplicates that were collapsed.
/// </summary>
public sealed record HostListParseResult(
    IReadOnlyList<HostEntry> Entries,
    IReadOnlyList<HostEntry> Duplicates);
