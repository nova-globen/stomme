using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Contracts;
using Stomme.Engine.Pipelines.Runtime.Providers;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides extension methods for <see cref="IServiceCollection"/>.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds the specified pipeline provider type to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the pipeline provider to add. Must implement <see cref="IPipelinesProvider"/>.</typeparam>
    [Obsolete("Use AddPipelinesProvider instead.", false)]
    public static IServiceCollection AddPipelineDefinitionProvider<T>(this IServiceCollection services) where T : class, IPipelinesProvider => services.AddScoped<IPipelinesProvider, T>();
    /// <summary>
    /// Registers a <see cref="ITriggerPayloadValidator{TPayload}"/> with the service container.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <typeparam name="TValidator">Validator of the validator</typeparam>
    /// <typeparam name="TPayload">Payload type</typeparam>
    public static IServiceCollection AddTriggerPayloadValidator<TValidator, TPayload>(this IServiceCollection services)
        where TValidator : class, ITriggerPayloadValidator<TPayload>
    {
        return services.AddScoped<ITriggerPayloadValidator<TPayload>, TValidator>();
    }

    /// <summary>
    /// Adds the specified pipelines provider type to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the pipelines provider to add. Must implement <see cref="IPipelinesProvider"/>.</typeparam>
    public static IServiceCollection AddPipelinesProvider<T>(this IServiceCollection services) where T : class, IPipelinesProvider => services.AddScoped<IPipelinesProvider, T>();
}