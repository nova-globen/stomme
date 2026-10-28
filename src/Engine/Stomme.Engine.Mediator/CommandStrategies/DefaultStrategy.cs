using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Extensions;

namespace Stomme.Engine.Mediator.CommandStrategies;

/// <summary>
/// Invokes command handlers using the default strategy. 
/// </summary>
public class DefaultStrategy : ICommandStrategy
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(CommandStrategyContext context)
    {
        var commandContext = context.CommandContext;
        var command = commandContext.Command;
        var commandType = command.GetType();
        var handleMethod = commandType.GetCommandHandlerMethod();
        var handler = context.Handler;
        
        return await handler.InvokeAsync<TResult>(handleMethod, commandContext).ConfigureAwait(false);
    }
}