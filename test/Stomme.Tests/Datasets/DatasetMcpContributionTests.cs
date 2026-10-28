#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Mcp;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// BL-94 increment 12 (D110 Q6) - the predecessor platform's DatasetMcpContributionTests (6) by name on the one <c>IMcpContributor</c>
    /// over the module's real startup on the File store: the descriptors carry the feature flag AND the key, the list
    /// and summary resources resolve to the classification-masked summary with no sample values, unknown or malformed
    /// ids are null, <c>query_dataset</c> returns the bounded preview and the refusal for BOTH sensitive levels, and the
    /// expected failures are error results.
    /// </summary>
    public sealed class DatasetMcpContributionTests
    {
        internal static Task<IMcpContributor> ContributorAsync(DatasetsApplicationHarness harness) =>
            harness.InScopeAsync(sp => Task.FromResult(sp.GetServices<IMcpContributor>().Single(contributor => contributor.ContributorId == "stomme.datasets")));

        internal static Task<McpResourceContent?> ReadAsync(DatasetsApplicationHarness harness, string template, string uri, IReadOnlyDictionary<string, string> arguments) =>
            harness.InScopeAsync(async sp =>
            {
                var contributor = sp.GetServices<IMcpContributor>().Single(candidate => candidate.ContributorId == "stomme.datasets");
                return await contributor.ReaderFor(template)!.ReadAsync(uri, arguments);
            });

        internal static Task<McpToolResult> InvokeAsync(DatasetsApplicationHarness harness, string argumentsJson) =>
            harness.InScopeAsync(async sp =>
            {
                var contributor = sp.GetServices<IMcpContributor>().Single(candidate => candidate.ContributorId == "stomme.datasets");
                return await contributor.InvokerFor(DatasetsMcpContributor.QueryDataset)!.InvokeAsync(argumentsJson);
            });

        [Fact]
        public async Task Descriptors_CarryTheDatasetPermissionAndFeatureFlagGates()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var contributor = await ContributorAsync(harness);

            Assert.Equal(new[] { DatasetsMcpContributor.ListUri, DatasetsMcpContributor.SummaryTemplate }, contributor.Resources.Select(resource => resource.Uri));
            Assert.All(contributor.Resources, resource =>
            {
                var gate = Assert.Single(resource.Requires);
                Assert.Equal((DatasetsFeatureContributor.Mcp, DatasetPermissions.View), (gate.Feature, gate.Permission));
            });

            var tool = Assert.Single(contributor.Tools);
            Assert.Equal(DatasetsMcpContributor.QueryDataset, tool.Name);
            var toolGate = Assert.Single(tool.Requires);
            Assert.Equal((DatasetsFeatureContributor.Mcp, DatasetPermissions.Preview), (toolGate.Feature, toolGate.Permission)); // raw rows are the higher privilege
            Assert.Equal(McpToolApproval.Auto, tool.Approval);
            Assert.Null(contributor.ReaderFor("stomme://elsewhere"));
            Assert.Null(contributor.InvokerFor("delete_dataset"));
        }

        [Fact]
        public async Task ListResource_ProjectsEveryDataset_WithMaskedSummaryMetadataOnly()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var salesId = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"amount\": \"10\" } ]"));
            await harness.SendAsync(new RegisterDatasetCommand("Secret", OriginKind.Upload, "file-2", "[ { \"ssn\": \"123\" } ]", DataClassification.Confidential));

            var content = await ReadAsync(harness, DatasetsMcpContributor.ListUri, DatasetsMcpContributor.ListUri, new Dictionary<string, string>());

            Assert.NotNull(content);
            Assert.Equal("application/json", content.MimeType);
            using var json = JsonDocument.Parse(content.Text);
            var byName = json.RootElement.EnumerateArray().ToDictionary(dataset => dataset.GetProperty("name").GetString()!);
            Assert.Equal(new[] { "Sales", "Secret" }, byName.Keys.Order());
            Assert.Equal(salesId, byName["Sales"].GetProperty("id").GetGuid());
            Assert.Equal("Confidential", byName["Secret"].GetProperty("classification").GetString());
            Assert.True(byName["Sales"].GetProperty("rowCountEstimate").GetInt32() > 0);
            Assert.DoesNotContain("123", content.Text); // metadata only - never a sensitive sample value
            Assert.DoesNotContain("rowsJson", content.Text);
        }

        [Fact]
        public async Task SummaryResource_ResolvesTheClassificationMaskedDataSummary()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Secret", OriginKind.Upload, null, "[ { \"ssn\": \"123\" } ]", DataClassification.Confidential));

            var content = await ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, $"stomme://datasets/{id}/summary", new Dictionary<string, string> { ["datasetId"] = id.ToString() });

            Assert.NotNull(content);
            using var json = JsonDocument.Parse(content.Text);
            Assert.Equal("Confidential", json.RootElement.GetProperty("classification").GetString());
            Assert.Contains(json.RootElement.GetProperty("schema").GetProperty("columns").EnumerateArray(), column => column.GetProperty("name").GetString() == "ssn");
            var summaryColumns = json.RootElement.GetProperty("summary").GetProperty("columns").EnumerateArray().ToList();
            Assert.NotEmpty(summaryColumns);
            Assert.All(summaryColumns, column => Assert.Empty(column.GetProperty("samples").EnumerateArray()));
            Assert.DoesNotContain("123", content.Text);
        }

        [Fact]
        public async Task SummaryResource_ReturnsNull_ForUnknownOrMalformedIds()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();

            Assert.Null(await ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, $"stomme://datasets/{Guid.NewGuid()}/summary", new Dictionary<string, string> { ["datasetId"] = Guid.NewGuid().ToString() }));
            Assert.Null(await ReadAsync(harness, DatasetsMcpContributor.SummaryTemplate, "stomme://datasets/not-a-guid/summary", new Dictionary<string, string> { ["datasetId"] = "not-a-guid" }));
        }

        [Fact]
        public async Task QueryDataset_ReturnsTheBoundedPreview_AndTheConfidentialRefusal()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var salesId = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"amount\": \"10\" } ]"));
            var secretId = await harness.SendAsync(new RegisterDatasetCommand("Secret", OriginKind.Upload, "file-2", "[ { \"ssn\": \"123\" } ]", DataClassification.Confidential));

            var allowed = await InvokeAsync(harness, $$"""{"datasetId":"{{salesId}}"}""");
            Assert.False(allowed.IsError);
            using (var json = JsonDocument.Parse(allowed.ContentJson))
            {
                Assert.True(json.RootElement.GetProperty("allowed").GetBoolean());
                Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray()); // the stored bounded sample
            }

            var refused = await InvokeAsync(harness, $$"""{"datasetId":"{{secretId}}"}""");
            Assert.False(refused.IsError);
            using (var json = JsonDocument.Parse(refused.ContentJson))
            {
                Assert.False(json.RootElement.GetProperty("allowed").GetBoolean());
                Assert.Empty(json.RootElement.GetProperty("rows").EnumerateArray());
            }

            Assert.DoesNotContain("123", refused.ContentJson);
        }

        [Fact]
        public async Task QueryDataset_ReportsExpectedFailures_AsErrorResults()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();

            Assert.True((await InvokeAsync(harness, $$"""{"datasetId":"{{Guid.NewGuid()}}"}""")).IsError); // unknown id
            Assert.True((await InvokeAsync(harness, """{"datasetId":"not-a-guid"}""")).IsError);
            Assert.True((await InvokeAsync(harness, "not json")).IsError);
            Assert.True((await InvokeAsync(harness, "{}")).IsError);
        }
    }
}
