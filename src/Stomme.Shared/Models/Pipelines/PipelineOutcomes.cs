#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// The outcomes a step NAMES ITSELF: a Fork (flow)'s branches - a list held in its Branches input - and
    /// a Switch (flow)'s cases, each beside its Default. The editor draws one port per outcome; the engine goes out through the
    /// ones it completes with.
    /// </summary>
    public static class PipelineOutcomes
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The outcome a step goes out through when it still fails and takes its Failed path (n8n's error output,
        /// <see cref="PipelineGraphStep.FailedPath"/>) - the name the data steps' own failure port carries, so a data step keeps
        /// one Failed port.
        /// </summary>
        public const string Failed = "Failed";

        /// <summary>
        /// Every outcome port the step shows, in order: the outcomes it names (<see cref="Of"/>), else its catalog ports (or the one
        /// unnamed outcome), and - when it takes its Failed path - <see cref="Failed"/> at the end, once.
        /// </summary>
        public static IReadOnlyList<string?> Ports(StepCatalogEntry? entry, IReadOnlyDictionary<string, string>? inputs, IEnumerable<PipelineGraphCase>? cases, bool failedPath)
        {
            var ports = (Of(entry, inputs, cases) ?? (entry?.Ports is { Count: > 0 } catalog ? catalog.Cast<string?>().ToList() : new List<string?> { null })).ToList();
            if (failedPath && !ports.Contains(Failed))
            {
                ports.Add(Failed);
            }

            return ports;
        }

        /// <summary>
        /// The step's outcomes when it names them, in order; null when its outcomes are its catalog ports alone. A Fork with no
        /// branch has one, the implicit outcome (null).
        /// </summary>
        public static IReadOnlyList<string?>? Of(StepCatalogEntry? entry, IReadOnlyDictionary<string, string>? inputs, IEnumerable<PipelineGraphCase>? cases) =>
            entry?.OutcomesFrom switch
            {
                StepCatalogEntry.BranchesOutcomes => Branches(inputs is not null && inputs.TryGetValue(StepCatalogEntry.BranchesOutcomes, out var text) ? text : null) is { Count: > 0 } branches
                    ? branches.Cast<string?>().ToList()
                    : new List<string?> { null },
                StepCatalogEntry.CaseOutcomes => (cases ?? Enumerable.Empty<PipelineGraphCase>())
                    .Select(@case => @case?.Label?.Trim())
                    .Where(label => !string.IsNullOrEmpty(label))
                    .Distinct(StringComparer.Ordinal)
                    .Concat(entry.Ports ?? new List<string>())
                    .Cast<string?>()
                    .ToList(),
                _ => null,
            };

        /// <summary>A Fork's branches as its input holds them: a JSON list of names, or names one per line or comma-separated.</summary>
        public static List<string> Branches(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            var trimmed = text.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    return (JsonSerializer.Deserialize<List<string?>>(trimmed, Json) ?? new List<string?>())
                        .Select(name => name?.Trim() ?? string.Empty)
                        .Where(name => name.Length > 0)
                        .ToList();
                }
                catch (JsonException)
                {
                    // not a list: read it as names
                }
            }

            return trimmed.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .ToList();
        }

        /// <summary>The branches as the Branches input holds them: a JSON list, or nothing for none.</summary>
        public static string WriteBranches(IEnumerable<string>? names)
        {
            var list = (names ?? Enumerable.Empty<string>()).Select(name => name?.Trim() ?? string.Empty).Where(name => name.Length > 0).ToList();
            return list.Count == 0 ? string.Empty : JsonSerializer.Serialize(list, Json);
        }

        /// <summary>A copy of the cases, so an editor's change to one never reaches another.</summary>
        public static List<PipelineGraphCase> Copy(IEnumerable<PipelineGraphCase>? cases) =>
            (cases ?? Enumerable.Empty<PipelineGraphCase>())
                .Where(@case => @case is not null)
                .Select(@case => new PipelineGraphCase { Label = @case.Label, Condition = PipelineConditions.Copy(@case.Condition) })
                .ToList();

        /// <summary>The cases as one text - what two editors' merge compares.</summary>
        public static string WriteCases(IEnumerable<PipelineGraphCase>? cases) => JsonSerializer.Serialize(Copy(cases), Json);

        public static List<PipelineGraphCase> ReadCases(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<PipelineGraphCase>();
            }

            try
            {
                return Copy(JsonSerializer.Deserialize<List<PipelineGraphCase>>(text, Json));
            }
            catch (JsonException)
            {
                return new List<PipelineGraphCase>();
            }
        }
    }
}
