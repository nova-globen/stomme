using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a pipeline bound to one or more triggers.
/// </summary>
public record TriggerBoundPipeline(PipelineGraph PipelineGraph, ICollection<StoredTrigger> Triggers);