namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a source of pipeline definitions.
/// </summary>
public interface IPipelinesProvider
{
    /// <summary>
    /// Gets the name of the provider.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Gets the pipeline definitions.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pipeline definitions.</returns>
    ValueTask<IEnumerable<MaterializedPipeline>> GetPipelinesAsync(CancellationToken cancellationToken = default);
}