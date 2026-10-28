using System.ComponentModel;
using Stomme.Engine.Common;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Implements a periodic pipeline commit strategy based on a specified time interval.
/// This strategy determines if a pipeline should commit by comparing the elapsed time
/// since the last commit with the configured interval.
/// </summary>
[DisplayName("Commit Periodically")]
[Description("Determines whether a pipeline state should be committed based on a specified time interval.")]
public class PeriodicPipelineStrategy(TimeSpan interval) : IPipelineCommitStrategy
{
    private static readonly object LastCommitPropertyKey = new();
    public TimeSpan Interval { get; } = interval;

    public CommitAction ShouldCommit(PipelineCommitStateStrategyContext context)
    {
        var lastCommit = context.PipelineExecutionContext.TransientProperties.TryGetValue(LastCommitPropertyKey, out var value) ? (DateTimeOffset?)value : null;
        var now = context.PipelineExecutionContext.GetRequiredService<ISystemClock>().UtcNow;
        var shouldCommit = lastCommit == null || (now - lastCommit.Value).TotalMilliseconds > Interval.TotalMilliseconds;

        if (shouldCommit)
            context.PipelineExecutionContext.TransientProperties[LastCommitPropertyKey] = now;

        return shouldCommit ? CommitAction.Commit : CommitAction.Default;
    }
}