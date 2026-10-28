using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public interface IStepTestRunner
{
    Task<StepExecutionContext> RunAsync(PipelineGraph pipelineGraph, IStep step, CancellationToken cancellationToken = default);
}