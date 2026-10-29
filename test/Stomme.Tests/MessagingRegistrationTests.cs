using System.Collections.Generic;
using System.Linq;
using Stomme.EventBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Xunit;

namespace Stomme.Tests
{
    // The integration bus is opt-in host wiring. These guard the gate itself:
    // an existing install with no Messaging section must boot exactly as before — no bus, no hosted
    // services, no Rebus worker threads — while enabling the section really does register the seam.
    [Trait("Category", "ExternalDatabase")]
    public class MessagingRegistrationTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] settings) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build();

        [Fact]
        public void Messaging_is_not_registered_without_the_config_section()
        {
            var services = new ServiceCollection();

            services.AddStommeMessaging(Config());

            // The whole point: absent config changes nothing about the host.
            Assert.Empty(services);
        }

        [Fact]
        public void Messaging_stays_off_when_explicitly_disabled()
        {
            var services = new ServiceCollection();

            services.AddStommeMessaging(Config(("Messaging:Enabled", "false")));

            Assert.Empty(services);
        }

        [Fact]
        public void Enabling_messaging_registers_the_event_bus_seam()
        {
            var services = new ServiceCollection();

            services.AddStommeMessaging(Config(
                ("Messaging:Enabled", "true"),
                ("Messaging:InputQueue", "stomme-test")));

            // Consumers depend on the seam; the transport stays an implementation detail.
            Assert.Contains(services, d => d.ServiceType == typeof(IEventBus));
            Assert.Contains(services, d => d.ServiceType == typeof(IDeadLetterQueue));
        }

        [Fact]
        public void Selecting_the_ravendb_transport_registers_the_raven_dead_letter_queue()
        {
            var services = new ServiceCollection();

            // Stand-in for the host's lazily-built shared document store: the messaging wiring
            // resolves IDocumentStore at runtime, so composition needs only a registration — never a
            // connection. The real host registers this via AddRavenFrameworkStore before AddStommeMessaging.
            using var store = new DocumentStore();
            services.AddSingleton<IDocumentStore>(store);

            services.AddStommeMessaging(Config(
                ("Messaging:Enabled", "true"),
                ("Messaging:Transport", "RavenDB"), // matching is trim + case-insensitive, like the library's own switch
                ("Messaging:InputQueue", "stomme-test")));

            using var provider = services.BuildServiceProvider();
            var deadLetterQueue = provider.GetRequiredService<IDeadLetterQueue>();

            // The RavenDB transport dead-letters into queue documents, so its IDeadLetterQueue must replace
            // the NullDeadLetterQueue fallback the other transports keep.
            Assert.Equal("RavenDbDeadLetterQueue", deadLetterQueue.GetType().Name);
        }

        [Fact]
        public void Broker_and_default_transports_keep_the_null_dead_letter_queue()
        {
            var services = new ServiceCollection();

            services.AddStommeMessaging(Config(
                ("Messaging:Enabled", "true"),
                ("Messaging:Transport", "inmemory"),
                ("Messaging:InputQueue", "stomme-test")));

            using var provider = services.BuildServiceProvider();
            var deadLetterQueue = provider.GetRequiredService<IDeadLetterQueue>();

            // Mirrors the library's existing behavior: any non-ravendb transport (including an unknown
            // value, which falls back to in-memory) keeps the null admin port.
            Assert.Equal("NullDeadLetterQueue", deadLetterQueue.GetType().Name);
        }
    }
}
