using Stomme.Engine.Common.Models;

namespace Stomme.Engine.Pipelines.Runtime.Parameters;

/// <summary>
/// Represents parameters for starting a pipeline.
/// </summary>
[Obsolete("This type is obsolete. Use the new CreateClientAsync methods of IPipelineRuntime instead.")]
public class StartPipelineRuntimeParams
{
    public string? CorrelationId { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public VersionOptions VersionOptions { get; set; }
    public string? TriggerStepId { get; set; }
    public string? InstanceId { get; set; }
    public bool TenantAgnostic { get; set; }
    public CancellationToken CancellationToken { get; set; }    
    public string? ParentPipelineInstanceId { get; set; }
}