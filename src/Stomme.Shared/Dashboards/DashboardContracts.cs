#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Dashboards
{
    // ============================================================================================================
    // The Dashboards module's Shared contracts (BL-96 increment 8, D110): the permission keys and the enumerations a
    // page or an API consumer reads. BCL-ONLY like the other Shared contracts.
    // ============================================================================================================

    public static class DashboardPermissions
    {
        public const string Resource = "dashboards";

        public const string View = "dashboards:view";

        public const string Manage = "dashboards:manage";

        public const string Publish = "dashboards:publish";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage, Publish };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
            (Publish, new[] { "Administrators" }),
        };
    }

    public static class DashboardLimits
    {
        public const int MaxNameLength = 200;

        public const int MaxWidgetTitleLength = 200;

        public const int MinColumns = 1;

        public const int MaxColumns = 48;

        public const int MinRowHeightPx = 1;

        public const int MaxRowHeightPx = 1000;

        /// <summary>The shortest interval an interval-refreshed widget may ask for, in seconds.</summary>
        public const int MinimumRefreshIntervalSeconds = 30;

        /// <summary>The longest chart specification a widget stores.</summary>
        public const int MaxChartSpecLength = 64_000;
    }

    public enum DashboardStatus
    {
        Draft = 0,

        Published = 1,
    }

    public enum WidgetKind
    {
        Chart = 0,

        Grid = 1,

        List = 2,

        Kpi = 3,

        /// <summary>A pivot over the bounded rows (increment 10): the first bound column groups, the second is summed when numeric; a count always.</summary>
        Pivot = 4,
    }

    public enum RefreshKind
    {
        Manual = 0,

        Interval = 1,

        EventDriven = 2,
    }

    public enum InteractionKind
    {
        None = 0,

        Navigate = 1,

        Popup = 2,

        Form = 3,
    }
}
