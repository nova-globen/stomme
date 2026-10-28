using Stomme.Engine.Pipelines;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddStorageDriver<T>() where T : class, IStorageDriver
        {
            return services.AddScoped<IStorageDriver, T>();
        }

        public IServiceCollection AddStepStateFilter<T>() where T : class, IStepStateFilter
        {
            return services.AddScoped<IStepStateFilter, T>();
        }
    }
}