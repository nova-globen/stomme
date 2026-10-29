#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>A step the find on the canvas matched: which, where it sits, and what of it matched.</summary>
    public sealed class PipelineGraphMatch
    {
        public string StepId { get; set; } = "";

        /// <summary>The step as the canvas names it: its name, else its type's.</summary>
        public string Step { get; set; } = "";

        /// <summary>The bodies it sits in, outermost first - "in 'Each row' › Body" - or empty for the pipeline itself.</summary>
        public string Where { get; set; } = "";

        /// <summary>What matched: "name", "type", "comment", "input 'Url'", "expression of 'Text'"...</summary>
        public string Field { get; set; } = "";

        /// <summary>The matching text, cut around the match when long.</summary>
        public string Snippet { get; set; } = "";
    }

    /// <summary>
    /// Find on the canvas: the steps of a pipeline - in every body, however deep - whose name, type, comment, inputs,
    /// expressions, scripts, conditions or case labels hold the text, case-insensitively, in the order they were drawn; one
    /// match per step (its first field), at most <c>max</c>. A large pipeline's step is a search away, wherever it sits.
    /// </summary>
    public static class PipelineGraphFind
    {
        public const int SnippetLength = 60;

        public static List<PipelineGraphMatch> Find(PipelineGraph? graph, IEnumerable<StepCatalogEntry>? catalog, string? query, int max = 20)
        {
            var found = new List<PipelineGraphMatch>();
            var text = query?.Trim();
            if (graph?.Steps is null || string.IsNullOrEmpty(text) || max <= 0)
            {
                return found;
            }

            var entries = new Dictionary<string, StepCatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in catalog ?? Enumerable.Empty<StepCatalogEntry>())
            {
                if (entry?.Type is not null)
                {
                    entries.TryAdd(entry.Type, entry);
                }
            }

            var steps = graph.Steps.Where(step => !string.IsNullOrEmpty(step?.Id)).ToList();
            var byId = new Dictionary<string, PipelineGraphStep>(StringComparer.Ordinal);
            foreach (var step in steps)
            {
                byId.TryAdd(step.Id, step);
            }

            string Title(PipelineGraphStep step) =>
                !string.IsNullOrWhiteSpace(step.Name) ? step.Name.Trim()
                : entries.TryGetValue(step.Type ?? "", out var entry) && !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName
                : step.Type ?? step.Id;

            foreach (var step in steps)
            {
                var hit = FirstHit(step, entries.TryGetValue(step.Type ?? "", out var known) ? known : null, text);
                if (hit is null)
                {
                    continue;
                }

                found.Add(new PipelineGraphMatch
                {
                    StepId = step.Id,
                    Step = Title(step),
                    Where = Where(step, byId, Title),
                    Field = hit.Value.Field,
                    Snippet = Snippet(hit.Value.Value, text),
                });
                if (found.Count == max)
                {
                    break;
                }
            }

            return found;
        }

        private static (string Field, string Value)? FirstHit(PipelineGraphStep step, StepCatalogEntry? entry, string text)
        {
            bool Has(string? value) => !string.IsNullOrEmpty(value) && value.Contains(text, StringComparison.OrdinalIgnoreCase);
            string Label(string input) => entry?.Inputs?.FirstOrDefault(candidate => candidate.Name == input)?.DisplayName is { Length: > 0 } shown ? shown : input;

            if (Has(step.Name))
            {
                return ("name", step.Name!);
            }

            if (Has(entry?.DisplayName) || Has(step.Type))
            {
                return ("type", Has(entry?.DisplayName) ? entry!.DisplayName : step.Type!);
            }

            if (Has(step.Comment))
            {
                return ("comment", step.Comment!);
            }

            foreach (var input in step.Inputs ?? new Dictionary<string, string>())
            {
                if (Has(input.Value))
                {
                    return ($"input '{Label(input.Key)}'", input.Value);
                }
            }

            foreach (var input in step.InputExpressions ?? new Dictionary<string, string>())
            {
                if (Has(input.Value))
                {
                    return ($"expression of '{Label(input.Key)}'", input.Value);
                }
            }

            foreach (var input in step.InputScripts ?? new Dictionary<string, string>())
            {
                if (Has(input.Value))
                {
                    return ($"script of '{Label(input.Key)}'", input.Value);
                }
            }

            foreach (var input in step.InputConditions ?? new Dictionary<string, PipelineCondition>())
            {
                foreach (var rule in input.Value?.Rules ?? new List<PipelineConditionRule>())
                {
                    if (Has(rule?.Left) || Has(rule?.Right))
                    {
                        return ($"condition of '{Label(input.Key)}'", Has(rule!.Left) ? rule.Left! : rule.Right!);
                    }
                }
            }

            foreach (var @case in step.Cases ?? new List<PipelineGraphCase>())
            {
                if (Has(@case?.Label))
                {
                    return ("case", @case!.Label!);
                }
            }

            return null;
        }

        // The bodies a step sits in, outermost first: "in 'Each row' › Body › 'Check' › Then".
        private static string Where(PipelineGraphStep step, IReadOnlyDictionary<string, PipelineGraphStep> byId, Func<PipelineGraphStep, string> title)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = step; !string.IsNullOrEmpty(current.Parent) && seen.Add(current.Parent!) && byId.TryGetValue(current.Parent!, out var holder); current = holder)
            {
                parts.Insert(0, $"'{title(holder)}' › {current.ParentPort ?? "Body"}");
            }

            return parts.Count == 0 ? "" : "in " + string.Join(" › ", parts);
        }

        // The text around the match, one line, at most SnippetLength characters with an ellipsis where it is cut.
        private static string Snippet(string value, string text)
        {
            var line = string.Join(" ", value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (line.Length <= SnippetLength)
            {
                return line;
            }

            var at = line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, Math.Min(at - (SnippetLength - text.Length) / 2, line.Length - SnippetLength));
            var cut = line.Substring(start, SnippetLength);
            return (start > 0 ? "…" : "") + cut + (start + SnippetLength < line.Length ? "…" : "");
        }
    }
}
