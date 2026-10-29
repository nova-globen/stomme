using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// Copy and paste on the canvas. A copy is the chosen steps, EVERYTHING they hold (a loop's body, an
    /// If's branches, however deep) and the connections among them; a connection to a step left out is left out. A paste gives
    /// every step a new id, puts the copied steps into the body the canvas shows at an offset, keeps what they held inside them,
    /// and rewires the copied connections to the new ids - so a step pasted twice is two independent steps.
    /// </summary>
    public static class StepClipboard
    {
        public const double PasteOffset = 40;

        public static PipelineGraph Copy(PipelineGraph graph, IEnumerable<string> stepIds)
        {
            var steps = graph?.Steps ?? new List<PipelineGraphStep>();
            var chosen = new HashSet<string>(stepIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var taken = new HashSet<string>(chosen, StringComparer.Ordinal);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var step in steps)
                {
                    if (step.Parent is not null && taken.Contains(step.Parent) && taken.Add(step.Id))
                    {
                        grew = true;
                    }
                }
            }

            return new PipelineGraph
            {
                Steps = steps.Where(step => taken.Contains(step.Id)).Select(step => Clone(step, step.Id, chosen.Contains(step.Id) ? null : step.Parent, chosen.Contains(step.Id) ? null : step.ParentPort, 0)).ToList(),
                Connections = (graph?.Connections ?? new List<PipelineGraphConnection>())
                    .Where(connection => taken.Contains(connection.SourceId) && taken.Contains(connection.TargetId))
                    .Select(connection => new PipelineGraphConnection { SourceId = connection.SourceId, SourcePort = connection.SourcePort, TargetId = connection.TargetId })
                    .ToList(),
            };
        }

        /// <summary>The copy's steps and connections with new ids, its top steps in the given body (null for the pipeline itself).</summary>
        public static PipelineGraph Paste(PipelineGraph copied, string parent, string parentPort, Func<string> newId, double offset = PasteOffset)
        {
            ArgumentNullException.ThrowIfNull(newId);
            var ids = (copied?.Steps ?? new List<PipelineGraphStep>()).ToDictionary(step => step.Id, _ => newId(), StringComparer.Ordinal);
            return new PipelineGraph
            {
                Steps = (copied?.Steps ?? new List<PipelineGraphStep>()).Select(step => step.Parent is null
                        ? Clone(step, ids[step.Id], parent, parent is null ? null : parentPort, offset)
                        : Clone(step, ids[step.Id], ids.TryGetValue(step.Parent, out var holder) ? holder : step.Parent, step.ParentPort, 0))
                    .ToList(),
                Connections = (copied?.Connections ?? new List<PipelineGraphConnection>())
                    .Where(connection => ids.ContainsKey(connection.SourceId) && ids.ContainsKey(connection.TargetId))
                    .Select(connection => new PipelineGraphConnection { SourceId = ids[connection.SourceId], SourcePort = connection.SourcePort, TargetId = ids[connection.TargetId] })
                    .ToList(),
            };
        }

        /// <summary>
        /// The variables a copy uses, so it can travel to another pipeline (the system clipboard): those its steps read or save to
        /// by id, and those an expression, a condition or a script names.
        /// </summary>
        public static List<PipelineGraphVariable> VariablesUsed(PipelineGraph copy, IEnumerable<PipelineGraphVariable> variables)
        {
            var steps = copy?.Steps ?? new List<PipelineGraphStep>();
            var ids = new HashSet<string>(steps.SelectMany(step => (step.InputVariables ?? new Dictionary<string, string>()).Values
                .Concat((step.OutputVariables ?? new Dictionary<string, string>()).Values)
                .Concat(string.IsNullOrEmpty(step.FailureVariable) ? Enumerable.Empty<string>() : new[] { step.FailureVariable })), StringComparer.Ordinal);
            var names = new HashSet<string>(steps.SelectMany(step => (step.InputExpressions ?? new Dictionary<string, string>()).Values.SelectMany(PipelineTemplate.Names)
                .Concat((step.InputConditions ?? new Dictionary<string, PipelineCondition>()).Values.SelectMany(PipelineConditions.Names))
                .Concat(step.Cases?.SelectMany(@case => PipelineConditions.Names(@case.Condition)) ?? Enumerable.Empty<string>())), StringComparer.Ordinal);
            var scripts = string.Join("\n", steps.SelectMany(step => (step.InputScripts ?? new Dictionary<string, string>()).Values));
            return (variables ?? Enumerable.Empty<PipelineGraphVariable>())
                .Where(variable => ids.Contains(variable.Id) || names.Contains(variable.Name ?? "")
                    || (scripts.Length > 0 && !string.IsNullOrEmpty(variable.Name) && System.Text.RegularExpressions.Regex.IsMatch(scripts, @"(?<![\w$])" + System.Text.RegularExpressions.Regex.Escape(variable.Name) + @"(?![\w$])")))
                .Select(variable => new PipelineGraphVariable { Id = variable.Id, Name = variable.Name, TypeName = variable.TypeName, Value = variable.Value })
                .ToList();
        }

        /// <summary>
        /// The variables a pasted copy brings, against those the pipeline has: one the pipeline has by id is that one; by name, the
        /// pipeline's (the copy's bindings re-pointed to it); otherwise it is added. Returns the variables to add; <paramref name="pasted"/>'s
        /// steps are re-pointed in place.
        /// </summary>
        public static List<PipelineGraphVariable> MergeVariables(PipelineGraph pasted, IEnumerable<PipelineGraphVariable> brought, IReadOnlyCollection<PipelineGraphVariable> existing)
        {
            var added = new List<PipelineGraphVariable>();
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var variable in brought ?? Enumerable.Empty<PipelineGraphVariable>())
            {
                if (string.IsNullOrEmpty(variable?.Id) || existing.Any(have => have.Id == variable.Id))
                {
                    continue;
                }

                if (existing.FirstOrDefault(have => string.Equals(have.Name, variable.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
                {
                    remap[variable.Id] = same.Id;
                }
                else if (added.All(other => !string.Equals(other.Name, variable.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    added.Add(new PipelineGraphVariable { Id = variable.Id, Name = variable.Name, TypeName = variable.TypeName, Value = variable.Value });
                }
            }

            foreach (var step in pasted?.Steps ?? new List<PipelineGraphStep>())
            {
                foreach (var bindings in new[] { step.InputVariables, step.OutputVariables })
                {
                    foreach (var key in (bindings ?? new Dictionary<string, string>()).Keys.ToList())
                    {
                        if (remap.TryGetValue(bindings[key], out var to))
                        {
                            bindings[key] = to;
                        }
                    }
                }

                if (step.FailureVariable is not null && remap.TryGetValue(step.FailureVariable, out var failureTo))
                {
                    step.FailureVariable = failureTo;
                }
            }

            return added;
        }

        private static PipelineGraphStep Clone(PipelineGraphStep step, string id, string parent, string parentPort, double offset) => new()
        {
            Id = id,
            Type = step.Type,
            Version = step.Version,
            Name = step.Name,
            Position = new GraphPosition { X = (step.Position?.X ?? 0) + offset, Y = (step.Position?.Y ?? 0) + offset },
            Comment = step.Comment,
            PinnedResult = step.PinnedResult,
            Retries = step.Retries,
            RetryDelaySeconds = step.RetryDelaySeconds,
            ContinueOnError = step.ContinueOnError,
            FailedPath = step.FailedPath,
            FailureVariable = step.FailureVariable,
            Disabled = step.Disabled,
            Inputs = new Dictionary<string, string>(step.Inputs ?? new Dictionary<string, string>()),
            InputVariables = new Dictionary<string, string>(step.InputVariables ?? new Dictionary<string, string>()),
            InputExpressions = new Dictionary<string, string>(step.InputExpressions ?? new Dictionary<string, string>()),
            InputScripts = new Dictionary<string, string>(step.InputScripts ?? new Dictionary<string, string>()),
            InputConditions = (step.InputConditions ?? new Dictionary<string, PipelineCondition>()).ToDictionary(pair => pair.Key, pair => PipelineConditions.Copy(pair.Value)),
            Cases = PipelineOutcomes.Copy(step.Cases),
            OutputVariables = new Dictionary<string, string>(step.OutputVariables ?? new Dictionary<string, string>()),
            Parent = parent,
            ParentPort = parent is null ? null : parentPort,
        };
    }
}
