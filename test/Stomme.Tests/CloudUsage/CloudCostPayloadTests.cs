#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Stomme.Tests.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>Fixture-backed <see cref="ICloudCostPayloadSource"/> returning a captured sample payload.</summary>
    internal sealed class StaticCloudCostPayloadSource(string payload) : ICloudCostPayloadSource
    {
        public Task<string> ReadAsync(CloudCostQuery query, CancellationToken ct = default) => Task.FromResult(payload);
    }

    internal static class CloudCostQueries
    {
        public static CloudCostQuery July2026 { get; } = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), new Dictionary<string, string>());
    }

    /// <summary>
    /// The cases of <c>ConnectorCostPayloadSourceTests</c>, by name. Note: the request leaves through the
    /// query's HOST egress (a stub here, recording the client it handed out), never a handler the source built; a query with
    /// no egress is a configuration error - the port's own case.
    /// </summary>
    public sealed class ConnectorCostPayloadSourceTests
    {
        private sealed class StubHandler(HttpStatusCode status, string body, Action<HttpRequestMessage, string?>? capture = null) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                capture?.Invoke(request, content);
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            }
        }

        private static CloudCostQuery Query(IReadOnlyDictionary<string, string> settings, StubEgress? egress) =>
            new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), settings, egress);

        [Fact]
        public async Task Read_IssuesABearerGet_AndSubstitutesTheWindowInTheEndpoint()
        {
            HttpRequestMessage? sent = null;
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK, "[]", (r, _) => sent = r));

            var payload = await new ConnectorCostPayloadSource().ReadAsync(Query(new Dictionary<string, string>
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = "https://billing.example.test/costs?from={from}&to={to}",
                [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token",
            }, egress));

            Assert.Equal("[]", payload);
            Assert.Equal(1, egress.Clients); // the one client, the egress's
            Assert.Equal(HttpMethod.Get, sent?.Method);
            Assert.Equal("https://billing.example.test/costs?from=2026-07-01&to=2026-07-31", sent?.RequestUri?.ToString());
            Assert.Equal("Bearer", sent?.Headers.Authorization?.Scheme);
            Assert.Equal("vault-token", sent?.Headers.Authorization?.Parameter);
        }

        [Fact]
        public async Task Read_PostsTheSubstitutedJsonBody_WhenARequestBodyIsConfigured()
        {
            HttpRequestMessage? sent = null;
            string? sentBody = null;
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK, "{}", (r, b) => (sent, sentBody) = (r, b)));

            _ = await new ConnectorCostPayloadSource().ReadAsync(Query(new Dictionary<string, string>
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = "https://billing.example.test/query",
                [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token",
                [ConnectorCostPayloadSource.RequestBodySettingKey] = """{ "timePeriod": { "from": "{from}", "to": "{to}" } }""",
            }, egress));

            Assert.Equal(HttpMethod.Post, sent?.Method);
            Assert.Equal("application/json", sent?.Content?.Headers.ContentType?.MediaType);
            Assert.Contains("\"from\": \"2026-07-01\"", sentBody, StringComparison.Ordinal);
            Assert.Contains("\"to\": \"2026-07-31\"", sentBody, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Read_WithoutEndpointOrCredential_ThrowsAConfigurationError()
        {
            var source = new ConnectorCostPayloadSource();
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK, "[]"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Query(new Dictionary<string, string> { [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token" }, egress)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Query(new Dictionary<string, string>
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = "not-a-url",
                [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token",
            }, egress)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Query(new Dictionary<string, string> { [ConnectorCostPayloadSource.EndpointSettingKey] = "https://billing.example.test/costs" }, egress)));
            Assert.Equal(0, egress.Clients); // nothing left the process for a request that could not be made
        }

        [Fact]
        public async Task Read_NonSuccessStatus_ThrowsHttpRequestException()
        {
            var egress = new StubEgress(new StubHandler(HttpStatusCode.Forbidden, ""));

            await Assert.ThrowsAsync<HttpRequestException>(() => new ConnectorCostPayloadSource().ReadAsync(Query(new Dictionary<string, string>
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = "https://billing.example.test/costs",
                [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token",
            }, egress)));
        }

        [Fact]
        public async Task Read_WithoutTheHostsEgress_IsAConfigurationError_and_opens_no_connection()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ConnectorCostPayloadSource().ReadAsync(Query(new Dictionary<string, string>
            {
                [ConnectorCostPayloadSource.EndpointSettingKey] = "https://billing.example.test/costs",
                [ConnectorCostPayloadSource.CredentialSettingKey] = "vault-token",
            }, egress: null)));

            Assert.Contains("egress", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The cases of <c>CloudCostProviderRegistryTests</c>, by name; its two <c>AddCloudUsage_*</c> cases
    /// compose the module's OWN startup, the way the host does.
    /// </summary>
    public sealed class CloudCostProviderRegistryTests
    {
        private static CloudCostProviderRegistry CreateRegistry()
        {
            var source = new StaticCloudCostPayloadSource("{}");
            var normalizer = new CloudCostNormalizer();
            return new CloudCostProviderRegistry(new ICloudCostProvider[] { new AzureCostProvider(source, normalizer), new AwsCostProvider(source, normalizer), new GcpCostProvider(source, normalizer) });
        }

        [Theory]
        [InlineData(CloudProvider.Azure, typeof(AzureCostProvider))]
        [InlineData(CloudProvider.Aws, typeof(AwsCostProvider))]
        [InlineData(CloudProvider.Gcp, typeof(GcpCostProvider))]
        public void Resolve_ReturnsTheRegisteredAdapter(CloudProvider provider, Type expected)
        {
            var registry = CreateRegistry();

            Assert.IsType(expected, registry.Resolve(provider));
            Assert.True(registry.TryResolve(provider, out var resolved));
            Assert.IsType(expected, resolved);
        }

        [Fact]
        public void Resolve_ThrowsForUnregisteredProvider()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => CreateRegistry().Resolve(CloudProvider.Datadog));

            Assert.Contains("Datadog", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TryResolve_ReturnsFalseForUnregisteredProvider()
        {
            Assert.False(CreateRegistry().TryResolve(CloudProvider.Salesforce, out var resolved));
            Assert.Null(resolved);
        }

        [Fact]
        public async Task AddCloudUsage_RegistersTheCloudAndSaasAdaptersAndRegistry()
        {
            await using var provider = Compose();
            var registry = provider.GetRequiredService<ICloudCostProviderRegistry>();

            Assert.IsType<AzureCostProvider>(registry.Resolve(CloudProvider.Azure));
            Assert.IsType<AwsCostProvider>(registry.Resolve(CloudProvider.Aws));
            Assert.IsType<GcpCostProvider>(registry.Resolve(CloudProvider.Gcp));
            foreach (var saas in new[] { CloudProvider.Datadog, CloudProvider.Snowflake, CloudProvider.GitHub, CloudProvider.Microsoft365, CloudProvider.Salesforce, CloudProvider.GenericRest })
            {
                Assert.Equal(saas, Assert.IsType<SaasCostProvider>(registry.Resolve(saas)).Provider);
            }
        }

        [Fact]
        public async Task AddCloudUsage_DefaultPayloadSourceIsConnectorBacked_AndFailsWithoutTransportSettings()
        {
            await using var provider = Compose();
            var azure = provider.GetRequiredService<ICloudCostProviderRegistry>().Resolve(CloudProvider.Azure);

            // The connection-backed source needs the endpoint and credential in the query settings (and the host's
            // egress); a query without them fails with a clear configuration error instead of egressing anywhere.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => azure.FetchCostsAsync(CloudCostQueries.July2026));

            Assert.Contains("endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<ConnectorCostPayloadSource>(provider.GetRequiredService<ICloudCostPayloadSource>());
        }

        private static ServiceProvider Compose()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = Path.Combine(Path.GetTempPath(), $"stomme-cloudusage-registry-{Guid.NewGuid():N}"),
                })
                .Build());
            new Stomme.Modules.CloudUsage.Startup.ServerStartup().ConfigureServices(services);
            return services.BuildServiceProvider();
        }
    }
}
