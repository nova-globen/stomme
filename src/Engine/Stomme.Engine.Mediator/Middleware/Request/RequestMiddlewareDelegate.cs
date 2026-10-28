namespace Stomme.Engine.Mediator.Middleware.Request;

/// <summary>
/// Represents a delegate for a request middleware.
/// </summary>
public delegate ValueTask RequestMiddlewareDelegate(RequestContext context);