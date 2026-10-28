#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Dashboards;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Dashboards.Domain
{
    public sealed record DashboardCreatedEvent(Guid DashboardId, string Name) : DomainEvent;

    public sealed record WidgetAddedEvent(Guid DashboardId, Guid WidgetId) : DomainEvent;

    public sealed record DashboardUpdatedEvent(Guid DashboardId) : DomainEvent;

    public sealed record DashboardPublishedEvent(Guid DashboardId) : DomainEvent;

    /// <summary>
    /// A dashboard (BL-96 increment 8, lifted from the predecessor platform's Dashboard): a named, tenant-owned, per-site board of widgets on a
    /// layout grid - no two widgets overlap, none reaches past the last column, a layout change that would orphan one is
    /// refused - published once and cloned as a fresh draft. Storage-blind: the widgets ride the module store's JSON
    /// collection column, the layout is one scalar, and every store round-trips the same shape (the conformance suite).
    /// <b>CHANGED:</b> the predecessor platform's dashboard belonged to a tenant only; here it belongs to a SITE of that tenant too, as
    /// every Stomme page-owned aggregate does, so one tenant's sites keep their boards apart.
    /// </summary>
    public sealed class DashboardAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<Widget> _widgets = new();

        // Materialisation constructor. Not for application use.
        private DashboardAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            Layout = LayoutGrid.Default;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Name { get; private set; }

        public string NameNormalized { get; private set; }

        public DashboardStatus Status { get; private set; }

        public LayoutGrid Layout { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public IReadOnlyList<Widget> Widgets => _widgets;

        public static string NormalizeName(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        public static DashboardAggregate Create(Guid id, TenantId tenantId, int siteId, string name, LayoutGrid layout, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(layout);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A dashboard belongs to a site.");
            }

            var clean = CleanName(name);
            var dashboard = new DashboardAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Name = clean,
                NameNormalized = NormalizeName(clean),
                Status = DashboardStatus.Draft,
                Layout = layout,
                CreatedUtc = Utc(nowUtc),
                UpdatedUtc = Utc(nowUtc),
            };
            dashboard.RaiseDomainEvent(new DashboardCreatedEvent(id, clean));
            return dashboard;
        }

        public void Rename(string name, DateTime nowUtc)
        {
            Name = CleanName(name);
            NameNormalized = NormalizeName(Name);
            Touch(nowUtc);
        }

        public void AddWidget(Widget widget, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(widget);
            if (_widgets.Any(existing => existing.Id == widget.Id))
            {
                throw new InvalidOperationException($"Widget '{widget.Id}' is already on this dashboard.");
            }

            EnsurePlacement(widget.Position, exceptWidgetId: null);
            _widgets.Add(widget);
            UpdatedUtc = Utc(nowUtc);
            RaiseDomainEvent(new WidgetAddedEvent(Id, widget.Id));
        }

        public void RemoveWidget(Guid widgetId, DateTime nowUtc)
        {
            _widgets.Remove(RequireWidget(widgetId));
            Touch(nowUtc);
        }

        public void MoveWidget(Guid widgetId, GridPosition position, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(position);
            var widget = RequireWidget(widgetId);
            EnsurePlacement(position, exceptWidgetId: widgetId);
            widget.Reposition(position);
            Touch(nowUtc);
        }

        public void RebindWidget(Guid widgetId, DataBinding binding, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(binding);
            RequireWidget(widgetId).Rebind(binding);
            Touch(nowUtc);
        }

        public void ReconfigureWidget(Guid widgetId, string title, RefreshMode refresh, Interaction interaction, ChartSpec? chartSpec, DateTime nowUtc)
        {
            RequireWidget(widgetId).Reconfigure(title, refresh, interaction, chartSpec);
            Touch(nowUtc);
        }

        public void SaveWidgetGridState(Guid widgetId, GridState gridState, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(gridState);
            RequireWidget(widgetId).SaveGridState(gridState);
            Touch(nowUtc);
        }

        /// <summary>Swaps the widget with its neighbour in render order; at an edge nothing happens.</summary>
        public void ReorderWidget(Guid widgetId, bool forward, DateTime nowUtc)
        {
            var index = _widgets.FindIndex(widget => widget.Id == widgetId);
            if (index < 0)
            {
                throw new InvalidOperationException($"Widget '{widgetId}' is not on this dashboard.");
            }

            var target = forward ? index + 1 : index - 1;
            if (target < 0 || target >= _widgets.Count)
            {
                return;
            }

            (_widgets[index], _widgets[target]) = (_widgets[target], _widgets[index]);
            Touch(nowUtc);
        }

        public void RearrangeLayout(LayoutGrid layout, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(layout);
            foreach (var widget in _widgets)
            {
                if (!widget.Position.FitsWithin(layout.Columns))
                {
                    throw new InvalidOperationException($"Widget '{widget.Id}' does not fit within a {layout.Columns}-column grid.");
                }
            }

            Layout = layout;
            Touch(nowUtc);
        }

        public void Publish(DateTime nowUtc)
        {
            if (Status == DashboardStatus.Published)
            {
                return;
            }

            Status = DashboardStatus.Published;
            UpdatedUtc = Utc(nowUtc);
            RaiseDomainEvent(new DashboardPublishedEvent(Id));
        }

        /// <summary>A fresh draft with the same layout and copies of every widget under new ids, in the same order.</summary>
        public DashboardAggregate CloneAs(Guid newId, string newName, DateTime nowUtc)
        {
            var clone = Create(newId, TenantId, SiteId, newName, Layout, nowUtc);
            foreach (var widget in _widgets)
            {
                clone.AddWidget(Widget.Create(Guid.NewGuid(), widget.Title, widget.Kind, widget.Binding, widget.Position, widget.Refresh, widget.Interaction, widget.ChartSpec), nowUtc);
            }

            return clone;
        }

        private void Touch(DateTime nowUtc)
        {
            UpdatedUtc = Utc(nowUtc);
            RaiseDomainEvent(new DashboardUpdatedEvent(Id));
        }

        private Widget RequireWidget(Guid widgetId) =>
            _widgets.SingleOrDefault(widget => widget.Id == widgetId) ?? throw new InvalidOperationException($"Widget '{widgetId}' is not on this dashboard.");

        private void EnsurePlacement(GridPosition position, Guid? exceptWidgetId)
        {
            if (!position.FitsWithin(Layout.Columns))
            {
                throw new InvalidOperationException($"The widget does not fit within the {Layout.Columns}-column layout grid.");
            }

            if (_widgets.Any(widget => widget.Id != exceptWidgetId && widget.Position.Overlaps(position)))
            {
                throw new InvalidOperationException("Widgets may not overlap on the layout grid.");
            }
        }

        private static string CleanName(string? name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var clean = name.Trim();
            if (clean.Length > DashboardLimits.MaxNameLength)
            {
                throw new ArgumentException($"A dashboard name must be at most {DashboardLimits.MaxNameLength} characters.", nameof(name));
            }

            return clean;
        }

        private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("An instant must be UTC.", nameof(value));
    }
}
