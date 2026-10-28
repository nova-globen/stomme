using Stomme.Engine.Expressions.Options;
using Stomme.Engine.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Expressions.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTypeAlias<T>(this IServiceCollection services, string alias)
    {
        services.Configure<ExpressionOptions>(options => options.AddTypeAlias<T>(alias));
        return services;
    }
}