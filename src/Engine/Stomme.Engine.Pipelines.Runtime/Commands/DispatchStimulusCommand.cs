using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

public record DispatchStimulusCommand(DispatchStimulusRequest Request) : ICommand<Unit>;