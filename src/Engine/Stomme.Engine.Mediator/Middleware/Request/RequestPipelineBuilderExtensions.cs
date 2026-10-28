using Stomme.Engine.Mediator.Middleware.Request.Components;
using Stomme.Engine.Mediator.Middleware.Request.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Request;

/// <summary>
/// Provides a set of static methods for <see cref="IRequestPipelineBuilder"/>.
/// </summary>
public static class RequestPipelineBuilderExtensions
{
    /// <summary>
    /// Adds a request handler middleware to the pipeline.
    /// </summary>
    /// <param name="builder">The request pipeline builder.</param>
    public static IRequestPipelineBuilder UseRequestHandlers(this IRequestPipelineBuilder builder) => builder.UseMiddleware<RequestHandlerInvokerMiddleware>();
    
    /// <summary>
    /// Adds a request logging middleware to the pipeline.
    /// </summary>
    /// <param name="builder">The request pipeline builder.</param>
    public static IRequestPipelineBuilder UseRequestLogging(this IRequestPipelineBuilder builder) => builder.UseMiddleware<RequestLoggingMiddleware>();
}