namespace Stomme.Engine.Pipelines.Builders;

/// <inheritdoc />
public class PipelineBuilderFactory : IPipelineBuilderFactory
{
    private readonly Func<IPipelineBuilder> _factory;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineBuilderFactory"/> class.
    /// </summary>
    public PipelineBuilderFactory(Func<IPipelineBuilder> factory) => _factory = factory;

    /// <inheritdoc />
    public IPipelineBuilder CreateBuilder() => _factory();
}