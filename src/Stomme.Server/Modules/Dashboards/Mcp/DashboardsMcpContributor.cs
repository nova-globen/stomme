#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.FeatureManagement.Application;
using Stomme.Mcp;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Application;

namespace Stomme.Modules.Dashboards.Mcp
{
    /// <summary>The Dashboards module's feature flags, declared to the catalog by discovery (a public parameterless constructor).</summary>
    public sealed class DashboardsFeatureContributor : IFeatureContributor
    {
        /// <summary>Exposing dashboards as MCP resources and the <c>get_widget_data</c> tool. DEFAULT OFF, like <c>datasets.mcp</c> (D110 Q6).</summary>
        public const string Mcp = "dashboards.mcp";

        public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
        {
            new FeatureDescriptor(Mcp, "Expose the site's dashboards as MCP resources and the get_widget_data tool.", "MCP", DefaultEnabled: false),
        };
    }

    /// <summary>
    /// The dashboards' MCP surface (over BL-80's contributor seam), READ-ONLY: two resources - the
    /// site's dashboards and one dashboard with its widgets - and one tool, <c>get_widget_data</c>, which answers what
    /// the page's widget shows: the bound dataset's classification-masked summary and the bounded rows only when the
    /// classification allows them. Every gate names BOTH the feature flag (<c>dashboards.mcp</c>, off by default) and
    /// the <c>dashboards:view</c> key; every read goes through the module's mediator messages, so the key decides a
    /// second time behind the MCP gate, the board is resolved through the request's site (another site's id is not
    /// found), and the rows pass the Datasets classification gate as they do for the page. The tool is
    /// <see cref="McpToolApproval.Auto"/>: it changes nothing.
    /// </summary>
    public sealed class DashboardsMcpContributor(IMediator mediator) : IMcpContributor, IMcpResourceReader
    {
        public const string ListUri = "stomme://dashboards";
        public const string DashboardTemplate = "stomme://dashboards/{dashboardId}";
        public const string GetWidgetData = "get_widget_data";

        private static readonly IReadOnlyList<McpAccessGate> ViewGate = new[] { new McpAccessGate(DashboardsFeatureContributor.Mcp, DashboardPermissions.View) };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string ContributorId => "stomme.dashboards";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(ListUri, "Dashboards", "The site's dashboards: name, status, layout and each widget's kind, bound dataset and columns - definitions only, never data.", "application/json", ViewGate),
            new McpResourceDescriptor(DashboardTemplate, "Dashboard", "One dashboard by id with its widgets.", "application/json", ViewGate),
        };

        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
        {
            new McpToolDescriptor(GetWidgetData, "What one widget shows: the bound dataset's classification-masked summary and the bounded rows - refused (rowsAllowed: false, no rows) for Confidential and Restricted data.",
                """{"type":"object","properties":{"dashboardId":{"type":"string","description":"The dashboard's id."},"widgetId":{"type":"string","description":"The widget's id."}},"required":["dashboardId","widgetId"]}""", ViewGate, McpToolApproval.Auto),
        };

        public IMcpResourceReader? ReaderFor(string uriTemplate) => uriTemplate is ListUri or DashboardTemplate ? this : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => toolName == GetWidgetData
            ? new Invoker(async (arguments, ct) =>
            {
                if (!TryGuid(arguments, "dashboardId", out var dashboardId) || !TryGuid(arguments, "widgetId", out var widgetId))
                {
                    return Error("The arguments need a 'dashboardId' and a 'widgetId' that are GUIDs.");
                }

                var data = await mediator.Send(new GetWidgetDataQuery(dashboardId, widgetId), ct);
                return Ok(new
                {
                    dashboardId,
                    widgetId,
                    datasetId = data.DatasetId,
                    datasetName = data.DatasetName,
                    classification = data.Classification.ToString(),
                    columns = data.Columns,
                    summary = new { rowCountEstimate = data.Summary.RowCountEstimate, columns = data.Summary.Columns.Select(column => new { name = column.Name, samples = column.Samples, min = column.Min, max = column.Max, uniqueCount = column.UniqueCount, nullCount = column.NullCount }) },
                    rowsAllowed = data.RowsAllowed,
                    rowsReason = data.RowsReason,
                    rows = JsonSerializer.Deserialize<JsonElement>(data.RowsAllowed ? data.RowsJson : "[]"),
                });
            })
            : null;

        public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            if (string.Equals(uri, ListUri, StringComparison.Ordinal))
            {
                var dashboards = await mediator.Send(new ListDashboardsQuery(), ct);
                return Content(uri, dashboards.Select(Definition).ToList());
            }

            if (arguments.TryGetValue("dashboardId", out var raw) && Guid.TryParse(raw, out var dashboardId))
            {
                var dashboard = await mediator.Send(new GetDashboardQuery(dashboardId), ct);
                return dashboard is null ? null : Content(uri, Definition(dashboard));
            }

            return null;
        }

        /// <summary>What a resource carries: the board's definition - never a widget's data, which is the tool's and passes the classification gate there.</summary>
        private static object Definition(DashboardView dashboard) => new
        {
            id = dashboard.Id,
            name = dashboard.Name,
            status = dashboard.Status.ToString(),
            columns = dashboard.Columns,
            rowHeightPx = dashboard.RowHeightPx,
            createdUtc = dashboard.CreatedUtc,
            updatedUtc = dashboard.UpdatedUtc,
            widgets = dashboard.Widgets.Select(widget => new
            {
                id = widget.Id,
                title = widget.Title,
                kind = widget.Kind.ToString(),
                datasetId = widget.DatasetId,
                columns = widget.Columns,
                position = new { column = widget.Column, row = widget.Row, columnSpan = widget.ColumnSpan, rowSpan = widget.RowSpan },
                refresh = widget.RefreshKind.ToString(),
                intervalSeconds = widget.IntervalSeconds,
            }),
        };

        private static bool TryGuid(IReadOnlyDictionary<string, JsonElement>? arguments, string name, out Guid value)
        {
            value = Guid.Empty;
            return arguments is not null && arguments.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out value);
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    return Error(ex.Message);
                }
            }
        }
    }
}
