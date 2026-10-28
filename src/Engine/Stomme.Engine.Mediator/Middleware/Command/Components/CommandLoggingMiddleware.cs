using Stomme.Engine.Mediator.Middleware.Command.Contracts;
using Stomme.Engine.Mediator.Models;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Mediator.Middleware.Command.Components;

/// <summary>
/// A command middleware that logs the command being invoked.
/// </summary>
[UsedImplicitly]
public class CommandLoggingMiddleware(CommandMiddlewareDelegate next, ILogger<CommandLoggingMiddleware> logger) : ICommandMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(CommandContext context)
    {
        var commandType = context.Command.GetType();
        logger.LogInformation("Invoking {CommandName}", commandType.Name);
        
        await next(context).ConfigureAwait(false);
        
        if (context.CommandResult is null or Unit)
            logger.LogInformation("{CommandName} completed with no result", commandType.Name);
        else
            logger.LogInformation("{CommandName} completed with result {CommandResult}", commandType.Name, context.CommandResult);
    }
}