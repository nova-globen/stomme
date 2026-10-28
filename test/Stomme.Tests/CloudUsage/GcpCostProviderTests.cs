#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Xunit;

// the predecessor platform's cases by name (BL-97 increment 3); the fixtures and assertions are unchanged.
namespace Stomme.Tests.CloudUsage
{
    public sealed class GcpCostProviderTests
    {
        /// <summary>Captured-style GCP BigQuery billing-export payload (service/cost/currency/labels rows).</summary>
        private const string SamplePayload = """
            {
              "rows": [
                {
                  "service": { "id": "6F81-5844-456A", "description": "Compute Engine" },
                  "cost": 18.75,
                  "currency": "USD",
                  "usage_start_time": "2026-07-01T00:00:00Z",
                  "project": { "id": "acme-prod", "name": "Acme Production" },
                  "resource": { "name": "instances/vm-ingest-01" },
                  "labels": [
                    { "key": "department", "value": "Platform" },
                    { "key": "env", "value": "prod" }
                  ]
                },
                {
                  "service": { "description": "BigQuery" },
                  "cost": "4.20",
                  "currency": "usd",
                  "usage_start_time": "2026-07-02T00:00:00Z",
                  "project": { "id": "predecessor-analytics" },
                  "labels": []
                },
                {
                  "service": { "description": "" },
                  "cost": 1.0,
                  "currency": "USD",
                  "usage_start_time": "2026-07-01T00:00:00Z"
                }
              ]
            }
            """;

        private static GcpCostProvider CreateProvider(string payload) =>
            new(new StaticCloudCostPayloadSource(payload), new CloudCostNormalizer());

        [Fact]
        public void Provider_IsGcp()
        {
            Assert.Equal(CloudProvider.Gcp, CreateProvider(SamplePayload).Provider);
        }

        [Fact]
        public async Task FetchCostsAsync_MapsBillingExportRowsToConformantEntries()
        {
            var entries = await CreateProvider(SamplePayload).FetchCostsAsync(CloudCostQueries.July2026);

            // The blank-service row is skipped.
            Assert.Equal(2, entries.Count);

            var compute = entries[0];
            Assert.Equal(new DateOnly(2026, 7, 1), compute.Date);
            Assert.Equal(CloudProvider.Gcp, compute.Provider);
            Assert.Equal(CloudServiceCategory.Compute, compute.ServiceCategory);
            Assert.Equal("Compute Engine", compute.Service);
            Assert.Equal("instances/vm-ingest-01", compute.Resource);
            Assert.Equal(18.75m, compute.Cost);
            Assert.Equal("USD", compute.Currency);
            Assert.Equal("Platform", compute.Department);
            Assert.Equal("acme-prod", compute.Project);
            Assert.Equal("prod", compute.Environment);

            var analytics = entries[1];
            Assert.Equal(new DateOnly(2026, 7, 2), analytics.Date);
            Assert.Equal(CloudServiceCategory.Analytics, analytics.ServiceCategory);
            Assert.Equal("BigQuery", analytics.Service);
            Assert.Equal(string.Empty, analytics.Resource);
            Assert.Equal(4.20m, analytics.Cost);
            Assert.Equal("USD", analytics.Currency);
            Assert.Equal("predecessor-analytics", analytics.Project);
            Assert.Null(analytics.Department);
            Assert.Null(analytics.Environment);
        }

        [Fact]
        public async Task FetchCostsAsync_PrefersProjectLabelOverProjectId()
        {
            const string payload = """
                {
                  "rows": [
                    {
                      "service": { "description": "Cloud Run" },
                      "cost": 2.5,
                      "currency": "USD",
                      "usage_start_time": "2026-07-01T00:00:00Z",
                      "project": { "id": "acme-prod" },
                      "labels": [ { "key": "project", "value": "orders" } ]
                    }
                  ]
                }
                """;

            var entries = await CreateProvider(payload).FetchCostsAsync(CloudCostQueries.July2026);

            Assert.Equal("orders", Assert.Single(entries).Project);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("""{ "data": [] }""")]
        [InlineData("""{ "rows": { "cost": 1 } }""")]
        public async Task FetchCostsAsync_ThrowsOnStructurallyInvalidPayload(string payload)
        {
            await Assert.ThrowsAsync<CloudCostPayloadFormatException>(
                () => CreateProvider(payload).FetchCostsAsync(CloudCostQueries.July2026));
        }
    }
}
