using System.Reflection;
using Stomme.EventBus;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rebus.Config;
using Rebus.Handlers;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Pipeline.Send;
using Rebus.Retry.Simple;
using Rebus.Serialization.Json;
using Rebus.Transport;
using Rebus.Transport.InMem;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Wires the Rebus-backed message bus into the host: the <see cref="IEventBus"/> adapter, the
/// System.Text.Json serializer, worker/retry options, and discovery of
/// <see cref="IIntegrationEventHandler{TEvent}"/> implementations bridged to Rebus handlers. The
/// transport is a seam — callers pass a configurator (e.g. the custom RavenDB transport) or get an
/// in-memory transport by default (single-node dev / tests). Rebus' in-memory transport registers its
/// own centralized subscription storage, so single-node publish/subscribe works with the default
/// transport; broker transports do pub/sub natively and the RavenDB transport supplies its own.
/// </summary>
public static class MessagingServiceExtensions
{
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IEnumerable<Assembly> handlerAssemblies,
        Action<MessagingOptions>? configure = null,
        Action<StandardConfigurer<ITransport>, IServiceProvider>? transport = null)
    {
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        var options = new MessagingOptions();
        configure?.Invoke(options);

        // Discover every IIntegrationEventHandler<T> implementation across the given assemblies.
        var handlers = handlerAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
                .Select(i => (Implementation: type, EventType: i.GetGenericArguments()[0])))
            .ToList();

        foreach (var (implementation, eventType) in handlers)
        {
            services.AddTransient(typeof(IIntegrationEventHandler<>).MakeGenericType(eventType), implementation);
        }

        // Register the Rebus adapter only for event types that actually have a handler.
        foreach (var eventType in handlers.Select(h => h.EventType).Distinct())
        {
            services.AddTransient(
                typeof(IHandleMessages<>).MakeGenericType(eventType),
                typeof(IntegrationEventHandlerAdapter<>).MakeGenericType(eventType));
        }

        // Bounded in-memory buffer; capacity from options so hosts can tune it.
        services.AddSingleton(new BusTransportBuffer(options.BufferCapacity));
        // Register the concrete Rebus bus so the resilient decorator can inject it as its inner bus.
        // RebusEventBus is stateless (wraps the singleton IBus), so it is safe to register as Scoped.
        services.AddScoped<RebusEventBus>();
        // Resilient IEventBus decorator: on transport failure, buffers to the in-memory store; on full,
        // rejects with InvalidOperationException. Replay lambdas close over the inner RebusEventBus
        // instance so no additional service resolution is needed at flush time.
        services.AddScoped<IEventBus>(sp => new ResilientEventBus(
            sp.GetRequiredService<RebusEventBus>(),
            sp.GetRequiredService<BusTransportBuffer>(),
            sp.GetRequiredService<ILogger<ResilientEventBus>>()));
        // Background flusher: drains the buffer by invoking each message's replay delegate.
        services.AddHostedService<BusTransportFlusher>();

        // A transport-backed dead-letter queue (the RavenDB one) overrides this fallback when registered.
        services.TryAddSingleton<IDeadLetterQueue, NullDeadLetterQueue>();

        var network = new InMemNetwork();
        services.AddRebus((configurer, provider) =>
        {
            return configurer
                .Transport(t =>
                {
                    if (transport is not null)
                    {
                        transport(t, provider);
                    }
                    else
                    {
                        ConfigureSelectedTransport(t, options, network);
                    }
                })
                .Serialization(s => s.UseSystemTextJson())
                .Options(o =>
                {
                    o.SetNumberOfWorkers(options.EffectiveWorkers);
                    o.SetMaxParallelism(options.EffectiveParallelism);
                    o.RetryStrategy(errorQueueName: options.ErrorQueue, maxDeliveryAttempts: options.MaxDeliveryAttempts);

                    // Tenant propagation: stamp the ambient tenant on send and restore its scope on receive, so
                    // cross-context handlers run under the originating tenant. Skipped when tenancy isn't wired
                    // (minimal hosts/tests); the platform host registers it.
                    //
                    // Whether tenancy is wired is answered WITHOUT resolving the ports: the bus is a singleton
                    // built from the root provider, and both ports may legitimately be registered scoped (the
                    // platform host registers ITenantContext scoped), which the root provider refuses to
                    // resolve. IServiceProviderIsService asks the question from metadata; the steps then
                    // resolve the ports per message inside their own scope. IServiceScopeFactory is a
                    // singleton, so injecting it here is safe.
                    if (IsTenancyWired(services, provider))
                    {
                        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
                        o.Decorate<IPipeline>(c => new PipelineStepInjector(c.Get<IPipeline>())
                            .OnSend(
                                new TenantPropagationOutgoingStep(scopeFactory),
                                PipelineRelativePosition.Before,
                                typeof(SerializeOutgoingMessageStep))
                            .OnReceive(
                                new TenantScopeIncomingStep(scopeFactory),
                                PipelineRelativePosition.Before,
                                typeof(DispatchIncomingMessageStep)));
                    }
                });
        });

        return services;
    }

    // True when the host wired BOTH tenancy ports, answered without instantiating either of them.
    // IServiceProviderIsService is registered by the default container; the ServiceCollection scan is the
    // fallback for a container that does not offer it.
    private static bool IsTenancyWired(IServiceCollection services, IServiceProvider provider)
    {
        var isService = provider.GetService<IServiceProviderIsService>();
        if (isService is not null)
        {
            return isService.IsService(typeof(ITenantContext)) && isService.IsService(typeof(ITenantScopeFactory));
        }

        return services.Any(d => d.ServiceType == typeof(ITenantContext))
            && services.Any(d => d.ServiceType == typeof(ITenantScopeFactory));
    }

    // Selects a broker transport from configuration when the host did not supply one explicitly.
    private static void ConfigureSelectedTransport(
        StandardConfigurer<ITransport> configurer, MessagingOptions options, InMemNetwork network)
    {
        switch (options.Transport?.Trim().ToLowerInvariant())
        {
            case "rabbitmq":
                configurer.UseRabbitMq(
                    options.ConnectionString ?? throw new InvalidOperationException("Messaging:ConnectionString is required for the RabbitMQ transport."),
                    options.InputQueue);
                break;

            case "azureservicebus":
                configurer.UseAzureServiceBus(
                    options.ConnectionString ?? throw new InvalidOperationException("Messaging:ConnectionString is required for the Azure Service Bus transport."),
                    options.InputQueue);
                break;

            default:
                configurer.UseInMemoryTransport(network, options.InputQueue);
                break;
        }
    }
}
