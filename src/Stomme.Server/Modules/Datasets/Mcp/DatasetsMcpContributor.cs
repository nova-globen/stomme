#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.FeatureManagement.Application;
using Stomme.Mcp;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Application;

namespace Stomme.Modules.Datasets.Mcp
{
    /// <summary>The Datasets module's feature flags, declared to the catalog by discovery (a public parameterless constructor).</summary>
    public sealed class DatasetsFeatureContributor : IFeatureContributor
    {
        /// <summary>Exposing datasets as MCP resources and the <c>query_dataset</c> tool. DEFAULT OFF (D110 Q6; the predecessor platform shipped it on).</summary>
        public const string Mcp = "datasets.mcp";

        public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
        {
            new FeatureDescriptor(Mcp, "Expose the site's datasets as MCP resources and the query_dataset tool.", "MCP", DefaultEnabled: false),
        };
    }

    /// <summary>
    /// The datasets' MCP surface (BL-94 increment 12, over BL-80's contributor seam): two resources - the site's datasets
    /// and one dataset's classification-masked summary - and one tool, <c>query_dataset</c>, which returns the bounded,
    /// classification-gated preview and nothing else. Every gate names BOTH the feature flag (<c>datasets.mcp</c>, off
    /// by default) and a key: the resources <c>datasets:view</c>, the tool <c>datasets:preview</c> (raw rows are the
    /// higher privilege). Every read goes through the module's mediator messages, so the D99 keys decide a second time
    /// behind the MCP gates, and the catalog's site scoping makes another site's id not found. The tool is
    /// <see cref="McpToolApproval.Auto"/>: it changes nothing (Q6).
    /// </summary>
    public sealed class DatasetsMcpContributor(IMediator mediator) : IMcpContributor, IMcpResourceReader
    {
        public const string ListUri = "stomme://datasets";
        public const string SummaryTemplate = "stomme://datasets/{datasetId}/summary";
        public const string QueryDataset = "query_dataset";

        private static readonly IReadOnlyList<McpAccessGate> ViewGate = new[] { new McpAccessGate(DatasetsFeatureContributor.Mcp, DatasetPermissions.View) };
        private static readonly IReadOnlyList<McpAccessGate> PreviewGate = new[] { new McpAccessGate(DatasetsFeatureContributor.Mcp, DatasetPermissions.Preview) };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string ContributorId => "stomme.datasets";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(ListUri, "Datasets", "The site's datasets: name, origin, classification, schema and the classification-masked summary - metadata only, never rows.", "application/json", ViewGate),
            new McpResourceDescriptor(SummaryTemplate, "Dataset summary", "One dataset by id: its schema and its classification-masked summary.", "application/json", ViewGate),
        };

        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
        {
            new McpToolDescriptor(QueryDataset, "The bounded preview rows of one dataset - refused (allowed: false, no rows) for Confidential and Restricted data.",
                """{"type":"object","properties":{"datasetId":{"type":"string","description":"The dataset's id."}},"required":["datasetId"]}""", PreviewGate, McpToolApproval.Auto),
        };

        public IMcpResourceReader? ReaderFor(string uriTemplate) => uriTemplate is ListUri or SummaryTemplate ? this : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => toolName == QueryDataset
            ? new Invoker(async (arguments, ct) =>
            {
                if (!TryDatasetId(arguments, out var datasetId))
                {
                    return Error("The arguments need a 'datasetId' that is a GUID.");
                }

                if (await mediator.Send(new GetDatasetQuery(datasetId), ct) is null)
                {
                    return Error($"No dataset '{datasetId}' on this site.");
                }

                var preview = await mediator.Send(new PreviewDatasetRowsQuery(datasetId), ct);
                return Ok(new { datasetId, allowed = preview.Allowed, reason = preview.Reason, rows = JsonSerializer.Deserialize<JsonElement>(preview.RowsJson) });
            })
            : null;

        public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            if (string.Equals(uri, ListUri, StringComparison.Ordinal))
            {
                var datasets = await mediator.Send(new ListDatasetsQuery(), ct);
                return Content(uri, datasets.Select(Summary).ToList());
            }

            if (arguments.TryGetValue("datasetId", out var raw) && Guid.TryParse(raw, out var datasetId))
            {
                var dataset = await mediator.Send(new GetDatasetQuery(datasetId), ct);
                return dataset is null ? null : Content(uri, Summary(dataset));
            }

            return null;
        }

        /// <summary>What a resource carries: the descriptor's metadata - the summary is already masked by the catalog for a sensitive dataset; the sample rows never travel here.</summary>
        private static object Summary(DatasetDescriptor dataset) => new
        {
            id = dataset.Id,
            name = dataset.Name,
            originKind = dataset.OriginKind.ToString(),
            originReference = dataset.OriginReference,
            classification = dataset.Classification.ToString(),
            isSensitive = dataset.IsSensitive,
            rowCountEstimate = dataset.Summary.RowCountEstimate,
            lastRefreshedUtc = dataset.LastRefreshedUtc,
            schema = new { columns = dataset.Schema.Columns.Select(column => new { name = column.Name, dataType = column.DataType.ToString(), semanticType = column.SemanticType, formatKey = column.FormatKey }) },
            summary = new { columns = dataset.Summary.Columns.Select(column => new { name = column.Name, samples = column.Samples, min = column.Min, max = column.Max, uniqueCount = column.UniqueCount, nullCount = column.NullCount }) },
        };

        private static bool TryDatasetId(IReadOnlyDictionary<string, JsonElement>? arguments, out Guid datasetId)
        {
            datasetId = Guid.Empty;
            return arguments is not null && arguments.TryGetValue("datasetId", out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out datasetId);
        }

        private static McpResourceContent Content(string uri, object payload) => new(uri, "application/json", JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Ok(object payload) => new(JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Json), isError: true);

        private sealed class Invoker(Func<IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<McpToolResult>> invoke) : IMcpToolInvoker
        {
            public async Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
            {
                IReadOnlyDictionary<string, JsonElement>? arguments = null;
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    try
                    {
                        arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
                    }
                    catch (JsonException)
                    {
                        return Error("The arguments must be a JSON object.");
                    }
                }

                try
                {
                    return await invoke(arguments, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Error(ex.Message);
                }
            }
        }
    }
}
