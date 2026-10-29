#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Export;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.DataExport.Sources;
using Stomme.Shared;

namespace Stomme.Modules.Dashboards.Infrastructure
{
    /// <summary>
    /// One grid widget as an export: key
    /// <c>dashboards.grid</c>, arguments <c>{"dashboardId": "...", "widgetId": "..."}</c>, one table named after the
    /// widget - what the widget's data query answers, never more: the board through the job's site, every bound column
    /// checked against the dataset's published schema, the rows only when the dataset's classification allows them.
    /// </summary>
    public sealed class DashboardGridExportSource(IDashboardRepository dashboards, SiteState siteState, IDatasetCatalog catalog) : IExportSource
    {
        public const string SourceKey = "dashboards.grid";

        private static readonly string[] Members = { "dashboardId", "widgetId" };

        public string Key => SourceKey;

        public string RequiredPermission => DashboardPermissions.View;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) => TryParse(context?.ArgumentsJson, out _, out _, out var error) ? null : error;

        public async Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryParse(context.ArgumentsJson, out var dashboardId, out var widgetId, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var board = await DashboardExportTables.BoardAsync(dashboards, siteState, dashboardId, ct);
            var widget = board.Widgets.SingleOrDefault(candidate => candidate.Id == widgetId)
                ?? throw new InvalidOperationException($"Grid widget '{widgetId}' on dashboard '{dashboardId}' does not exist.");
            if (widget.Kind != WidgetKind.Grid)
            {
                throw new InvalidOperationException($"Widget '{widgetId}' is a {widget.Kind} widget; only a grid widget exports.");
            }

            return new ExportTablesDocument(new[] { await DashboardExportTables.TableAsync(catalog, widget, ct) });
        }

        internal static bool TryParse(string? json, out Guid dashboardId, out Guid widgetId, out string error)
        {
            widgetId = Guid.Empty;
            dashboardId = Guid.Empty;
            if (!ExportArguments.TryReadObject(json, Members, out var members, out error))
            {
                return false;
            }

            if (!ExportArguments.TryGuid(members, "dashboardId", out dashboardId) || !ExportArguments.TryGuid(members, "widgetId", out widgetId))
            {
                error = "The arguments need a 'dashboardId' and a 'widgetId' that are GUIDs.";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// A whole board as an export: key <c>dashboards.report</c>,
    /// arguments <c>{"dashboardId": "..."}</c>, one table per GRID widget in the board's widget order - MULTIPLE tables, so
    /// CSV refuses it at request time and XLSX renders a sheet each. A board with no grid widget is refused when opened.
    /// </summary>
    public sealed class DashboardReportExportSource(IDashboardRepository dashboards, SiteState siteState, IDatasetCatalog catalog) : IExportSource
    {
        public const string SourceKey = "dashboards.report";

        private static readonly string[] Members = { "dashboardId" };

        public string Key => SourceKey;

        public string RequiredPermission => DashboardPermissions.View;

        public TableCardinality TableCardinality => TableCardinality.Multiple;

        public string? Validate(ExportSourceContext context) => TryParse(context?.ArgumentsJson, out _, out var error) ? null : error;

        public async Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryParse(context.ArgumentsJson, out var dashboardId, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var board = await DashboardExportTables.BoardAsync(dashboards, siteState, dashboardId, ct);
            var grids = board.Widgets.Where(widget => widget.Kind == WidgetKind.Grid).ToList();
            if (grids.Count == 0)
            {
                throw new InvalidOperationException($"Dashboard '{dashboardId}' has no grid widgets to export.");
            }

            var tables = new List<ITabularTable>(grids.Count);
            foreach (var widget in grids)
            {
                tables.Add(await DashboardExportTables.TableAsync(catalog, widget, ct));
            }

            return new ExportTablesDocument(tables);
        }

        internal static bool TryParse(string? json, out Guid dashboardId, out string error)
        {
            dashboardId = Guid.Empty;
            if (!ExportArguments.TryReadObject(json, Members, out var members, out error))
            {
                return false;
            }

            if (!ExportArguments.TryGuid(members, "dashboardId", out dashboardId))
            {
                error = "The arguments need a 'dashboardId' that is a GUID.";
                return false;
            }

            return true;
        }
    }

    internal static class DashboardExportTables
    {
        // The board through the job's resolved site, as every Dashboards handler reads it: another site's board does not exist.
        public static async Task<DashboardAggregate> BoardAsync(IDashboardRepository dashboards, SiteState siteState, Guid dashboardId, CancellationToken ct)
        {
            var board = await dashboards.GetByIdAsync(dashboardId, ct);
            if (board is null || siteState.Alias?.SiteId is not { } siteId || board.SiteId != siteId)
            {
                throw new InvalidOperationException($"Dashboard '{dashboardId}' does not exist.");
            }

            return board;
        }

        // What the widget's data query answers, as a table: the bound columns (the schema's when none is bound), typed from
        // the schema, the rows only when the classification allows them.
        public static async Task<ITabularTable> TableAsync(IDatasetCatalog catalog, Widget widget, CancellationToken ct)
        {
            var descriptor = await catalog.GetDescriptorAsync(widget.Binding.DatasetId, ct)
                ?? throw new InvalidOperationException($"The dataset '{widget.Binding.DatasetId}' bound to widget '{widget.Id}' does not exist.");
            var schema = descriptor.Schema.Columns;
            IReadOnlyList<string> names = widget.Binding.Columns.Count > 0 ? widget.Binding.Columns : schema.Select(column => column.Name).ToList();
            var columns = new List<ExportColumn>(names.Count);
            foreach (var name in names)
            {
                var column = schema.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Widget '{widget.Id}' binds column '{name}', which does not exist in dataset '{descriptor.Name}'.");
                columns.Add(new ExportColumn(name, JsonRowsTable.KindOf(column.DataType), column.FormatKey));
            }

            var rows = await catalog.GetPreviewRowsAsync(widget.Binding.DatasetId, ct);
            return new JsonRowsTable(widget.Title, columns, rows.Allowed ? rows.RowsJson : "[]");
        }
    }
}
