using System.Reflection;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Persistence;

/// <summary>
/// Registers the domain-event unit of work: the ambient aggregate tracker (shared with the store
/// providers, which enroll written aggregates), the dispatcher, the <see cref="IUnitOfWork"/>, and
/// the <see cref="IDomainEventHandler{TEvent}"/> implementations discovered in the given assemblies.
/// </summary>
public static class PersistenceServiceExtensions
{
    public static IServiceCollection AddDomainEvents(this IServiceCollection services, params Assembly[] handlerAssemblies)
    {
        services.TryAddScoped<IAggregateTracker, AggregateTracker>();

        // CURRENT-SCOPE DISPATCH, AND ONLY ONE IMPLEMENTATION OF IT.
        //
        // This used to register a rival: Stomme.Persistence's own DomainEventDispatcher, which opened a NEW
        // child scope per dispatch, while Stomme.FeatureManagement TryAdd'd a current-scope one. Both are
        // TryAdd, i.e. first-caller-wins, so which SEMANTICS the port got depended on the order the host
        // happened to call AddFeatureManagement / AddDomainEvents in — an ordering accident with no compile
        // error to announce it (src/Stomme.Persistence/README.md defect #4).
        //
        // Current scope it is. Both rivals are gone and every caller now registers the one
        // CurrentScopeDomainEventDispatcher, which takes IServiceProvider — it has no scope factory to create a
        // scope with, so new-scope dispatch is unrepresentable rather than merely unregistered. Registration
        // order is now semantically inert: TryAdd either wins or loses with the SAME implementation type.
        //
        // Scoped on its own merits: dispatch is per unit of work, its consumer (IUnitOfWork) is scoped, and its
        // handlers are scoped.
        services.TryAddScoped<IDomainEventDispatcher, CurrentScopeDomainEventDispatcher>();

        // The framework's single UnitOfWork lives in Stomme.Persistence.Abstractions: both this path
        // and AddFeatureManagement register the SAME type, so the old clear-vs-dispatch order accident between
        // the two rival impls is gone and registration order is inert, like the dispatcher line above.
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();

        foreach (var (handlerType, serviceType) in DiscoverHandlers(handlerAssemblies))
        {
            // TryAddEnumerable, not AddScoped: AddOutbox calls AddDomainEvents, so a host that calls both
            // (or passes the same assembly twice) would otherwise register every handler twice and handle
            // every domain event twice. De-duplication is on (service type, implementation type), so genuinely
            // different handlers for one event still all register.
            services.TryAddEnumerable(ServiceDescriptor.Scoped(serviceType, handlerType));
        }

        return services;
    }

    public static IServiceCollection AddDomainEvents<TMarker>(this IServiceCollection services) =>
        services.AddDomainEvents(typeof(TMarker).Assembly);

    /// <summary>
    /// Registers the transactional outbox: the domain-event dispatcher + handlers (via
    /// <see cref="AddDomainEvents(IServiceCollection, Assembly[])"/>), the event serializer and type
    /// registry, the <see cref="OutboxProcessor"/>, and the background poller. Registering the
    /// serializer is what switches the store providers from in-process dispatch to writing the outbox
    /// inside the aggregate's transaction. The provider registration supplies the matching
    /// <see cref="IOutboxStore"/>.
    /// </summary>
    public static IServiceCollection AddOutbox(
        this IServiceCollection services, Action<OutboxOptions>? configure = null, params Assembly[] assemblies)
    {
        services.AddDomainEvents(assemblies);
        services.TryAddSingleton(new DomainEventTypeRegistry(assemblies));
        services.TryAddSingleton<IDomainEventSerializer, DomainEventSerializer>();
        services.TryAddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxBackgroundService>();
        services.Configure(configure ?? (_ => { }));
        return services;
    }

    private static IEnumerable<(Type HandlerType, Type ServiceType)> DiscoverHandlers(IEnumerable<Assembly> assemblies)
    {
        // GetTypes (not GetExportedTypes) so internal handlers — the common case — are discovered.
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
                     .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }))
        {
            foreach (var @interface in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                yield return (type, @interface);
            }
        }
    }
}
