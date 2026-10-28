namespace Stomme.Engine.Pipelines;

/// <summary>
/// Implement this interface or <see cref="PipelineBase"/> when implementing pipelines using code so that they become available to the system.
/// </summary>
public interface IPipeline
{
    /// <summary>
    /// Invokes the specified <see cref="IPipelineBuilder"/>.
    /// </summary>
    ValueTask BuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default);
}