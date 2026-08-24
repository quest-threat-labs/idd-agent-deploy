using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// Every remote operation this tool performs, behind one interface.
/// </summary>
/// <remarks>
/// <para>
/// HARD RULE (PRD 5.3, CLAUDE.md): no direct SMB, WinRM, or remoting call exists anywhere
/// else in the codebase. This is what lets the deployment orchestrator, the concurrency
/// guard, the retry logic, and the exit-code handling be developed and tested without a
/// live Active Directory forest.
/// </para>
/// <para>
/// Two implementations exist. <c>WinRmSmbTransport</c> (Phase 3) is the production path:
/// SMB for staging and retrieval, PowerShell Remoting for execution.
/// <c>SimulatedTransport</c> lives in the test-support assembly and drives the orchestrator
/// through every outcome in PRD 10.2 without touching a network.
/// </para>
/// <para>
/// Note there is no credential parameter. Credentials are supplied to a transport when it
/// is constructed and live for the duration of one run (SEC1), which keeps them out of
/// every call site and out of anything that might be logged.
/// </para>
/// </remarks>
public interface ITargetTransport
{
    /// <summary>
    /// Confirms a target is reachable before anything is copied to it (PRD 5.4 step 1).
    /// </summary>
    /// <remarks>
    /// Checked per target as it is reached rather than for all targets up front: probing 60
    /// DCs serially before starting would be slow, and the result can change by the time
    /// the target is reached (PRD 11).
    /// </remarks>
    Task<PreflightResult> PreflightAsync(
        string targetHost,
        CancellationToken ct);

    /// <summary>
    /// Copies a file to a directory on the target and verifies it arrived intact
    /// (PRD 5.4 step 2).
    /// </summary>
    /// <remarks>
    /// Implementations MUST compute the SHA-256 of the staged copy and compare it against
    /// the source before reporting success (SEC6). A file that changed in transit must never
    /// be installed on a domain controller.
    /// </remarks>
    Task<StagingResult> StageFileAsync(
        string targetHost,
        string localPath,
        string remoteDirectory,
        CancellationToken ct);

    /// <summary>
    /// Runs a command on the target and returns its process exit code (PRD 5.4 step 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command line always references a path local to the target. Never
    /// <c>msiexec /i \\unc\path</c> inside a remoting session: the local-staging design
    /// exists to avoid the Kerberos double-hop problem and is the single most important
    /// decision in this tool (PRD 5.4). Do not "simplify" it into a UNC execution.
    /// </para>
    /// <para>
    /// <see cref="ExecutionResult.ExitCode"/> is the exit code of the executed process, not
    /// a proxy for the remoting call having succeeded. A successful remoting call that ran a
    /// failing msiexec is a failed deployment.
    /// </para>
    /// </remarks>
    Task<ExecutionResult> ExecuteAsync(
        string targetHost,
        string commandLine,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Copies a file back from the target (PRD 5.4 step 4).
    /// </summary>
    /// <remarks>
    /// Called whether the install succeeded or failed. The msiexec verbose log is most
    /// valuable on failure, which is exactly when it is most tempting to skip retrieving it.
    /// </remarks>
    Task<RetrievalResult> RetrieveFileAsync(
        string targetHost,
        string remotePath,
        string localDestination,
        CancellationToken ct);

    /// <summary>
    /// Removes the staging directory from the target (PRD 5.4 step 5, SEC8).
    /// </summary>
    /// <remarks>
    /// A cleanup failure is logged as a warning and does not fail the deployment: the agent
    /// is installed either way, and failing a successful install because a temp directory
    /// survived would misreport what happened.
    /// </remarks>
    Task CleanupAsync(
        string targetHost,
        string remoteDirectory,
        CancellationToken ct);
}

/// <summary>
/// Outcome of a per-target reachability check.
/// </summary>
/// <param name="Succeeded">True when the target is ready to be staged to.</param>
/// <param name="ErrorCategory">Set when <paramref name="Succeeded"/> is false.</param>
/// <param name="ErrorDetail">
/// PRD 10.4: names the DC and the next diagnostic step, not just what failed.
/// </param>
public sealed record PreflightResult(
    bool Succeeded,
    ErrorCategory? ErrorCategory,
    string? ErrorDetail)
{
    public static PreflightResult Success() => new(true, null, null);

    public static PreflightResult Failed(ErrorCategory category, string detail) =>
        new(false, category, detail);
}

/// <summary>
/// Outcome of staging a file on a target.
/// </summary>
/// <param name="StagedPath">
/// The path on the target, used to build the msiexec command line and to clean up later.
/// </param>
/// <param name="VerifiedSha256">
/// The hash of the staged copy as read back from the target, so the caller can record what
/// was actually verified rather than trusting that verification happened (SEC6).
/// </param>
public sealed record StagingResult(
    bool Succeeded,
    string? StagedPath,
    string? VerifiedSha256,
    ErrorCategory? ErrorCategory,
    string? ErrorDetail)
{
    public static StagingResult Success(string stagedPath, string verifiedSha256) =>
        new(true, stagedPath, verifiedSha256, null, null);

    public static StagingResult Failed(ErrorCategory category, string detail) =>
        new(false, null, null, category, detail);
}

/// <summary>
/// Outcome of executing a command on a target.
/// </summary>
/// <param name="ExitCode">
/// The process exit code, mapped by the exit-code table in PRD 10.1. Null only when the
/// command could not be launched at all, which is distinct from a command that ran and
/// failed.
/// </param>
/// <param name="TimedOut">
/// True when the per-target timeout elapsed (PRD R7.5). Cleanup is still attempted.
/// </param>
public sealed record ExecutionResult(
    bool Launched,
    int? ExitCode,
    bool TimedOut,
    string? StandardOutput,
    string? StandardError,
    ErrorCategory? ErrorCategory,
    string? ErrorDetail)
{
    public static ExecutionResult Completed(int exitCode, string? stdout = null, string? stderr = null) =>
        new(true, exitCode, false, stdout, stderr, null, null);

    public static ExecutionResult Timeout(string detail) =>
        new(true, null, true, null, null, Models.ErrorCategory.Timeout, detail);

    public static ExecutionResult FailedToLaunch(ErrorCategory category, string detail) =>
        new(false, null, false, null, null, category, detail);
}

/// <summary>
/// Outcome of retrieving a file from a target.
/// </summary>
/// <param name="LocalPath">Where the file was written locally, when retrieval succeeded.</param>
public sealed record RetrievalResult(
    bool Succeeded,
    string? LocalPath,
    ErrorCategory? ErrorCategory,
    string? ErrorDetail)
{
    public static RetrievalResult Success(string localPath) => new(true, localPath, null, null);

    public static RetrievalResult Failed(ErrorCategory category, string detail) =>
        new(false, null, category, detail);
}
