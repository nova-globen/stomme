using Stomme.Engine.Mediator.Abstractions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Middleware.Command;

namespace Stomme.Engine.Mediator.Channels;

/// <inheritdoc cref="Stomme.Engine.Mediator.Contracts.ICommandsChannel" />
public class CommandsChannel : ChannelBase<CommandContext>, ICommandsChannel
{
}