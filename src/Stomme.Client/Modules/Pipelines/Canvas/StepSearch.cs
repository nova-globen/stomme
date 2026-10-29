using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// The step picker's search: every word of the query is found somewhere in a step's name, category,
    /// description or type, and the steps whose NAME matches come first - the name starting with the query, then a word of
    /// the name starting with it, then the name containing it, then a match elsewhere only. An empty query is the catalog.
    /// </summary>
    public static class StepSearch
    {
        public static List<StepCatalogEntry> Filter(IEnumerable<StepCatalogEntry> catalog, string query)
        {
            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry is not null).ToList();
            var text = (query ?? "").Trim();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return entries;
            }

            return entries
                .Select(entry => (Entry: entry, Score: Score(entry, words, text)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Entry.DisplayName ?? match.Entry.Type, StringComparer.CurrentCultureIgnoreCase)
                .Select(match => match.Entry)
                .ToList();
        }

        private static int Score(StepCatalogEntry entry, string[] words, string query)
        {
            var name = entry.DisplayName ?? entry.Type ?? "";
            var haystack = string.Join(" ", name, entry.Category, entry.Description, entry.Type);
            if (!words.All(word => haystack.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            {
                return 0;
            }

            if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            {
                return 4;
            }

            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase)))
            {
                return 3;
            }

            return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 2 : 1;
        }
    }

    /// <summary>
    /// Where a step added after another lands: one column to its right, at the first row no step on the
    /// canvas is already near - so a chain grows left to right and a second step after the same one goes below the first.
    /// </summary>
    public static class StepPlacement
    {
        public const double ColumnWidth = 260;

        public const double RowHeight = 140;

        public static (double X, double Y) After((double X, double Y) from, IEnumerable<(double X, double Y)> taken)
        {
            var occupied = (taken ?? Enumerable.Empty<(double X, double Y)>()).ToList();
            var x = from.X + ColumnWidth;
            var y = from.Y;
            for (var guard = 0; guard < 500 && occupied.Any(spot => Math.Abs(spot.X - x) < ColumnWidth * 0.8 && Math.Abs(spot.Y - y) < RowHeight * 0.8); guard++)
            {
                y += RowHeight;
            }

            return (x, y);
        }

        /// <summary>
        /// Where a step put on a connection lands (n8n's + on a connection): halfway between the two steps, on the row
        /// of the one the connection goes to. When they are less than two columns apart, that step and every step after it
        /// (<see cref="Downstream"/>) move right by <c>Shift</c>, so the chain keeps its spacing. A connection going back - to a
        /// step not right of the one it leaves - moves nothing: the step takes the first free spot after the one it leaves.
        /// </summary>
        public static (double X, double Y, double Shift) Between((double X, double Y) from, (double X, double Y) to, IEnumerable<(double X, double Y)> taken)
        {
            if (to.X <= from.X)
            {
                var free = After(from, taken);
                return (free.X, free.Y, 0);
            }

            var shift = Math.Max(0, from.X + 2 * ColumnWidth - to.X);
            return ((from.X + to.X + shift) / 2, to.Y, shift);
        }

        /// <summary>The steps reached from <paramref name="start"/> (itself included) along the connections, never through <paramref name="stop"/>.</summary>
        public static HashSet<string> Downstream(string start, string stop, IEnumerable<(string From, string To)> connections)
        {
            var next = (connections ?? Enumerable.Empty<(string From, string To)>())
                .Where(connection => connection.From is not null && connection.To is not null)
                .ToLookup(connection => connection.From, connection => connection.To, StringComparer.Ordinal);
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var waiting = new Queue<string>();
            if (start is not null && start != stop)
            {
                reached.Add(start);
                waiting.Enqueue(start);
            }

            while (waiting.Count > 0)
            {
                foreach (var to in next[waiting.Dequeue()])
                {
                    if (to != stop && reached.Add(to))
                    {
                        waiting.Enqueue(to);
                    }
                }
            }

            return reached;
        }
    }

    /// <summary>
    /// n8n's arrow keys on the canvas: from the selected step, Right goes to the next step along its connections (the
    /// one nearest its row, when several follow), Left to the one before, Up and Down to the nearest step above or below in its
    /// column. Null when there is none that way - the selection stays where it is.
    /// </summary>
    public static class StepNavigation
    {
        public static string Next(string direction, string from, IReadOnlyDictionary<string, (double X, double Y)> steps, IEnumerable<(string From, string To)> connections)
        {
            if (from is null || steps is null || !steps.TryGetValue(from, out var at))
            {
                return null;
            }

            var links = (connections ?? Enumerable.Empty<(string From, string To)>())
                .Where(link => link.From is not null && link.To is not null && link.From != link.To && steps.ContainsKey(link.From) && steps.ContainsKey(link.To))
                .ToList();
            var column = steps.Where(step => step.Key != from && Math.Abs(step.Value.X - at.X) < StepPlacement.ColumnWidth / 2);
            IEnumerable<string> candidates = direction switch
            {
                "right" => links.Where(link => link.From == from).Select(link => link.To)
                    .OrderBy(id => Math.Abs(steps[id].Y - at.Y)).ThenBy(id => steps[id].X),
                "left" => links.Where(link => link.To == from).Select(link => link.From)
                    .OrderBy(id => Math.Abs(steps[id].Y - at.Y)).ThenByDescending(id => steps[id].X),
                "up" => column.Where(step => step.Value.Y < at.Y).OrderByDescending(step => step.Value.Y).Select(step => step.Key),
                "down" => column.Where(step => step.Value.Y > at.Y).OrderBy(step => step.Value.Y).Select(step => step.Key),
                _ => Enumerable.Empty<string>(),
            };
            return candidates.FirstOrDefault();
        }
    }
}
