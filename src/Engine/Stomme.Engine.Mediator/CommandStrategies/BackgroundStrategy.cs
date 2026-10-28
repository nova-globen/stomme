using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Mediator.CommandStrategies;

/// <summary>
/// Invokes command handlers in the background and does not wait for the result.
/// </summary>
public class BackgroundStrategy : ICommandStrategy
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(CommandStrategyContext context)
    {
        var commandsChannel = context.ServiceProvider.GetRequiredService<ICommandsChannel>();
        await commandsChannel.Writer.WriteAsync(context.CommandContext, context.CancellationToken).ConfigureAwait(false);
        return default!;
    }
}