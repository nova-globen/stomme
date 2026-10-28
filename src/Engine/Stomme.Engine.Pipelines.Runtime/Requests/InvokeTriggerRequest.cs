using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

public class InvokeTriggerRequest
{
    public Pipeline Pipeline { get; set; } = null!;
    public string StepId { get; set; } = null!;
    public string? CorrelationId { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Variables { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public string? ParentPipelineInstanceId { get; set; }
    
}