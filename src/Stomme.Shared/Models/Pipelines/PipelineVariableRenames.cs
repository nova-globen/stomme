#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A variable renamed, and with it every expression, condition and case that reads it (n8n renames a node and the
    /// expressions that read it follow; data moves through variables here). A binding names the variable by its id and needs
    /// nothing. A value is sent as typed - text for another system may hold <c>{{ }}</c> of its own - so it is never changed;
    /// a script reads the variables by name, and a record's rules name the record's fields first, so the editor cannot tell what
    /// they mean: the steps holding the old name there are said, to be checked, rather than changed.
    /// </summary>
    public static class PipelineVariableRenames
    {
        private static readonly Regex Readable = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        /// <summary>Why <paramref name="name"/> cannot be the new name of the variable <paramref name="id"/>; null when it can.</summary>
        public static string? Problem(string? name, IEnumerable<PipelineGraphVariable> variables, string id)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return "A variable needs a name.";
            }

            if (!Readable.IsMatch(trimmed))
            {
                return $"'{trimmed}' could not be read in an expression - use letters, digits and _, starting with a letter or _.";
            }

            return (variables ?? Enumerable.Empty<PipelineGraphVariable>()).Any(variable => variable?.Id != id && string.Equals(variable?.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                ? $"There is already a variable named '{trimmed}'."
                : null;
        }

        /// <summary>What a rename changed: how many expressions and conditions now read the new name, and the steps to check.</summary>
        public sealed record Outcome(int Places, IReadOnlyList<string> ToCheck);

        /// <summary>Renames the variable <paramref name="id"/> in <paramref name="graph"/> - in place - to <paramref name="name"/>.</summary>
        public static Outcome Apply(PipelineGraph graph, string id, string name, IEnumerable<StepCatalogEntry> catalog)
        {
            var variable = graph.Variables.First(candidate => candidate.Id == id);
            var from = variable.Name;
            var to = name.Trim();
            variable.Name = to;
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                return new Outcome(0, Array.Empty<string>());
            }

            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry?.Type is not null)
                .GroupBy(entry => entry.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var places = 0;
            var toCheck = new List<string>();
            foreach (var step in graph.Steps.Where(step => step is not null))
            {
                foreach (var input in (step.InputExpressions ?? new Dictionary<string, string>()).Keys.ToList())
                {
                    var renamed = PipelineTemplate.Rename(step.InputExpressions[input], from, to);
                    if (!string.Equals(renamed, step.InputExpressions[input], StringComparison.Ordinal))
                    {
                        step.InputExpressions[input] = renamed!;
                        places++;
                    }
                }

                foreach (var condition in (step.InputConditions ?? new Dictionary<string, PipelineCondition>()).Values.Concat((step.Cases ?? new List<PipelineGraphCase>()).Select(@case => @case?.Condition)))
                {
                    places += Rename(condition, from, to);
                }

                entries.TryGetValue(step.Type ?? "", out var entry);
                var recordRules = (entry?.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.RecordCondition).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
                var scripts = (step.InputScripts ?? new Dictionary<string, string>()).Values.Any(script => script is not null && Regex.IsMatch(script, @"(?<![\w$.])" + Regex.Escape(from) + @"(?![\w$])", RegexOptions.CultureInvariant));
                var rules = (step.Inputs ?? new Dictionary<string, string>()).Any(pair => recordRules.Contains(pair.Key) && PipelineTemplate.Names(pair.Value).Contains(from));
                if (scripts || rules)
                {
                    toCheck.Add(!string.IsNullOrWhiteSpace(step.Name) ? step.Name.Trim() : entry?.DisplayName ?? step.Type ?? step.Id);
                }
            }

            return new Outcome(places, toCheck);
        }

        // A condition's rules reading the new name - each side a template; how many rules changed.
        private static int Rename(PipelineCondition? condition, string from, string to)
        {
            var changed = 0;
            foreach (var rule in condition?.Rules ?? new List<PipelineConditionRule>())
            {
                var left = PipelineTemplate.Rename(rule.Left, from, to);
                var right = PipelineTemplate.Rename(rule.Right, from, to);
                if (!string.Equals(left, rule.Left, StringComparison.Ordinal) || !string.Equals(right, rule.Right, StringComparison.Ordinal))
                {
                    rule.Left = left!;
                    rule.Right = right!;
                    changed++;
                }
            }

            return changed;
        }
    }
}
