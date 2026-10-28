using Stomme.Engine.Pipelines.Middleware.Steps;

namespace Stomme.Engine.Pipelines.Pipelines.StepExecution;

/// <inheritdoc />
public class StepExecutionPipeline : IStepExecutionPipeline
{
    private readonly IServiceProvider _serviceProvider;
    private StepMiddlewareDelegate? _executionPipeline;

    /// <summary>
    /// Constructor.
    /// </summary>
    public StepExecutionPipeline(IServiceProvider serviceProvider, Action<IStepExecutionPipelineBuilder> pipelineBuilder)
    {
        _serviceProvider = serviceProvider;
        Setup(pipelineBuilder);
    }

    /// <inheritdoc />
    public StepMiddlewareDelegate Setup(Action<IStepExecutionPipelineBuilder> setup)
    {
        var builder = new StepExecutionPipelinePipelineBuilder(_serviceProvider);
        setup(builder);
        _executionPipeline = builder.Build();
        return _executionPipeline;
    }

    /// <inheritdoc />
    public StepMiddlewareDelegate Pipeline => _executionPipeline ??= CreateDefaultPipeline();

    /// <inheritdoc />
    public async Task ExecuteAsync(StepExecutionContext context) => await Pipeline(context).ConfigureAwait(false);
        
    private StepMiddlewareDelegate CreateDefaultPipeline() => Setup(x => x
        .UseLogging()
        .UseExceptionHandling()
        .UseExecutionLogging()
        .UseNotifications()
        .UseDefaultStepInvoker()
    );
}