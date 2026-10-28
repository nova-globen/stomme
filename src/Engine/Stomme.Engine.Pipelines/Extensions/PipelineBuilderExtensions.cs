using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Steps;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Contains extensions for <see cref="IPipelineBuilder"/>.
/// </summary>
public static class PipelineBuilderExtensions
{
    /// <summary>
    /// Builds a pipeline asynchronously.
    /// </summary>
    /// <typeparam name="T">The type of the pipeline.</typeparam>
    /// <param name="builder">The <see cref="IPipelineBuilder"/> instance to build the pipeline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation that returns the built <see cref="Pipeline"/>.</returns>
    public static Task<Pipeline> BuildPipelineAsync<T>(
        this IPipelineBuilder builder, 
        CancellationToken cancellationToken = default) where T : IPipeline, new()
    {
        return builder.BuildPipelineAsync(Activator.CreateInstance<T>(), cancellationToken);
    }
}