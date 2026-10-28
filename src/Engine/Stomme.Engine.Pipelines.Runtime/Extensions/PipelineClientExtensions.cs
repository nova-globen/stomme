using Stomme.Engine.Pipelines.Runtime.Messages;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Provides extension methods for the <see cref="IPipelineClient"/> interface.
/// </summary>
public static class PipelineClientExtensions
{
    /// <summary>
    /// Executes the pipeline instance and waits for it to complete or reach a suspend point.
    /// </summary>
    public static Task<RunPipelineInstanceResponse> RunInstanceAsync(this IPipelineClient pipelineClient, CancellationToken cancellationToken = default)
    {
        return pipelineClient.RunInstanceAsync(RunPipelineInstanceRequest.Empty, cancellationToken);
    }
}