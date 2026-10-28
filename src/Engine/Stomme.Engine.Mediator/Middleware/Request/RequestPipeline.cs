using Stomme.Engine.Mediator.Middleware.Request.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Request;

/// <inheritdoc />
public class RequestPipeline : IRequestPipeline
{
    private readonly IServiceProvider _serviceProvider;
    private RequestMiddlewareDelegate? _executionPipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestPipeline"/> class.
    /// </summary>
    public RequestPipeline(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <inheritdoc />
    public RequestMiddlewareDelegate Pipeline => _executionPipeline ??= CreateDefaultPipeline();

    /// <inheritdoc />
    public RequestMiddlewareDelegate Setup(Action<IRequestPipelineBuilder>? setup = default)
    {
        var builder = new RequestPipelineBuilder(_serviceProvider);
        setup?.Invoke(builder);
        _executionPipeline = builder.Build();
        return _executionPipeline;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(RequestContext context) => await Pipeline(context).ConfigureAwait(false);

    private RequestMiddlewareDelegate CreateDefaultPipeline() => Setup(x => x.UseRequestHandlers());
}