namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler on the reporting thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to the captured synchronisation context, or to the thread
/// pool when there is none — which is what the GUI wants, and what a test cannot rely on. A
/// report queued that way can still be pending when the run returns, so a test asserting on
/// something observed mid-run races the scheduler.
///
/// Reporting inline makes ordering deterministic. Handlers must stay cheap, since they run on
/// the orchestrator's own thread.
/// </remarks>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
