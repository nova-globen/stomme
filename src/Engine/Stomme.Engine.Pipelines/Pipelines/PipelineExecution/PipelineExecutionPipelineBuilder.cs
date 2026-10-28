namespace Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

/// <inheritdoc />
public class PipelineExecutionPipelineBuilder : IPipelineExecutionPipelineBuilder
{
    private const string ServicesKey = "pipeline-execution.Services";
    private readonly IList<Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate>> _components = new List<Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate>>();

    /// <summary>
    /// Constructor.
    /// </summary>
    public PipelineExecutionPipelineBuilder(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IDictionary<object, object?> Properties { get; } = new Dictionary<object, object?>();

    /// <inheritdoc />
    public IEnumerable<Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate>> Components => _components.ToList();

    /// <inheritdoc />
    public IServiceProvider ServiceProvider
    {
        get => GetProperty<IServiceProvider>(ServicesKey)!;
        set => SetProperty(ServicesKey, value);
    }

    /// <inheritdoc />
    public IPipelineExecutionPipelineBuilder Use(Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware)
    {
        _components.Add(middleware);
        return this;
    }

    /// <inheritdoc />
    public PipelineMiddlewareDelegate Build()
    {
        PipelineMiddlewareDelegate executionPipeline = _ => new ValueTask();

        foreach (var component in _components.Reverse()) 
            executionPipeline = component(executionPipeline);

        return executionPipeline;
    }

    /// <inheritdoc />
    public IPipelineExecutionPipelineBuilder Reset()
    {
        _components.Clear();
        return this;
    }

    /// <inheritdoc />
    public IPipelineExecutionPipelineBuilder Insert(int index, Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware)
    {
        _components.Insert(index, middleware);
        return this;
    }

    /// <inheritdoc />
    public IPipelineExecutionPipelineBuilder Replace(int index, Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware)
    {
        _components[index] = middleware;
        return this;
    }

    private T? GetProperty<T>(string key) => Properties.TryGetValue(key, out var value) ? (T?)value : default(T);
    private void SetProperty<T>(string key, T value) => Properties[key] = value;
}