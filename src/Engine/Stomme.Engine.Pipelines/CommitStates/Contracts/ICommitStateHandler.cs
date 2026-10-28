using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.CommitStates;

public interface ICommitStateHandler
{
    Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default);
    Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState pipelineState, CancellationToken cancellationToken = default);
}