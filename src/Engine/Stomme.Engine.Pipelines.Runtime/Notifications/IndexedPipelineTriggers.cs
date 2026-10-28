using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Represents a collection of indexed pipeline triggers.
/// </summary>
public record IndexedPipelineTriggers(Pipeline Pipeline, ICollection<StoredTrigger> AddedTriggers, ICollection<StoredTrigger> RemovedTriggers, ICollection<StoredTrigger> UnchangedTriggers);