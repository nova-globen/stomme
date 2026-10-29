using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// Where a variable is used: the steps that SET it (an output saved to it, a Set Variable step's variable) and the
    /// steps that READ it (an input bound to it, an expression or a condition naming it, a script naming it - a script is matched
    /// by its words, so it may be a namesake; the editor says so). What the variables panel lists beside each variable, and what
    /// tells a variable nothing reads from one a step does.
    /// </summary>
    public static class PipelineVariableUses
    {
        /// <summary>The ids of the steps that set the variable and of those that read it, each in the graph's order, each once.</summary>
        public sealed record Uses(IReadOnlyList<string> SetBy, IReadOnlyList<string> ReadBy);

        public static Uses Of(PipelineGraph graph, string variableId, IEnumerable<StepCatalogEntry> catalog)
        {
            var variable = graph?.Variables?.FirstOrDefault(candidate => candidate?.Id == variableId);
            if (variable is null)
            {
                return new Uses(Array.Empty<string>(), Array.Empty<string>());
            }

            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry?.Type is not null)
                .GroupBy(entry => entry.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var setBy = new List<string>();
            var readBy = new List<string>();
            foreach (var step in graph.Steps.Where(step => !string.IsNullOrEmpty(step?.Id)))
            {
                entries.TryGetValue(step.Type ?? "", out var entry);
                var picks = (entry?.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.VariablePicker).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
                var bound = (step.InputVariables ?? new Dictionary<string, string>()).Where(pair => pair.Value == variable.Id).Select(pair => pair.Key).ToList();
                if ((step.OutputVariables ?? new Dictionary<string, string>()).Values.Contains(variable.Id) || bound.Any(picks.Contains))
                {
                    setBy.Add(step.Id);
                }

                if (bound.Any(input => !picks.Contains(input)) || Reads(step, variable.Name))
                {
                    readBy.Add(step.Id);
                }
            }

            return new Uses(setBy, readBy);
        }

        // An expression, a condition or a Switch case naming it, or a script with it as a word.
        private static bool Reads(PipelineGraphStep step, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var templates = (step.InputExpressions ?? new Dictionary<string, string>()).Values
                .Concat((step.InputConditions ?? new Dictionary<string, PipelineCondition>()).Values.Concat((step.Cases ?? new List<PipelineGraphCase>()).Select(@case => @case?.Condition))
                    .SelectMany(condition => condition?.Rules ?? new List<PipelineConditionRule>())
                    .SelectMany(rule => new[] { rule?.Left, rule?.Right }));
            return templates.Any(template => PipelineTemplate.Names(template).Contains(name))
                || (step.InputScripts ?? new Dictionary<string, string>()).Values.Any(script => script is not null
                    && Regex.IsMatch(script, @"(?<![\w$.])" + Regex.Escape(name) + @"(?![\w$])", RegexOptions.CultureInvariant));
        }
    }
}
