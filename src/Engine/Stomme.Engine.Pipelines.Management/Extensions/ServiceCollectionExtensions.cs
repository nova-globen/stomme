

// ReSharper disable once CheckNamespace

using Stomme.Engine.Pipelines;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides extension methods to <see cref="IServiceCollection"/> for various kinds of services and providers. 
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="IStepProvider"/>.
    /// </summary>
    public static IServiceCollection AddStepProvider<T>(this IServiceCollection services) where T : class, IStepProvider
    {
        return services
            .AddScoped<T>()
            .AddScoped<IStepProvider, T>(sp => sp.GetRequiredService<T>());
    }
}