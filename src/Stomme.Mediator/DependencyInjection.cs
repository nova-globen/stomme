using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Mediator;

public static class MediatorServiceExtensions
{
    public static IServiceCollection AddMediator(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        // Idempotent. Two startups in one assembly calling AddMediator<T>() registered every handler twice, so a
        // notification ran twice; each (service, implementation) pair is now registered once however often this is called.
        services.TryAddScoped<IMediator, Mediator>();

        var allTypes = assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .ToList();

        RegisterImplementations(services, allTypes, typeof(IRequestHandler<,>), ServiceLifetime.Scoped);
        RegisterImplementations(services, allTypes, typeof(INotificationHandler<>), ServiceLifetime.Scoped);
        RegisterImplementations(services, allTypes, typeof(IPipelineBehavior<,>), ServiceLifetime.Scoped);
        RegisterImplementations(services, allTypes, typeof(IStreamRequestHandler<,>), ServiceLifetime.Scoped);

        return services;
    }

    public static IServiceCollection AddMediator<TMarker>(this IServiceCollection services)
        => services.AddMediator(typeof(TMarker).Assembly);

    // The zero-argument form, spelled out. MEASURED (BL-95 increment (a)): once the host referenced the
    // pipeline engine, whose own AddMediator(IServiceCollection, Action<MediatorOptions>? = null) lives in the
    // Microsoft.Extensions.DependencyInjection namespace, a bare services.AddMediator() bound to THAT overload - an
    // optional parameter beats an empty params array - and a stock host silently composed the engine's mediator as
    // IMediator while Stomme's own was never registered. An exact zero-parameter candidate wins over both;
    // PipelinesHostCompositionTests pins that a stock host composes no engine type.
    public static IServiceCollection AddMediator(this IServiceCollection services)
        => services.AddMediator(Array.Empty<Assembly>());

    public static IServiceCollection AddMediatorBehavior(
        this IServiceCollection services, Type openGenericBehavior)
    {
        services.AddScoped(typeof(IPipelineBehavior<,>), openGenericBehavior);
        return services;
    }

    private static void RegisterImplementations(
        IServiceCollection services,
        List<Type> allTypes,
        Type openGenericInterface,
        ServiceLifetime lifetime)
    {
        foreach (var type in allTypes)
        {
            var interfaces = type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface);

            foreach (var iface in interfaces)
            {
                services.TryAddEnumerable(new ServiceDescriptor(iface, type, lifetime));
            }
        }
    }
}
