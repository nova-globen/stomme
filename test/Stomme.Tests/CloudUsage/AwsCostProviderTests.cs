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
    public sealed class AwsCostProviderTests
    {
        /// <summary>Captured-style AWS Cost Explorer GetCostAndUsage payload (grouped by service + tag).</summary>
        private const string SamplePayload = """
            {
              "GroupDefinitions": [
                { "Type": "DIMENSION", "Key": "SERVICE" },
                { "Type": "TAG", "Key": "environment" }
              ],
              "ResultsByTime": [
                {
                  "TimePeriod": { "Start": "2026-07-01", "End": "2026-07-02" },
                  "Estimated": false,
                  "Groups": [
                    {
                      "Keys": ["Amazon Elastic Compute Cloud - Compute", "environment$prod"],
                      "Metrics": { "UnblendedCost": { "Amount": "42.5", "Unit": "USD" } }
                    },
                    {
                      "Keys": ["AmazonS3", "environment$"],
                      "Metrics": { "UnblendedCost": { "Amount": "1.25", "Unit": "USD" } }
                    },
                    {
                      "Keys": [""],
                      "Metrics": { "UnblendedCost": { "Amount": "9.99", "Unit": "USD" } }
                    }
                  ]
                },
                {
                  "TimePeriod": { "Start": "2026-07-02", "End": "2026-07-03" },
                  "Estimated": true,
                  "Groups": [
                    {
                      "Keys": ["AWS Lambda", "project$ingest"],
                      "Metrics": { "UnblendedCost": { "Amount": "-0.10", "Unit": "USD" } }
                    }
                  ]
                }
              ]
            }
            """;

        private static AwsCostProvider CreateProvider(string payload) =>
            new(new StaticCloudCostPayloadSource(payload), new CloudCostNormalizer());

        [Fact]
        public void Provider_IsAws()
        {
            Assert.Equal(CloudProvider.Aws, CreateProvider(SamplePayload).Provider);
        }

        [Fact]
        public async Task FetchCostsAsync_MapsGroupedResultsToConformantEntries()
        {
            var entries = await CreateProvider(SamplePayload).FetchCostsAsync(CloudCostQueries.July2026);

            // The blank-service group is skipped.
            Assert.Equal(3, entries.Count);

            var compute = entries[0];
            Assert.Equal(new DateOnly(2026, 7, 1), compute.Date);
            Assert.Equal(CloudProvider.Aws, compute.Provider);
            Assert.Equal(CloudServiceCategory.Compute, compute.ServiceCategory);
            Assert.Equal("Amazon Elastic Compute Cloud - Compute", compute.Service);
            Assert.Equal(string.Empty, compute.Resource);
            Assert.Equal(42.5m, compute.Cost);
            Assert.Equal("USD", compute.Currency);
            Assert.Equal("prod", compute.Environment);
            Assert.Null(compute.Department);
            Assert.Null(compute.Project);

            var storage = entries[1];
            Assert.Equal(CloudServiceCategory.Storage, storage.ServiceCategory);
            Assert.Equal("AmazonS3", storage.Service);
            Assert.Equal(1.25m, storage.Cost);
            Assert.Null(storage.Environment);

            var lambda = entries[2];
            Assert.Equal(new DateOnly(2026, 7, 2), lambda.Date);
            Assert.Equal(CloudServiceCategory.Compute, lambda.ServiceCategory);
            Assert.Equal(-0.10m, lambda.Cost);
            Assert.Equal("ingest", lambda.Project);
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("[]")]
        [InlineData("""{ "Results": [] }""")]
        [InlineData("""{ "ResultsByTime": { "TimePeriod": {} } }""")]
        public async Task FetchCostsAsync_ThrowsOnStructurallyInvalidPayload(string payload)
        {
            await Assert.ThrowsAsync<CloudCostPayloadFormatException>(
                () => CreateProvider(payload).FetchCostsAsync(CloudCostQueries.July2026));
        }

        [Fact]
        public async Task FetchCostsAsync_SkipsBlocksAndGroupsWithoutUsableData()
        {
            const string payload = """
                {
                  "ResultsByTime": [
                    { "TimePeriod": { "Start": "not-a-date" }, "Groups": [] },
                    {
                      "TimePeriod": { "Start": "2026-07-01", "End": "2026-07-02" },
                      "Groups": [
                        { "Keys": ["Amazon EC2"], "Metrics": { "UnblendedCost": { "Amount": "oops", "Unit": "USD" } } }
                      ]
                    }
                  ]
                }
                """;

            var entries = await CreateProvider(payload).FetchCostsAsync(CloudCostQueries.July2026);

            Assert.Empty(entries);
        }
    }
}
