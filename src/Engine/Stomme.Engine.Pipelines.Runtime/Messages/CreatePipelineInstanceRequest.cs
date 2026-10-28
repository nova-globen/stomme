using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Messages;

/// <summary>
/// A request to create a new pipeline instance.
/// </summary>
[UsedImplicitly]
public class CreatePipelineInstanceRequest
{
    /// <summary>
    /// The ID of the pipeline definition version to create an instance of.
    /// </summary>
    public PipelineDefinitionHandle PipelineDefinitionHandle { get; set; } = null!;

    /// <summary>
    /// The correlation ID of the pipeline, if any.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The name of the pipeline instance to be created.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The input to the pipeline instance, if any.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Any properties to assign to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// The ID of the parent pipeline instance, if any.
    /// </summary>
    public string? ParentId { get; set; }
}