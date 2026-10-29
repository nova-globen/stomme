using System;
using System.Collections.Generic;
using System.Reflection;
using Stomme.Extensions;
using Stomme.Infrastructure.EventBus;
using Stomme.Persistence.RavenDb.Messaging;
using Microsoft.Extensions.Configuration;
using Raven.Client.Documents;
using Rebus.Config;
using Rebus.Transport;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Host wiring for the Stomme integration message bus.
    ///
    /// The bus is the cross-context ASYNC integration pipeline — not the in-process mediator. It is
    /// opt-in: with no <c>Messaging</c> section (or <c>Messaging:Enabled</c> false) nothing is
    /// registered and the host boots exactly as it did before, so an existing install is unaffected.
    /// Enable it in appsettings.json:
    ///
    /// <code>
    /// "Messaging": {
    ///   "Enabled": true,
    ///   "InputQueue": "stomme",          // per-node queue name; nodes sharing one name COMPETE for
    ///                                      // each message instead of all receiving it
    ///   "Transport": "inmemory",           // inmemory (default) | rabbitmq | azureservicebus | ravendb
    ///   "ConnectionString": "",            // broker connection; falls back to the connection string
    ///                                      // named after the transport (unused by ravendb, which rides
    ///                                      // the host's shared document store)
    ///   "MaxDeliveryAttempts": 3
    /// }
    /// </code>
    ///
    /// Integration-event handlers are discovered across every loaded Stomme assembly using the
    /// framework's own assembly filter, so a module contributes handlers by shipping them — the same
    /// convention-over-configuration contract as <c>IService</c>/<c>IServerStartup</c>.
    ///
    /// Relational deployments should stay on the in-memory transport plus the transactional outbox, or
    /// move to RabbitMQ / Azure Service Bus. A RavenDB install can select <c>ravendb</c>: a durable
    /// queue-document transport over the same lazily-built <c>IDocumentStore</c> the framework store
    /// uses — no broker, no connection string — with the RavenDB-backed dead-letter queue
    /// replacing the null admin port. There is deliberately no SQL bus transport.
    /// </summary>
    public static class StommeMessagingExtensions
    {
        public static IServiceCollection AddStommeMessaging(this IServiceCollection services, IConfiguration configuration)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            // Opt-in. Absent section => no bus, no hosted services, no behavior change.
            if (!configuration.GetValue<bool>("Messaging:Enabled"))
            {
                return services;
            }

            var inputQueue = configuration["Messaging:InputQueue"] ?? "stomme";

            // RavenDB is the one transport the messaging library cannot config-select itself: it rides the
            // host's lazily-built shared IDocumentStore, which the library deliberately does not
            // reference. The host maps the config string to the code-passed configurator here — matching
            // trim + case-insensitively, like the library's own transport switch. IDocumentStore is resolved
            // at bus-build time (the same runtime resolution AddRavenDbDeadLetterQueue uses), so composing
            // the container never touches RavenDB.
            var isRavenDbTransport = string.Equals(
                configuration["Messaging:Transport"]?.Trim(), "ravendb", StringComparison.OrdinalIgnoreCase);

            Action<StandardConfigurer<ITransport>, IServiceProvider> transport = null;
            if (isRavenDbTransport)
            {
                transport = (configurer, provider) =>
                    configurer.UseRavenDb(provider.GetRequiredService<IDocumentStore>(), inputQueue);
            }

            // Reuse the framework's discovery: every loaded Stomme assembly may contribute
            // IIntegrationEventHandler<T> implementations.
            IEnumerable<Assembly> handlerAssemblies = AppDomain.CurrentDomain.GetStommeAssemblies();

            services.AddMessaging(
                handlerAssemblies,
                configure: options =>
                {
                    options.InputQueue = inputQueue;
                    options.ErrorQueue = inputQueue + ".error";
                    configuration.GetSection("Messaging").Bind(options);

                    // For a config-selected broker, fall back to the like-named connection string when
                    // Messaging:ConnectionString is not set explicitly.
                    if (string.IsNullOrWhiteSpace(options.ConnectionString) && !string.IsNullOrWhiteSpace(options.Transport))
                    {
                        options.ConnectionString = configuration.GetConnectionString(options.Transport);
                    }
                },
                transport: transport);

            // The framework's own PUBLISHED events. A published event reaches this host's handlers only once its topic is
            // subscribed, so without these the bus silently dropped every live notice the moment messaging was switched on: an
            // editor's lock, presence and save notices, a dashboard's dataset change, and every pipeline event trigger. Point-to-
            // point sends (the notifications hint) need none. Each handler resolves its module lazily, so a subscription for a
            // module that is switched off costs a no-op delivery.
            services.AddBusSubscription<Stomme.Modules.Collaboration.Realtime.CollaborationChangedNotice>();
            services.AddBusSubscription<Stomme.Modules.Dashboards.Realtime.DatasetChangedNotice>();
            services.AddBusSubscription<Stomme.Modules.Pipelines.Triggers.PipelineEventRaised>();

            if (isRavenDbTransport)
            {
                // The RavenDB transport dead-letters into its own queue documents, so the RavenDB
                // IDeadLetterQueue replaces the NullDeadLetterQueue fallback the other transports keep.
                // The error queue mirrors the options binding above: config wins, else "<input>.error".
                var errorQueue = configuration["Messaging:ErrorQueue"];
                services.AddRavenDbDeadLetterQueue(
                    inputQueue,
                    string.IsNullOrWhiteSpace(errorQueue) ? inputQueue + ".error" : errorQueue);
            }

            return services;
        }
    }
}
