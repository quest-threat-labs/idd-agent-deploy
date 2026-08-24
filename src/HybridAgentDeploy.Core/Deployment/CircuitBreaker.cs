using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Halts a run when consecutive failures indicate an operator-side problem rather than a
/// per-DC one (PRD R7.3).
/// </summary>
/// <remarks>
/// <para>
/// Trips after <see cref="Threshold"/> consecutive operations fail with
/// <see cref="ErrorCategory.Authentication"/>, <see cref="ErrorCategory.Connectivity"/>, or
/// <see cref="ErrorCategory.Staging"/>. Those three almost always mean wrong credentials, a
/// firewall, or an unreachable subnet — attempting the remaining forty DCs generates noise,
/// wastes time, and in the authentication case risks locking out the operator's account.
/// </para>
/// <para>
/// <see cref="ErrorCategory.InstallFailure"/> deliberately does not trip it. A non-zero
/// msiexec exit code is a genuinely per-host condition, and R7.4 requires the run to continue
/// through those.
/// </para>
/// <para>
/// Two readings of R7.3 were possible and the resolution is recorded here. "The first 3
/// consecutive deployment operations" is read as <em>any</em> three consecutive, not only the
/// opening three: the stated rationale is account lockout and operator-side faults, and three
/// consecutive authentication failures at operation eighteen carry exactly the same risk as
/// at operation one. "Consecutive" is counted in the order operations <em>complete</em>,
/// since with up to five in flight there is no other well-defined ordering, and any
/// non-qualifying outcome resets the count.
/// </para>
/// <para>
/// Instances are shared across concurrent operations, so every member is guarded.
/// </para>
/// </remarks>
public sealed class CircuitBreaker
{
    /// <summary>Consecutive qualifying failures that halt a run (PRD R7.3).</summary>
    public const int Threshold = 3;

    private readonly Lock _gate = new();
    private readonly List<string> _consecutiveFailureDetails = [];
    private int _consecutiveFailures;
    private string? _haltReason;

    /// <summary>True once the breaker has tripped. Never resets within a run.</summary>
    public bool IsTripped
    {
        get
        {
            lock (_gate)
            {
                return _haltReason is not null;
            }
        }
    }

    /// <summary>
    /// Why the run was halted, phrased for the operator (PRD 10.4, R7.3). Null until tripped.
    /// </summary>
    public string? HaltReason
    {
        get
        {
            lock (_gate)
            {
                return _haltReason;
            }
        }
    }

    /// <summary>
    /// Records a completed operation and reports whether the run must now halt.
    /// </summary>
    /// <param name="targetFqdn">The DC whose operation completed.</param>
    /// <param name="category">
    /// The failure category, or null when the operation did not fail in a way that counts.
    /// A success, a timeout, or an install failure all reset the consecutive count.
    /// </param>
    /// <returns>True when this operation tripped the breaker.</returns>
    public bool RecordOutcome(string targetFqdn, ErrorCategory? category)
    {
        lock (_gate)
        {
            if (_haltReason is not null)
            {
                return false;
            }

            if (category is null || !category.Value.TripsCircuitBreaker())
            {
                _consecutiveFailures = 0;
                _consecutiveFailureDetails.Clear();
                return false;
            }

            _consecutiveFailures++;
            _consecutiveFailureDetails.Add($"{targetFqdn} ({category.Value})");

            if (_consecutiveFailures < Threshold)
            {
                return false;
            }

            _haltReason =
                $"Halted after {Threshold} consecutive failures of the same kind: " +
                $"{string.Join(", ", _consecutiveFailureDetails)}. " +
                "Authentication, connectivity, and staging failures almost always indicate a " +
                "problem affecting every target — wrong credentials, a firewall, or an " +
                "unreachable subnet — rather than a problem with these particular domain " +
                "controllers. No further targets were started. Resolve the underlying cause and " +
                "re-run against the remaining targets; if the failures were authentication, " +
                "check the account is not locked out before retrying.";

            return true;
        }
    }
}
