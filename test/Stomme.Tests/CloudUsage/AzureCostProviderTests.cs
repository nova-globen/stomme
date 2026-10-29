#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Xunit;

// Cases carried over unchanged: the fixtures and assertions are the same.
namespace Stomme.Tests.CloudUsage
{
    public sealed class AzureCostProviderTests
    {
        /// <summary>Captured-style Azure Cost Management query-result payload (columns + rows, with tag columns).</summary>
        private const string SamplePayload = """
            {
              "id": "subscriptions/s1/providers/Microsoft.CostManagement/query/q1",
              "type": "Microsoft.CostManagement/query",
              "properties": {
                "columns": [
                  { "name": "PreTaxCost", "type": "Number" },
                  { "name": "UsageDate", "type": "Number" },
                  { "name": "ServiceName", "type": "String" },
                  { "name": "ResourceId", "type": "String" },
                  { "name": "Currency", "type": "String" },
                  { "name": "department", "type": "String" },
                  { "name": "project", "type": "String" },
                  { "name": "environment", "type": "String" }
                ],
                "rows": [
                  [12.34, 20260701, "Virtual Machines", "/subscriptions/s1/resourceGroups/rg-orders/providers/Microsoft.Compute/virtualMachines/vm-orders-01", "USD", "Engineering", "orders", "prod"],
                  [3.5, 20260701, "Storage Accounts", "/subscriptions/s1/resourceGroups/rg-orders/providers/Microsoft.Storage/storageAccounts/ststomme", "usd", "", "", ""],
                  [0.75, 20260702, "", "/subscriptions/s1/resourceGroups/rg-orders", "USD", "", "", ""],
                  ["not-a-number", 20260702, "Azure Front Door", "/subscriptions/s1", "USD", "", "", ""]
                ]
              }
            }
            """;

        private static AzureCostProvider CreateProvider(string payload) =>
            new(new StaticCloudCostPayloadSource(payload), new CloudCostNormalizer());

        [Fact]
        public void Provider_IsAzure()
        {
            Assert.Equal(CloudProvider.Azure, CreateProvider(SamplePayload).Provider);
        }

        [Fact]
        public async Task FetchCostsAsync_MapsQueryResultRowsToConformantEntries()
        {
            var entries = await CreateProvider(SamplePayload).FetchCostsAsync(CloudCostQueries.July2026);

            Assert.Equal(2, entries.Count);

            var compute = entries[0];
            Assert.Equal(new DateOnly(2026, 7, 1), compute.Date);
            Assert.Equal(CloudProvider.Azure, compute.Provider);
            Assert.Equal(CloudServiceCategory.Compute, compute.ServiceCategory);
            Assert.Equal("Virtual Machines", compute.Service);
            Assert.Equal(
                "/subscriptions/s1/resourceGroups/rg-orders/providers/Microsoft.Compute/virtualMachines/vm-orders-01",
                compute.Resource);
            Assert.Equal(12.34m, compute.Cost);
            Assert.Equal("USD", compute.Currency);
            Assert.Equal("Engineering", compute.Department);
            Assert.Equal("orders", compute.Project);
            Assert.Equal("prod", compute.Environment);

            var storage = entries[1];
            Assert.Equal(CloudServiceCategory.Storage, storage.ServiceCategory);
            Assert.Equal("Storage Accounts", storage.Service);
            Assert.Equal("USD", storage.Currency);
            Assert.Null(storage.Department);
            Assert.Null(storage.Project);
            Assert.Null(storage.Environment);
        }

        [Fact]
        public async Task FetchCostsAsync_SkipsRowsWithoutUsableData()
        {
            // The sample carries a blank-service row and a non-numeric-cost row; both are skipped, none throw.
            var entries = await CreateProvider(SamplePayload).FetchCostsAsync(CloudCostQueries.July2026);

            Assert.DoesNotContain(entries, entry => entry.Service.Length == 0);
            Assert.DoesNotContain(entries, entry => entry.Service == "Azure Front Door");
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("[]")]
        [InlineData("""{ "properties": { "rows": [] } }""")]
        [InlineData("""{ "properties": { "columns": [ { "name": "ServiceName" } ], "rows": [] } }""")]
        public async Task FetchCostsAsync_ThrowsOnStructurallyInvalidPayload(string payload)
        {
            await Assert.ThrowsAsync<CloudCostPayloadFormatException>(
                () => CreateProvider(payload).FetchCostsAsync(CloudCostQueries.July2026));
        }
    }
}
