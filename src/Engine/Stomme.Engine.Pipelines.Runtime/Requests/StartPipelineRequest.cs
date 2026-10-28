using System.Collections;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime;

public class StartPipelineRequest
{
    /// <summary>
    /// The ID of the pipeline definition version to create an instance of.
    /// Will be used if <see cref="Pipeline"/> is not set.
    /// </summary>
    public PipelineDefinitionHandle? PipelineDefinitionHandle { get; set; }

    /// <summary>
    /// The pipeline to create an instance of.
    /// Will be used if <see cref="PipelineDefinitionHandle"/> is not set.
    /// </summary>
    public Pipeline? Pipeline { get; set; }

    /// <summary>
    /// The correlation ID of the pipeline, if any.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The name to use when starting a new pipeline instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The input to the pipeline instance, if any.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Any variables to set before starting the pipeline.
    /// </summary>
    public IDictionary<string, object>? Variables { get; set; }

    /// <summary>
    /// Any properties to assign to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// The ID of the parent pipeline instance, if any.
    /// </summary>
    public string? ParentId { get; set; }
    
    /// <summary>
    /// The ID of the step that triggered the pipeline instance, if any.
    /// </summary>
    public string? TriggerStepId { get; set; }
    
    /// <summary>
    /// The handle of the step to schedule, if any.
    /// </summary>
    public StepHandle? StepHandle { get; set; }
}