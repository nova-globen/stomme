using Stomme.Engine.Pipelines.Middleware.Pipelines;

namespace Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

/// <inheritdoc />
public class PipelineExecutionPipeline : IPipelineExecutionPipeline
{
    private readonly IServiceProvider _serviceProvider;
    private PipelineMiddlewareDelegate? _executionPipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineExecutionPipeline"/> class.
    /// </summary>
    public PipelineExecutionPipeline(IServiceProvider serviceProvider, Action<IPipelineExecutionPipelineBuilder> configurePipelineBuilder)
    {
        _serviceProvider = serviceProvider;
        ConfigurePipelineBuilder = configurePipelineBuilder;
        Setup(configurePipelineBuilder);
    }
    
    /// <inheritdoc />
    public PipelineMiddlewareDelegate Pipeline => _executionPipeline ??= CreateDefaultPipeline();

    /// <inheritdoc />
    public Action<IPipelineExecutionPipelineBuilder> ConfigurePipelineBuilder { get; }

    /// <inheritdoc />
    public PipelineMiddlewareDelegate Setup(Action<IPipelineExecutionPipelineBuilder> setup)
    {
        var builder = new PipelineExecutionPipelineBuilder(_serviceProvider);
        setup(builder);
        _executionPipeline = builder.Build();
        return _executionPipeline;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(PipelineExecutionContext context) => await Pipeline(context).ConfigureAwait(false);

    private PipelineMiddlewareDelegate CreateDefaultPipeline() => Setup(x => x
        .UseExceptionHandling()
        .UseDefaultStepScheduler());
}