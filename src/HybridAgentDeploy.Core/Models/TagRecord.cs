namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// A named, reusable grouping of domain controllers (PRD G3, 6).
/// </summary>
/// <remarks>
/// Tags are many-to-many with DCs and persist across sessions, so the same subset — a
/// pilot group, a site, the read-only DCs — can be redeployed to weeks later. Deleting a
/// tag removes associations only; it never deletes DC rows or deployment history (PRD 8.2).
/// </remarks>
public sealed class TagRecord
{
    public long Id { get; set; }

    /// <summary>Unique, case-insensitive.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string CreatedUtc { get; set; } = UtcTimestamp.Now();
}
