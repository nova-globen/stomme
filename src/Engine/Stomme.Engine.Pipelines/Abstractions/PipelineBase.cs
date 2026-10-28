using Stomme.Engine.Pipelines.Memory;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A base class for implementing pipeline definitions using the pipelineBuilder API.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors)]
public abstract class PipelineBase : IPipeline
{
    /// <summary>
    /// Invokes the specified <see cref="IPipelineBuilder"/>.
    /// </summary>
    protected virtual ValueTask BuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default)
    {
        Build(builder);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Invokes the specified <see cref="IPipelineBuilder"/>.
    /// </summary>
    /// <param name="builder"></param>
    protected virtual void Build(IPipelineBuilder builder)
    {
    }

    /// <summary>
    /// Gives derived types a chance to setup the <see cref="IPipelineBuilder"/> before the BuildAsync method is invoked.
    /// </summary>
    protected virtual ValueTask BeforeBuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    
    /// <summary>
    /// Gives derived types a chance to setup the <see cref="IPipelineBuilder"/> after the BuildAsync method was invoked.
    /// </summary>
    protected virtual ValueTask AfterBuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    async ValueTask IPipeline.BuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken)
    {
        await BeforeBuildAsync(builder, cancellationToken).ConfigureAwait(false);
        await BuildAsync(builder, cancellationToken).ConfigureAwait(false);
        await AfterBuildAsync(builder, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A base class for implementing pipeline definitions that can return a result using the pipelineBuilder API.
/// </summary>
public abstract class PipelineBase<TResult> : PipelineBase
{
    /// <inheritdoc />
    protected PipelineBase()
    {
        Result = new("Result", default!);
    }
    
    /// <summary>
    /// Use this variable from your pipeline to assign a result value. 
    /// </summary>
    protected Variable<TResult> Result { get; }

    /// <inheritdoc />
    protected override ValueTask BeforeBuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default)
    {
        builder.Result = Result;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask AfterBuildAsync(IPipelineBuilder builder, CancellationToken cancellationToken = default)
    {
        var variables = builder.Variables;
        
        if(!variables.Contains(Result))
           variables.Add(Result);

        builder.Variables = variables;
        
        return ValueTask.CompletedTask;
    }
}