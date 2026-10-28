using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

/// <summary>
/// A command to dispatch a pipeline instance.
/// </summary>
[PublicAPI]
public class DispatchPipelineInstanceCommand(string instanceId) : ICommand<Unit>
{
    public string InstanceId { get; init; } = instanceId;
    public string? BookmarkId { get; set; }
    public StepHandle? StepHandle { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public string? CorrelationId { get; set; }
}