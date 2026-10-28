namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// Contains arguments to use for counting the number of pipeline instances.
/// </summary>
[Obsolete("This type is obsolete. Use the new CreateClientAsync methods of IPipelineRuntime instead.")]
public class CountRunningPipelinesRequest
{
    /// <summary>
    /// The pipeline definition ID to include in the query.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>
    /// The pipeline definition version to include in the query.
    /// </summary>
    public int? Version { get; set; }

    /// <summary>
    /// The correlation ID to include in the query. 
    /// </summary>
    public string? CorrelationId { get; set; }
}