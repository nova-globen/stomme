#nullable enable
using System;
using Stomme.Dashboards;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Dashboards.Domain
{
    /// <summary>
    /// One widget on a dashboard: a titled cell of one kind bound to a
    /// dataset's columns, placed on the grid, refreshed one way, optionally interactive; a chart carries a theme-free
    /// spec and nothing else does; a grid carries its view state. A child entity of the aggregate: it rides the
    /// dashboard's JSON collection column and is changed only through the dashboard.
    /// </summary>
    public sealed class Widget : Entity<Guid>
    {
        // Materialisation constructor. Not for application use.
        private Widget()
        {
            Title = string.Empty;
            Binding = null!;
            Position = null!;
            Refresh = null!;
            Interaction = null!;
            GridState = GridState.Empty;
        }

        internal Widget(Guid id, string title, WidgetKind kind, DataBinding binding, GridPosition position, RefreshMode refresh, Interaction interaction, ChartSpec? chartSpec)
        {
            Id = id;
            Title = title;
            Kind = kind;
            Binding = binding;
            Position = position;
            Refresh = refresh;
            Interaction = interaction;
            ChartSpec = chartSpec;
            GridState = GridState.Empty;
        }

        public string Title { get; private set; }

        public WidgetKind Kind { get; private set; }

        public DataBinding Binding { get; private set; }

        public GridPosition Position { get; private set; }

        public RefreshMode Refresh { get; private set; }

        public Interaction Interaction { get; private set; }

        public ChartSpec? ChartSpec { get; private set; }

        public GridState GridState { get; private set; }

        public static Widget Create(Guid id, string title, WidgetKind kind, DataBinding binding, GridPosition position, RefreshMode refresh, Interaction? interaction = null, ChartSpec? chartSpec = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(position);
            ArgumentNullException.ThrowIfNull(refresh);
            RequireSpecMatchesKind(kind, chartSpec);
            if (kind == WidgetKind.Pivot && binding.Columns.Count == 0)
            {
                throw new InvalidOperationException("A pivot widget binds the column it groups by (and optionally the column it sums).");
            }

            var clean = title.Trim();
            if (clean.Length > DashboardLimits.MaxWidgetTitleLength)
            {
                throw new ArgumentException($"A widget title must be at most {DashboardLimits.MaxWidgetTitleLength} characters.", nameof(title));
            }

            return new Widget(id, clean, kind, binding, position, refresh, interaction ?? Interaction.None, chartSpec);
        }

        internal void Reposition(GridPosition position)
        {
            ArgumentNullException.ThrowIfNull(position);
            Position = position;
        }

        internal void Rebind(DataBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            Binding = binding;
        }

        internal void SaveGridState(GridState gridState)
        {
            ArgumentNullException.ThrowIfNull(gridState);
            if (Kind != WidgetKind.Grid)
            {
                throw new InvalidOperationException("Only grid widgets carry grid view-state.");
            }

            GridState = gridState;
        }

        internal void Reconfigure(string title, RefreshMode refresh, Interaction interaction, ChartSpec? chartSpec)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(refresh);
            ArgumentNullException.ThrowIfNull(interaction);
            RequireSpecMatchesKind(Kind, chartSpec);
            var clean = title.Trim();
            if (clean.Length > DashboardLimits.MaxWidgetTitleLength)
            {
                throw new ArgumentException($"A widget title must be at most {DashboardLimits.MaxWidgetTitleLength} characters.", nameof(title));
            }

            Title = clean;
            Refresh = refresh;
            Interaction = interaction;
            ChartSpec = chartSpec;
        }

        private static void RequireSpecMatchesKind(WidgetKind kind, ChartSpec? chartSpec)
        {
            if (kind == WidgetKind.Pivot && chartSpec is not null)
            {
                throw new InvalidOperationException("Only chart widgets may carry a chart spec.");
            }

            if (kind == WidgetKind.Chart && chartSpec is null)
            {
                throw new InvalidOperationException("A chart widget requires a chart spec.");
            }

            if (kind != WidgetKind.Chart && chartSpec is not null)
            {
                throw new InvalidOperationException("Only chart widgets may carry a chart spec.");
            }
        }
    }
}
