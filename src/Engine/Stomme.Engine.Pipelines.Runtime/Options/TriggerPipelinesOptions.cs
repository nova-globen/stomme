namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Options for triggering pipelines.
/// </summary>
[Obsolete("This type is obsolete. Use the new CreateClientAsync methods of IPipelineRuntime instead.")]
public class TriggerPipelinesOptions
{
    public string? CorrelationId { get; set; }
    public string? PipelineInstanceId { get; set; }
    public string? StepInstanceId { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public bool TenantAgnostic { get; set; }
    public CancellationToken CancellationToken { get; set; }
}