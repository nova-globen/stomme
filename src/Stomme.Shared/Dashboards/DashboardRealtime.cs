#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stomme.Dashboards
{
    /// <summary>What a site's dashboard viewers are told over the hub (BL-96 increment 10): which dataset changed and how - <c>Refreshed</c> or <c>SchemaChanged</c>. No rows, no values: the viewer re-reads through the API, under its own key.</summary>
    public sealed record DashboardDatasetChange(Guid DatasetId, string Kind);

    public static class DashboardHubMethods
    {
        /// <summary>The client method the push invokes with a <see cref="DashboardDatasetChange"/>.</summary>
        public const string DatasetChanged = "datasetChanged";
    }

    /// <summary>
    /// The in-process fan-out of a dataset change to the widgets rendered on THIS host (the predecessor platform's IDashboardDataSignal by
    /// name): an Interactive Server circuit has no browser cookie to open the hub with, so a page rendered on the server
    /// subscribes here and re-reads without a round-trip; a browser page uses the hub. A singleton, registered by the
    /// Server; absent in the browser, where the page asks for it optionally. The site key scopes a change to its site.
    /// </summary>
    public interface IDashboardDataSignal
    {
        event Action<string, DashboardDatasetChange>? DatasetChanged;

        void NotifyDatasetChanged(string siteKey, DashboardDatasetChange change);
    }

    /// <summary>
    /// Each subscriber is called in the context it subscribed in (<see cref="Stomme.Transport.Realtime.SiteSignal{TMessage}"/>).
    /// <b>CORRECTED (BL-97 increment 12):</b> it invoked its subscribers on the raiser's thread, so a viewer's circuit re-read
    /// its widgets as the user whose request refreshed the dataset.
    /// </summary>
    public sealed class DashboardDataSignal : Stomme.Transport.Realtime.SiteSignal<DashboardDatasetChange>, IDashboardDataSignal
    {
        public event Action<string, DashboardDatasetChange>? DatasetChanged
        {
            add => Subscribe(value);
            remove => Unsubscribe(value);
        }

        public void NotifyDatasetChanged(string siteKey, DashboardDatasetChange change) => Raise(siteKey, change);
    }

    /// <summary>One row of a pivot: the group key, how many rows carry it, and the sum of the value column over them when it is numeric.</summary>
    public sealed record PivotRow(string Key, int Count, double? Sum);

    /// <summary>
    /// The pivot widget's computation (BL-96 increment 10), over the bounded preview rows the widget's data carries: the
    /// rows grouped by the first bound column, counted, and the second bound column summed where it parses as a number
    /// (invariant culture); groups in first-seen order, a missing key as an empty string. BCL-only, so the page and a
    /// test compute the same table.
    /// </summary>
    public static class PivotTable
    {
        public static IReadOnlyList<PivotRow> Compute(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string keyColumn, string? valueColumn)
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sums = new Dictionary<string, double>(StringComparer.Ordinal);
            var numeric = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = row.TryGetValue(keyColumn, out var keyValue) ? keyValue ?? string.Empty : string.Empty;
                if (!counts.ContainsKey(key))
                {
                    order.Add(key);
                    counts[key] = 0;
                    sums[key] = 0;
                    numeric[key] = valueColumn is not null;
                }

                counts[key]++;
                if (valueColumn is not null && numeric[key])
                {
                    if (row.TryGetValue(valueColumn, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        sums[key] += number;
                    }
                    else if (!string.IsNullOrWhiteSpace(raw))
                    {
                        numeric[key] = false; // a value that is not a number makes the group's sum unavailable, never wrong
                    }
                }
            }

            return order.Select(key => new PivotRow(key, counts[key], valueColumn is not null && numeric[key] ? sums[key] : null)).ToList();
        }
    }
}
