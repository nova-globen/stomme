#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Xunit;

// the predecessor platform's cases by name; the fixtures and assertions are unchanged.
namespace Stomme.Tests.CloudUsage
{
    public sealed class SaasCostProviderTests
    {
        /// <summary>Generic cost-row payload as a SaaS billing source (here: Datadog) would supply it.</summary>
        private const string SamplePayload = """
            [
              {
                "date": "2026-07-01",
                "service": "Infrastructure Hosts",
                "resource": "predecessor-org",
                "cost": 250.0,
                "currency": "USD",
                "tags": { "department": "Platform", "environment": "prod" }
              },
              {
                "date": "2026-07-01",
                "service": "",
                "cost": 1.0,
                "currency": "USD"
              }
            ]
            """;

        private static SaasCostProvider CreateProvider(CloudProvider provider, string payload) =>
            new(provider, new StaticCloudCostPayloadSource(payload), new CloudCostNormalizer());

        [Fact]
        public async Task FetchCostsAsync_MapsGenericRowsBehindTheSameContract()
        {
            var entries = await CreateProvider(CloudProvider.Datadog, SamplePayload)
                .FetchCostsAsync(CloudCostQueries.July2026);

            // The blank-service row is skipped.
            var entry = Assert.Single(entries);
            Assert.Equal(new DateOnly(2026, 7, 1), entry.Date);
            Assert.Equal(CloudProvider.Datadog, entry.Provider);
            Assert.Equal(CloudServiceCategory.Monitoring, entry.ServiceCategory);
            Assert.Equal("Infrastructure Hosts", entry.Service);
            Assert.Equal("predecessor-org", entry.Resource);
            Assert.Equal(250.0m, entry.Cost);
            Assert.Equal("USD", entry.Currency);
            Assert.Equal("Platform", entry.Department);
            Assert.Equal("prod", entry.Environment);
            Assert.Null(entry.Project);
        }

        [Fact]
        public async Task FetchCostsAsync_SupportsGenericRestAsTheExtensionPath()
        {
            const string payload = """
                [ { "date": "2026-07-03", "service": "Custom Metering", "cost": 9.99, "currency": "EUR" } ]
                """;

            var entries = await CreateProvider(CloudProvider.GenericRest, payload)
                .FetchCostsAsync(CloudCostQueries.July2026);

            var entry = Assert.Single(entries);
            Assert.Equal(CloudProvider.GenericRest, entry.Provider);
            Assert.Equal(CloudServiceCategory.Other, entry.ServiceCategory);
            Assert.Equal("EUR", entry.Currency);
            Assert.Equal(string.Empty, entry.Resource);
        }

        [Theory]
        [InlineData(CloudProvider.Azure)]
        [InlineData(CloudProvider.Aws)]
        [InlineData(CloudProvider.Gcp)]
        [InlineData((CloudProvider)0)]
        public void Constructor_RejectsInfrastructureCloudsAndUndefinedProviders(CloudProvider provider)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProvider(provider, SamplePayload));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("""{ "rows": [] }""")]
        public async Task FetchCostsAsync_ThrowsOnStructurallyInvalidPayload(string payload)
        {
            await Assert.ThrowsAsync<CloudCostPayloadFormatException>(
                () => CreateProvider(CloudProvider.Snowflake, payload).FetchCostsAsync(CloudCostQueries.July2026));
        }
    }
}
