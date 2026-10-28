using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

/// <summary>
/// Sends a command to cancel pipelines.
/// </summary>
public class CancelPipelinesCommand : ICommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CancelPipelinesCommand"/> class.
    /// </summary>
    public CancelPipelinesCommand()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CancelPipelinesCommand"/> class.
    /// </summary>
    public CancelPipelinesCommand(DispatchCancelPipelineRequest request)
    {
        Request = request;
    }
    
    /// <summary>
    /// Gets or sets the request.
    /// </summary>
    public DispatchCancelPipelineRequest Request { get; set; } = default!;
}