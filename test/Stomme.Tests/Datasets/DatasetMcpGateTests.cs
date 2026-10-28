#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.FeatureManagement.Application;
using Stomme.Mcp;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Domain;
using Stomme.Modules.Datasets.Mcp;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>BL-94 increment 12 (D110 Q6) - the feature flag is declared by discovery and DEFAULT OFF, every gate names it, and the one tool changes nothing.</summary>
    public sealed class DatasetMcpFeatureDefaultTests
    {
        [Fact]
        public void The_flag_is_declared_by_a_discoverable_contributor_and_off_by_default()
        {
            var contributor = (IFeatureContributor)Activator.CreateInstance(typeof(DatasetsFeatureContributor))!; // the discovery shape: public, parameterless
            var flag = Assert.Single(contributor.Features);
            Assert.Equal((DatasetsFeatureContributor.Mcp, false, "MCP"), (flag.Key, flag.DefaultEnabled, flag.Category));
        }

        [Fact]
        public async Task Every_resource_and_the_tool_are_gated_on_the_flag_and_the_tool_is_auto_and_not_mutating()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var contributor = await DatasetMcpContributionTests.ContributorAsync(harness);

            Assert.All(contributor.Resources, resource => Assert.All(resource.Requires, gate => Assert.Equal(DatasetsFeatureContributor.Mcp, gate.Feature)));
            Assert.All(contributor.Tools, tool => Assert.All(tool.Requires, gate => Assert.Equal(DatasetsFeatureContributor.Mcp, gate.Feature)));
            var tool = Assert.Single(contributor.Tools);
            Assert.False(McpToolNames.IsMutating(tool.Name));
            Assert.True(McpToolNames.IsCanonical(tool.Name));
            Assert.Equal(McpToolApproval.Auto, tool.Approval);
        }
    }

    /// <summary>BL-94 increment 12 - a summary or a tool call for ANOTHER site's dataset id is not found: the catalog scopes every read by the request's site.</summary>
    public sealed class DatasetMcpSiteIsolationTests
    {
        [Fact]
        public async Task Another_sites_dataset_is_not_listed_not_summarised_and_not_queried()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var mine = await harness.SendAsync(new RegisterDatasetCommand("Mine", OriginKind.Upload, null, "[ { \"a\": \"1\" } ]"));
            var theirs = await harness.InScopeAsync(async sp =>
            {
                var inference = SchemaInference.Infer("[ { \"b\": \"2\" } ]");
                var dataset = DatasetAggregate.Register(TenantId.Default, 2, "Theirs", OriginKind.Upload, null, DataClassification.Public, inference.Schema, inference.Summary, inference.SampleRowsJson, DatasetsApplicationHarness.Start);
                await sp.GetRequiredService<IDatasetRepository>().AddAsync(dataset);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return dataset.Id;
            });

            var list = await DatasetMcpContributionTests.ReadAsync(harness, DatasetsMcpContributor.ListUri, DatasetsMcpContributor.ListUri, new Dictionary<string, string>());
            using var json = JsonDocument.Parse(list!.Text);
            Assert.Equal(new[] { mine }, json.RootElement.EnumerateArray().Select(dataset => dataset.GetProperty("id").GetGuid()));

            Assert.Null(await DatasetMcpContributionTests.ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, $"stomme://datasets/{theirs}/summary", new Dictionary<string, string> { ["datasetId"] = theirs.ToString() }));
            var call = await DatasetMcpContributionTests.InvokeAsync(harness, $$"""{"datasetId":"{{theirs}}"}""");
            Assert.True(call.IsError);
            Assert.DoesNotContain("\"2\"", call.ContentJson);
        }
    }

    /// <summary>BL-94 increment 12 (D110 Q1) - Confidential AND Restricted are refused by <c>query_dataset</c> and masked in the summary resource; Public and Internal are served.</summary>
    public sealed class DatasetMcpClassificationTests
    {
        [Theory]
        [InlineData(DataClassification.Confidential)]
        [InlineData(DataClassification.Restricted)]
        public async Task A_sensitive_dataset_is_refused_by_the_tool_and_masked_in_the_resource(DataClassification classification)
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            // A benign sample: the level under test is the one ASSERTED (a PII sample would be escalated to Restricted by the classifier, which is its own case).
            var id = await harness.SendAsync(new RegisterDatasetCommand("Secret " + classification, OriginKind.Upload, null, "[ { \"note\": \"secret-value-42\" } ]", classification));

            var call = await DatasetMcpContributionTests.InvokeAsync(harness, $$"""{"datasetId":"{{id}}"}""");
            Assert.False(call.IsError);
            using var result = JsonDocument.Parse(call.ContentJson);
            Assert.False(result.RootElement.GetProperty("allowed").GetBoolean());
            Assert.Empty(result.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Contains(classification.ToString(), result.RootElement.GetProperty("reason").GetString());
            Assert.DoesNotContain("secret-value-42", call.ContentJson);

            var summary = await DatasetMcpContributionTests.ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, $"stomme://datasets/{id}/summary", new Dictionary<string, string> { ["datasetId"] = id.ToString() });
            Assert.DoesNotContain("secret-value-42", summary!.Text);
            Assert.Contains("\"isSensitive\":true", summary.Text);
        }

        [Theory]
        [InlineData(DataClassification.Public)]
        [InlineData(DataClassification.Internal)]
        public async Task An_open_dataset_is_served_by_the_tool_and_the_resource(DataClassification classification)
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Open " + classification, OriginKind.Upload, null, "[ { \"sku\": \"A1\" } ]", classification));

            var call = await DatasetMcpContributionTests.InvokeAsync(harness, $$"""{"datasetId":"{{id}}"}""");
            using var result = JsonDocument.Parse(call.ContentJson);
            Assert.True(result.RootElement.GetProperty("allowed").GetBoolean());
            Assert.Equal("A1", result.RootElement.GetProperty("rows")[0].GetProperty("sku").GetString());

            var summary = await DatasetMcpContributionTests.ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, $"stomme://datasets/{id}/summary", new Dictionary<string, string> { ["datasetId"] = id.ToString() });
            Assert.Contains("\"A1\"", summary!.Text); // an open dataset's samples are published
        }
    }
}
