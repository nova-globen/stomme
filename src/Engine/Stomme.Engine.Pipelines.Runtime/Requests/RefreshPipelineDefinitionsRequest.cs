namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// Represents a request to refresh pipeline definitions by re-indexing their triggers.
/// </summary>
public class RefreshPipelineDefinitionsRequest
{
    /// <summary>
    /// Initializes a new instance of <see cref="RefreshPipelineDefinitionsRequest"/>.
    /// </summary>
    public RefreshPipelineDefinitionsRequest()
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RefreshPipelineDefinitionsRequest"/>.
    /// </summary>
    public RefreshPipelineDefinitionsRequest(IEnumerable<string>? definitionIds, int batchSize)
    {
        BatchSize = batchSize;
        DefinitionIds = definitionIds?.ToList();
    }
    
    /// <summary>
    /// Gets or sets the batch size for refreshing pipeline definitions.
    /// The batch size determines the number of definitions to be processed at a time during the refresh process.
    /// </summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the collection of definition IDs for pipeline definitions to be refreshed.
    /// If not specified, all pipelines will be refreshed.
    /// </summary>
    public ICollection<string>? DefinitionIds { get; set; }
}