namespace Stomme.Engine.Mediator.Middleware.Command;

/// <summary>
/// Represents a command middleware delegate.
/// </summary>
public delegate ValueTask CommandMiddlewareDelegate(CommandContext context);