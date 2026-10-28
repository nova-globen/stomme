using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines;

public class NoopCommitStateHandler : ICommitStateHandler
{
    public Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}