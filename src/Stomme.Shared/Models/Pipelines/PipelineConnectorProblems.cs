using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - n8n marks a node's required field until it is filled. A step that calls one of its connection's operations
    /// gives that operation's parameters as 'name = value' lines; each parameter the operation needs and has no default for,
    /// missing from them - or given no value - is a warning on the step, naming the line to add. A Query box gives
    /// <c>query</c>. Run by the editor, which knows each connection's connector; an operation chosen by an expression or a
    /// script, or parameters written as a script, only the run can say.
    /// </summary>
    public static class PipelineConnectorProblems
    {
        /// <summary>An operation's required parameter its step does not give.</summary>
        public const string MissingParameter = "MissingParameter";

        public const string ParametersInput = "Parameters";

        public const string QueryInput = "Query";

        /// <summary>The parameter a step's Query box gives the connector.</summary>
        public const string QueryParameter = "query";

        public static List<PipelineGraphProblem> Find(PipelineGraph graph, IEnumerable<StepCatalogEntry> catalog, Func<string, ConnectorView> connectorOf)
        {
            var problems = new List<PipelineGraphProblem>();
            if (graph?.Steps is null || connectorOf is null)
            {
                return problems;
            }

            var entries = new Dictionary<string, StepCatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in catalog ?? Enumerable.Empty<StepCatalogEntry>())
            {
                if (entry?.Type is not null && !entries.ContainsKey(entry.Type))
                {
                    entries[entry.Type] = entry;
                }
            }

            foreach (var step in graph.Steps.Where(step => step is not null && !step.Disabled && !string.IsNullOrWhiteSpace(step.Id)))
            {
                if (!entries.TryGetValue(step.Type ?? "", out var entry) || entry.Inputs is null)
                {
                    continue;
                }

                var connectionInput = entry.Inputs.FirstOrDefault(input => input.UIHint == PipelineInputHints.ConnectionPicker)?.Name;
                var operationInput = entry.Inputs.FirstOrDefault(input => input.UIHint == PipelineInputHints.ConnectorOperation)?.Name;
                if (connectionInput is null || operationInput is null || !entry.Inputs.Any(input => input.Name == ParametersInput))
                {
                    continue;
                }

                if (Written(step, operationInput) || Plain(step, connectionInput) is not { Length: > 0 } connection || connectorOf(connection.Trim()) is not { } connector)
                {
                    continue;
                }

                var operations = connector.Operations ?? Array.Empty<ConnectorOperationView>();
                var named = Plain(step, operationInput);
                var operation = string.IsNullOrWhiteSpace(named)
                    ? operations.FirstOrDefault()
                    : operations.FirstOrDefault(known => string.Equals(known.Name, named.Trim(), StringComparison.OrdinalIgnoreCase));
                if (operation?.Parameters is null || step.InputScripts is not null && step.InputScripts.TryGetValue(ParametersInput, out var script) && script is not null)
                {
                    continue;
                }

                var given = Names(step.InputExpressions is not null && step.InputExpressions.TryGetValue(ParametersInput, out var template) && template is not null ? template : Plain(step, ParametersInput));
                if (Plain(step, QueryInput) is { Length: > 0 } || Written(step, QueryInput))
                {
                    given.Add(QueryParameter);
                }

                foreach (var field in operation.Parameters.Where(field => field.Required && string.IsNullOrEmpty(field.Default) && !given.Contains(field.Name)))
                {
                    problems.Add(new PipelineGraphProblem
                    {
                        StepId = step.Id,
                        Step = string.IsNullOrWhiteSpace(step.Name) ? (entry.DisplayName ?? step.Type) : step.Name.Trim(),
                        Input = ParametersInput,
                        Severity = PipelineGraphProblems.Warning,
                        Code = MissingParameter,
                        Message = field.Name == QueryParameter
                            ? $"{operation.DisplayName} needs {field.DisplayName} - write it in Query."
                            : $"{operation.DisplayName} needs {field.DisplayName} - add '{field.Name} = ...' to Parameters.",
                    });
                }
            }

            return problems;
        }

        // An input's value as typed: null when it is an expression or a script, or not given.
        private static string Plain(PipelineGraphStep step, string input) =>
            Written(step, input) ? null : step.Inputs is not null && step.Inputs.TryGetValue(input, out var value) ? value : null;

        // An input given by an expression or a script - what it holds only the run knows.
        private static bool Written(PipelineGraphStep step, string input) =>
            step.InputExpressions is not null && step.InputExpressions.TryGetValue(input, out var template) && template is not null
            || step.InputScripts is not null && step.InputScripts.TryGetValue(input, out var script) && script is not null;

        // The names the 'name = value' lines give a value, in any case.
        private static HashSet<string> Names(string lines)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (lines ?? "").Split('\n'))
            {
                var at = line.IndexOf('=');
                if (at > 0 && line[(at + 1)..].Trim().Length > 0)
                {
                    names.Add(line[..at].Trim());
                }
            }

            return names;
        }
    }
}
