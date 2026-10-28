namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Reloads all pipelines by invoking the populator.
/// </summary>
public interface IPipelineDefinitionsReloader
{
    /// <summary>
    /// Reloads all pipelines by invoking the populator.
    /// </summary>
    Task ReloadPipelineDefinitionsAsync(CancellationToken cancellationToken = default);
}