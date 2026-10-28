#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Datasets;

namespace Stomme.Dashboards
{
    // ============================================================================================================
    // The shapes a page or an API consumer reads and sends (BL-96 increment 9): a dashboard and its widgets as the
    // server projects them, a widget's data as the Datasets catalogue answers it, and the requests the API takes.
    // BCL-only, like the contracts beside them; the Datasets descriptors are the same Shared assembly's.
    // ============================================================================================================

    public sealed record ColumnWidthView(string Column, int WidthPx);

    public sealed record GridStateView(IReadOnlyList<ColumnWidthView> ColumnWidths, string? SortColumn, bool SortDescending, string? FilterExpression);

    public sealed record WidgetView(
        Guid Id,
        string Title,
        WidgetKind Kind,
        Guid DatasetId,
        IReadOnlyList<string> Columns,
        int Column,
        int Row,
        int ColumnSpan,
        int RowSpan,
        RefreshKind RefreshKind,
        int? IntervalSeconds,
        InteractionKind Interaction,
        string? Target,
        string? ChartSpecJson,
        GridStateView GridState);

    public sealed record DashboardView(Guid Id, string Name, DashboardStatus Status, int Columns, int RowHeightPx, DateTime CreatedUtc, DateTime UpdatedUtc, IReadOnlyList<WidgetView> Widgets);

    /// <summary>A widget's data: the bound dataset as the catalogue describes it (the summary masked when sensitive), and the bounded rows when the classification allows them.</summary>
    public sealed record WidgetDataView(
        Guid WidgetId,
        Guid DatasetId,
        string DatasetName,
        DataClassification Classification,
        IReadOnlyList<string> Columns,
        DatasetSummaryDescriptor Summary,
        bool RowsAllowed,
        string RowsJson,
        string RowsReason);

    public sealed class CreateDashboardRequest
    {
        public string Name { get; set; } = string.Empty;

        public int Columns { get; set; } = 12;

        public int RowHeightPx { get; set; } = 40;
    }

    public sealed class RenameDashboardRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CloneDashboardRequest
    {
        public string NewName { get; set; } = string.Empty;
    }

    public sealed class LayoutRequest
    {
        public int Columns { get; set; } = 12;

        public int RowHeightPx { get; set; } = 40;
    }

    public sealed class AddWidgetRequest
    {
        public string Title { get; set; } = string.Empty;

        public WidgetKind Kind { get; set; } = WidgetKind.Kpi;

        public Guid DatasetId { get; set; }

        public List<string> Columns { get; set; } = new();

        public int Column { get; set; }

        public int Row { get; set; }

        public int ColumnSpan { get; set; } = 4;

        public int RowSpan { get; set; } = 4;

        public RefreshKind RefreshKind { get; set; } = RefreshKind.Manual;

        public int? IntervalSeconds { get; set; }

        public InteractionKind Interaction { get; set; } = InteractionKind.None;

        public string? Target { get; set; }

        public string? ChartSpecJson { get; set; }
    }

    public sealed class UpdateWidgetRequest
    {
        public string Title { get; set; } = string.Empty;

        public RefreshKind RefreshKind { get; set; } = RefreshKind.Manual;

        public int? IntervalSeconds { get; set; }

        public InteractionKind Interaction { get; set; } = InteractionKind.None;

        public string? Target { get; set; }

        public string? ChartSpecJson { get; set; }
    }

    public sealed class MoveWidgetRequest
    {
        public int Column { get; set; }

        public int Row { get; set; }

        public int ColumnSpan { get; set; } = 4;

        public int RowSpan { get; set; } = 4;
    }

    public sealed class GridStateRequest
    {
        public List<ColumnWidthView> ColumnWidths { get; set; } = new();

        public string? SortColumn { get; set; }

        public bool SortDescending { get; set; }

        public string? FilterExpression { get; set; }
    }

    public sealed class ReorderWidgetRequest
    {
        public bool Forward { get; set; } = true;
    }
}
