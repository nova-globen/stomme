#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>A place in a pipeline where a text was found: the step it is in (null for a note or a variable), where, and a snippet.</summary>
    public sealed class PipelineFoundView
    {
        public string DefinitionId { get; set; } = "";

        public string? PipelineName { get; set; }

        public string Folder { get; set; } = "";

        public string? StepId { get; set; }

        /// <summary>Where it was found, as a person reads it: "step 'Fetch', input 'Url'", "a note", "variable 'who'".</summary>
        public string Where { get; set; } = "";

        /// <summary>The text around what was found, on one line.</summary>
        public string Snippet { get; set; } = "";
    }

    /// <summary>
    /// BL-229 - find in every pipeline (beyond n8n, whose search reads names): where a text is used in a pipeline's graph - a
    /// step's name and comment, its inputs as written (values, expressions, scripts, conditions, cases), a note, a variable's
    /// name and starting value - so a connection, a site table or a <c>$vars</c> name is found before it is renamed or removed.
    /// An input its step marks SENSITIVE is never read, and nor is a kept test value: a search must not tell, letter by letter,
    /// what a secret is. Case is ignored.
    /// </summary>
    public static class PipelineContentSearch
    {
        /// <summary>The shortest text searched for.</summary>
        public const int MinLength = 2;

        /// <summary>The most places one search answers with.</summary>
        public const int MaxResults = 200;

        private const int Around = 30;

        /// <summary>Where <paramref name="text"/> is in <paramref name="graph"/>, in the graph's order; nothing for a text too short.</summary>
        public static List<(string? StepId, string Where, string Snippet)> Find(PipelineGraph? graph, string? text, IEnumerable<StepCatalogEntry>? catalog)
        {
            var found = new List<(string?, string, string)>();
            var sought = text?.Trim() ?? "";
            if (graph is null || sought.Length < MinLength)
            {
                return found;
            }

            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry?.Type is not null)
                .GroupBy(entry => entry.Type).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            void Look(string? stepId, string where, string? value)
            {
                if (Snippet(value, sought) is { } snippet)
                {
                    found.Add((stepId, where, snippet));
                }
            }

            foreach (var step in graph.Steps ?? new List<PipelineGraphStep>())
            {
                if (step is null)
                {
                    continue;
                }

                entries.TryGetValue(step.Type ?? "", out var entry);
                var called = string.IsNullOrWhiteSpace(step.Name) ? $"step '{entry?.DisplayName ?? step.Type}'" : $"step '{step.Name}'";
                bool Secret(string input) => entry?.Inputs?.FirstOrDefault(candidate => candidate.Name == input)?.IsSensitive == true;
                string Named(string input) => entry?.Inputs?.FirstOrDefault(candidate => candidate.Name == input)?.DisplayName is { Length: > 0 } shown ? shown : input;

                Look(step.Id, called + ", its name", step.Name);
                Look(step.Id, called + ", its comment", step.Comment);
                foreach (var (input, value) in step.Inputs ?? new Dictionary<string, string>())
                {
                    if (!Secret(input))
                    {
                        Look(step.Id, $"{called}, input '{Named(input)}'", value);
                    }
                }

                foreach (var (input, value) in step.InputExpressions ?? new Dictionary<string, string>())
                {
                    if (!Secret(input))
                    {
                        Look(step.Id, $"{called}, the expression of input '{Named(input)}'", value);
                    }
                }

                foreach (var (input, value) in step.InputScripts ?? new Dictionary<string, string>())
                {
                    if (!Secret(input))
                    {
                        Look(step.Id, $"{called}, the JavaScript of input '{Named(input)}'", value);
                    }
                }

                foreach (var (input, condition) in step.InputConditions ?? new Dictionary<string, PipelineCondition>())
                {
                    Look(step.Id, $"{called}, the condition of input '{Named(input)}'", condition is null ? null : PipelineConditions.Describe(condition));
                }

                foreach (var @case in step.Cases ?? new List<PipelineGraphCase>())
                {
                    Look(step.Id, $"{called}, case '{@case?.Label}'", @case is null ? null : @case.Label + " " + PipelineConditions.Describe(@case.Condition));
                }
            }

            foreach (var note in graph.Notes ?? new List<PipelineGraphNote>())
            {
                Look(note?.Parent, "a note", note?.Text);
            }

            foreach (var variable in graph.Variables ?? new List<PipelineGraphVariable>())
            {
                Look(null, $"variable '{variable?.Name}'", variable?.Name);
                Look(null, $"variable '{variable?.Name}', its starting value", variable?.Value);
            }

            return found;
        }

        // The text around the first place the sought text is, on one line - or null when it is not there.
        private static string? Snippet(string? value, string sought)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var at = value.IndexOf(sought, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            var start = Math.Max(0, at - Around);
            var end = Math.Min(value.Length, at + sought.Length + Around);
            var line = string.Join(' ', value[start..end].Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return (start > 0 ? "…" : "") + line + (end < value.Length ? "…" : "");
        }
    }
}
