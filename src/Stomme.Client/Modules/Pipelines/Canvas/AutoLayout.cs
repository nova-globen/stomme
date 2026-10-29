using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// "Tidy up": every flowchart of the graph (the pipeline's own and each body) laid out left to right
    /// by its flow. A step goes one column right of the furthest step that leads to it (a step nothing leads to is in the first
    /// column); within a column, steps follow the rows of the steps leading to them, then their order in the graph. A cycle is
    /// cut where it is met, so every step gets a place. Positions only - nothing else of the graph changes. Or only the steps
    /// chosen, kept where they were.
    /// </summary>
    public static class AutoLayout
    {
        public const double Left = 30;

        public const double Top = 40;

        public static void Arrange(PipelineGraph graph)
        {
            if (graph?.Steps is null)
            {
                return;
            }

            foreach (var scope in graph.Steps.Where(step => !string.IsNullOrWhiteSpace(step?.Id))
                         .GroupBy(step => string.IsNullOrWhiteSpace(step.Parent) ? "" : step.Parent + "\u001f" + step.ParentPort))
            {
                ArrangeScope(scope.ToList(), graph.Connections ?? new List<PipelineGraphConnection>());
            }
        }

        /// <summary>
        /// n8n's "Tidy up" on the steps chosen: only they are laid out, by the connections among them, and kept where they were -
        /// their top left corner where theirs was. Nothing else moves.
        /// </summary>
        public static void Arrange(PipelineGraph graph, IReadOnlyCollection<string> only)
        {
            if (graph?.Steps is null || only is null || only.Count == 0)
            {
                return;
            }

            foreach (var scope in graph.Steps.Where(step => !string.IsNullOrWhiteSpace(step?.Id) && only.Contains(step.Id))
                         .GroupBy(step => string.IsNullOrWhiteSpace(step.Parent) ? "" : step.Parent + "\u001f" + step.ParentPort))
            {
                var chosen = scope.ToList();
                var (left, top) = (chosen.Min(step => step.Position?.X ?? Left), chosen.Min(step => step.Position?.Y ?? Top));
                ArrangeScope(chosen, graph.Connections ?? new List<PipelineGraphConnection>());
                foreach (var step in chosen)
                {
                    step.Position = new GraphPosition { X = step.Position.X - Left + left, Y = step.Position.Y - Top + top };
                }
            }
        }

        private static void ArrangeScope(List<PipelineGraphStep> steps, List<PipelineGraphConnection> connections)
        {
            var order = steps.Select((step, index) => (step.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
            var inner = connections
                .Where(connection => connection is not null && order.ContainsKey(connection.SourceId ?? "") && order.ContainsKey(connection.TargetId ?? "") && connection.SourceId != connection.TargetId)
                .ToList();
            var leadsTo = inner.GroupBy(connection => connection.TargetId).ToDictionary(group => group.Key, group => group.Select(connection => connection.SourceId).Distinct().ToList(), StringComparer.Ordinal);

            // Columns: the longest path from a step nothing leads to; a step met again while its own column is being worked out
            // closes a cycle and is not followed further.
            var column = new Dictionary<string, int>(StringComparer.Ordinal);
            var working = new HashSet<string>(StringComparer.Ordinal);
            int ColumnOf(string id)
            {
                if (column.TryGetValue(id, out var known))
                {
                    return known;
                }

                if (!working.Add(id))
                {
                    return 0;
                }

                var at = leadsTo.TryGetValue(id, out var sources) ? sources.Max(source => ColumnOf(source) + 1) : 0;
                working.Remove(id);
                column[id] = at;
                return at;
            }

            foreach (var step in steps)
            {
                ColumnOf(step.Id);
            }

            // Rows: column by column, each step under the steps leading to it (the mean of their rows), then in graph order.
            var row = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var group in steps.GroupBy(step => column[step.Id]).OrderBy(group => group.Key))
            {
                var placed = group
                    .Select(step => (Step: step, Want: leadsTo.TryGetValue(step.Id, out var sources) && sources.Any(row.ContainsKey)
                        ? sources.Where(row.ContainsKey).Average(source => row[source])
                        : double.MaxValue))
                    .OrderBy(candidate => candidate.Want)
                    .ThenBy(candidate => order[candidate.Step.Id])
                    .ToList();
                for (var index = 0; index < placed.Count; index++)
                {
                    var step = placed[index].Step;
                    row[step.Id] = index;
                    step.Position = new GraphPosition
                    {
                        X = Left + column[step.Id] * StepPlacement.ColumnWidth,
                        Y = Top + index * StepPlacement.RowHeight,
                    };
                }
            }
        }
    }
}
