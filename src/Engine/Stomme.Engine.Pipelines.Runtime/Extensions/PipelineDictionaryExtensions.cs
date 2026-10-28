using Stomme.Engine.Pipelines;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Extension methods for <see cref="IDictionary{TKey,TValue}"/>.
/// </summary>
public static class PipelineDictionaryExtensions
{
    /// <summary>
    /// Register a pipeline factory that creates an instance of the specified pipeline type. 
    /// </summary>
    public static void Add<TPipeline>(this IDictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>> dictionary) where TPipeline : IPipeline
    {
        // FullName should never be null here, as we filter out generic types
        dictionary.Add(typeof(TPipeline).FullName!, sp =>
        {
            var pipeline = ActivatorUtilities.GetServiceOrCreateInstance<TPipeline>(sp);
            return new ValueTask<IPipeline>(pipeline);
        });
    }
    
    /// <summary>
    /// Register a pipeline factory that creates an instance of the specified pipeline type. 
    /// </summary>
    public static void Add(this IDictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>> dictionary, Type pipelineType)
    {
        // FullName should never be null here, as we filter out generic types
        dictionary.Add(pipelineType.FullName!, sp =>
        {
            var pipeline = (IPipeline)ActivatorUtilities.GetServiceOrCreateInstance(sp, pipelineType);
            return new ValueTask<IPipeline>(pipeline);
        });
    }
}