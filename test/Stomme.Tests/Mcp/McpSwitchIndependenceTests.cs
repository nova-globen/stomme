#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Mcp;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Modules.Mcp.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// <b>TWO SWITCHES, AND NEITHER IMPLIES THE OTHER</b>: <c>Mcp:Enabled</c> is the CLIENT half - this site may
    /// register external MCP servers - and <c>Mcp:Server:Enabled</c> is the ENDPOINT this install publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D101 named this as a mistake to avoid rather than one already made: reading one switch for the other hides the
    /// client-half admin page behind the endpoint's flag, so an install that only CONSUMES external servers - and
    /// publishes nothing of its own - would have no way to manage them. It is easy to write, impossible to see in a
    /// driven suite that turns both on, and this is the only place that looks.
    /// </para>
    /// <para>
    /// Asserted against the SERVICE COLLECTION rather than a booted host: what is being checked is which registrations
    /// each switch produces, and that is a question about composition. A driven install would cost forty seconds per
    /// combination and answer the same question less precisely. The driven suites cover the both-on case.
    /// </para>
    /// </remarks>
    public class McpSwitchIndependenceTests
    {
        public static TheoryData<bool, bool> Combinations() => new()
        {
            { false, false },
            { true, false },
            { false, true },
            { true, true },
        };

        [Theory]
        [MemberData(nameof(Combinations))]
        public void Each_half_is_registered_exactly_when_its_OWN_switch_is_on(bool client, bool server)
        {
            var services = Compose(client, server);

            // The client half: the connector, the guard, the tool source and the registration service.
            Assert.Equal(client, IsRegistered<IMcpEndpointGuard>(services));
            Assert.Equal(client, IsRegistered<IMcpClientConnector>(services));
            Assert.Equal(client, IsRegistered<IMcpClientToolSource>(services));
            Assert.Equal(client, IsRegistered<McpRegistrationService>(services));

            // The server half: the catalog and the contributor registry.
            Assert.Equal(server, IsRegistered<IMcpServerCatalog>(services));
            Assert.Equal(server, IsRegistered<McpContributorRegistry>(services));

            // No contributor is even LOOKED FOR when the server half is off - the property McpDisabledSurface drives
            // over HTTP, asserted here as the absence of the registrations that discovery produces.
            Assert.Equal(server, services.Any(descriptor => descriptor.ServiceType == typeof(IMcpContributor)));
        }

        [Theory]
        [MemberData(nameof(Combinations))]
        public void The_two_aggregates_are_stored_whatever_either_switch_says(bool client, bool server)
        {
            // A switch turned off must not take the DATA with it: a site that turns the client half off for a week
            // must still have its registrations - and its audit ledger - when it turns it back on.
            var services = Compose(client, server);

            Assert.True(IsRegistered<Stomme.Mcp.Persistence.IMcpServerRegistrationRepository>(services));
            Assert.True(IsRegistered<Stomme.Mcp.Persistence.IMcpAuditRepository>(services));
            Assert.True(IsRegistered<IMcpClock>(services));
        }

        [Fact]
        public void An_absent_or_unparseable_switch_is_OFF()
        {
            // The default posture is off, and anything that is not the word "true" leaves it off: a typo in an
            // operator's configuration must not publish an endpoint or open an outbound path.
            foreach (var value in new string?[] { null, "", " ", "yes", "1", "True!", "Off" })
            {
                var configuration = Configuration(new Dictionary<string, string?>
                {
                    [McpSettings.ClientEnabledKey] = value,
                    [McpSettings.ServerEnabledKey] = value,
                });

                Assert.False(McpSettings.ClientEnabled(configuration), $"client, value '{value ?? "<null>"}'");
                Assert.False(McpSettings.ServerEnabled(configuration), $"server, value '{value ?? "<null>"}'");
            }

            // ...and the other direction, so the assertion above is not vacuously true of everything.
            var on = Configuration(new Dictionary<string, string?>
            {
                [McpSettings.ClientEnabledKey] = "true",
                [McpSettings.ServerEnabledKey] = "TRUE",
            });
            Assert.True(McpSettings.ClientEnabled(on));
            Assert.True(McpSettings.ServerEnabled(on));
        }

        [Fact]
        public void A_configured_route_must_be_an_absolute_path()
        {
            Assert.Equal(McpSettings.DefaultPath, McpSettings.Path(Configuration(new Dictionary<string, string?>())));
            Assert.Equal("/mcp", McpSettings.Path(Configuration(new Dictionary<string, string?> { [McpSettings.PathKey] = "/mcp" })));

            // A relative value would map the endpoint somewhere nobody intended, and silently.
            Assert.Throws<InvalidOperationException>(() =>
                McpSettings.Path(Configuration(new Dictionary<string, string?> { [McpSettings.PathKey] = "mcp/v1" })));
        }

        private static IServiceCollection Compose(bool client, bool server)
        {
            var configuration = Configuration(new Dictionary<string, string?>
            {
                [McpSettings.ClientEnabledKey] = client ? "true" : "false",
                [McpSettings.ServerEnabledKey] = server ? "true" : "false",
            });

            var services = new ServiceCollection();

            // The module startup reads the configuration off the COLLECTION, the way AddStomme leaves it there as an
            // instance before any module startup runs.
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton((IConfigurationRoot)configuration);
            services.AddLogging();

            new ServerStartup().ConfigureServices(services);
            return services;
        }

        private static IConfigurationRoot Configuration(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        private static bool IsRegistered<T>(IServiceCollection services) =>
            services.Any(descriptor => descriptor.ServiceType == typeof(T));
    }
}
