#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// n8n's "Convert to sub-workflow": steps of a pipeline moved into a pipeline of their own, which the pipeline then
    /// calls where they stood (Execute pipeline, waiting for it, failing when it fails). The steps go on doing what they did: the
    /// variables they read are sent with the call and set from its input first thing, and the variables they set that the rest
    /// of the pipeline reads are given back as the called pipeline's output and set again after the call. Steps that cannot
    /// move so are refused, saying why - one way in, one way out, no way to end among them while the rest goes on.
    /// </summary>
    public static class PipelineSubPipelines
    {
        public const string CallType = "Stomme.ExecutePipeline";

        public const string SetVariableType = "Stomme.SetVariable";

        public const string SetOutputType = "Stomme.SetOutput";

        /// <summary>The caller's variable the call's result is saved to - what is given back is read from its output.</summary>
        public const string ResultVariableName = "subPipeline";

        // Where the moved steps' own flowchart begins in the called pipeline, and how far apart the steps it adds stand.
        private const double Column = 260;

        /// <summary>What moving the chosen steps does - or why they cannot move.</summary>
        public sealed class Plan
        {
            /// <summary>Why the steps cannot move; null when they can.</summary>
            public string? Refusal { get; init; }

            /// <summary>Every step that moves: the ones chosen and every step inside their bodies.</summary>
            public IReadOnlyList<string> Moved { get; init; } = Array.Empty<string>();

            /// <summary>The variables the moved steps read, sent with the call.</summary>
            public IReadOnlyList<PipelineGraphVariable> Sent { get; init; } = Array.Empty<PipelineGraphVariable>();

            /// <summary>The variables they set that the rest of the pipeline reads, given back.</summary>
            public IReadOnlyList<PipelineGraphVariable> GivenBack { get; init; } = Array.Empty<PipelineGraphVariable>();

            /// <summary>The called pipeline: its start, the variables taken from the call, the moved steps, what it gives back.</summary>
            public PipelineGraph? Called { get; init; }

            internal string Entry { get; init; } = "";

            internal (string Step, string? Port)? Exit { get; init; }

            internal string? Parent { get; init; }

            internal string? ParentPort { get; init; }

            internal static Plan Refused(string why) => new() { Refusal = why };
        }

        /// <summary>The plan for moving <paramref name="chosen"/> (step ids) out of <paramref name="graph"/>.</summary>
        public static Plan Make(PipelineGraph graph, IEnumerable<string> chosen, IEnumerable<StepCatalogEntry> catalog, Func<string> newId)
        {
            var steps = (graph?.Steps ?? new List<PipelineGraphStep>()).Where(step => !string.IsNullOrWhiteSpace(step?.Id)).ToList();
            var byId = steps.GroupBy(step => step.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry?.Type is not null)
                .GroupBy(entry => entry.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            StepCatalogEntry? EntryOf(PipelineGraphStep step) => entries.TryGetValue(step.Type ?? "", out var entry) ? entry : null;
            string Label(PipelineGraphStep step) => !string.IsNullOrWhiteSpace(step.Name) ? step.Name.Trim() : EntryOf(step)?.DisplayName ?? step.Type ?? step.Id;

            var picked = new HashSet<string>((chosen ?? Enumerable.Empty<string>()).Where(id => id is not null && byId.ContainsKey(id)), StringComparer.Ordinal);
            if (picked.Count == 0)
            {
                return Plan.Refused("Choose the steps to move first.");
            }

            // The chosen steps and every step inside their bodies.
            var moved = new HashSet<string>(picked, StringComparer.Ordinal);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var step in steps.Where(step => step.Parent is not null && moved.Contains(step.Parent)))
                {
                    grew |= moved.Add(step.Id);
                }
            }

            // The ones not inside another that moves: they stand in one flowchart - the pipeline's own, or one body's.
            var top = steps.Where(step => moved.Contains(step.Id) && (step.Parent is null || !moved.Contains(step.Parent))).ToList();
            if (top.Select(step => (step.Parent ?? "") + "\u001f" + (step.ParentPort ?? "")).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                return Plan.Refused("The steps stand in different flowcharts - choose steps of one: the pipeline's own, or one body's.");
            }

            var variables = (graph!.Variables ?? new List<PipelineGraphVariable>()).Where(variable => !string.IsNullOrWhiteSpace(variable?.Id))
                .GroupBy(variable => variable.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
            foreach (var step in steps.Where(step => moved.Contains(step.Id)))
            {
                if (EntryOf(step)?.Kind == "Trigger" || step.Type is PipelineCallContracts.WhenCalledType or PipelineEvaluations.TriggerType)
                {
                    return Plan.Refused($"'{Label(step)}' starts the pipeline - a sub-pipeline is started by its caller.");
                }

                if (step.Type == PipelineWebhookAnswering.RespondStepType)
                {
                    return Plan.Refused($"'{Label(step)}' answers this pipeline's webhook call, so it stays with the Webhook.");
                }

                if (NamesRead(step).Contains(PipelineBuiltIns.Input))
                {
                    return Plan.Refused($"'{Label(step)}' reads {{{{ {PipelineBuiltIns.Input} }}}}, what this run was started with - moved, it would read what the call sends. Save what it needs to a variable first.");
                }
            }

            // A call address and the wait for it go together: the address is the run's own.
            bool Calls(PipelineGraphStep step) => step.Type is PipelineCalls.AddressStepType or PipelineCalls.WaitStepType;
            if (steps.Any(step => moved.Contains(step.Id) && Calls(step)) && steps.Any(step => !moved.Contains(step.Id) && Calls(step)))
            {
                return Plan.Refused("'Call address' and 'Wait for a call' go together - move both, or neither.");
            }

            var connections = (graph.Connections ?? new List<PipelineGraphConnection>())
                .Where(connection => connection is not null && connection.SourceId is not null && connection.TargetId is not null).ToList();
            var topIds = top.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
            var comingIn = connections.Where(connection => topIds.Contains(connection.TargetId) && !moved.Contains(connection.SourceId)).ToList();
            var goingOut = connections.Where(connection => topIds.Contains(connection.SourceId) && !moved.Contains(connection.TargetId)).ToList();
            var among = connections.Where(connection => topIds.Contains(connection.SourceId) && topIds.Contains(connection.TargetId)).ToList();

            // One way in: the one step reached from outside, or - reached from nowhere - the one nothing among them leads to.
            var reached = comingIn.Select(connection => connection.TargetId).Distinct(StringComparer.Ordinal).ToList();
            string entry;
            if (reached.Count > 1)
            {
                return Plan.Refused($"They are reached from outside at {Names(reached.Select(id => Label(byId[id])))} - the steps must have one way in.");
            }

            if (reached.Count == 1)
            {
                entry = reached[0];
            }
            else
            {
                var firsts = top.Where(step => !among.Any(connection => connection.TargetId == step.Id)).ToList();
                if (firsts.Count != 1)
                {
                    return Plan.Refused("They start at several steps - the steps must have one first step.");
                }

                entry = firsts[0].Id;
            }

            // Where a run can leave them: an outcome connected outside, or one leading nowhere - its end. A failure's outcome is
            // the called pipeline's to end at: the call fails with it.
            var leaves = new List<(string Step, string? Port, bool Outside)>();
            foreach (var step in top)
            {
                var ports = PipelineOutcomes.Ports(EntryOf(step), step.Inputs, step.Cases, step.FailedPath);
                foreach (var port in ports.Where(port => port != PipelineOutcomes.Failed))
                {
                    var from = connections.Where(connection => connection.SourceId == step.Id && SamePort(connection.SourcePort, port, ports)).ToList();
                    if (from.Count == 0)
                    {
                        leaves.Add((step.Id, port, false));
                    }
                    else if (from.Any(connection => !moved.Contains(connection.TargetId)))
                    {
                        if (from.Any(connection => moved.Contains(connection.TargetId)))
                        {
                            return Plan.Refused($"'{Label(step)}' goes on both among them and after them - the steps must have one way out.");
                        }

                        leaves.Add((step.Id, port, true));
                    }
                }
            }

            if (goingOut.Any(connection => connection.SourcePort == PipelineOutcomes.Failed))
            {
                return Plan.Refused("A failure among them goes on outside them - move the steps that handle it as well, or fewer.");
            }

            var outside = leaves.Where(leave => leave.Outside).ToList();
            if (outside.Count > 1)
            {
                return Plan.Refused($"They go on from {Names(outside.Select(leave => Label(byId[leave.Step]) + (leave.Port is null ? "" : $" ({leave.Port})")))} - the steps must have one way out.");
            }

            if (outside.Count == 1 && leaves.Count > 1)
            {
                var ending = leaves.First(leave => !leave.Outside);
                return Plan.Refused($"The run can end at '{Label(byId[ending.Step])}'{(ending.Port is null ? "" : $" ({ending.Port})")} among them while the rest goes on after them - moved, the rest would go on either way. Move the steps after it as well, or fewer.");
            }

            var written = new HashSet<string>(steps.Where(step => moved.Contains(step.Id)).SelectMany(step => Writes(step, EntryOf(step))), StringComparer.Ordinal);
            var readIn = Reads(steps.Where(step => moved.Contains(step.Id)), variables, EntryOf);
            var readOut = Reads(steps.Where(step => !moved.Contains(step.Id)), variables, EntryOf);
            var givenBack = variables.Where(variable => written.Contains(variable.Id) && readOut.Contains(variable.Id)).ToList();
            // What they read is sent - but not a variable that is always empty when the call comes: no starting value, set only
            // among them and not given back (found driving: 'report', set and then read among the moved steps, was "sent").
            var writtenOut = new HashSet<string>(steps.Where(step => !moved.Contains(step.Id)).SelectMany(step => Writes(step, EntryOf(step))), StringComparer.Ordinal);
            var sent = variables.Where(variable => readIn.Contains(variable.Id)
                && !(string.IsNullOrEmpty(variable.Value) && written.Contains(variable.Id) && !writtenOut.Contains(variable.Id) && !givenBack.Contains(variable))).ToList();
            if (givenBack.Count > 0 && leaves.Count != 1)
            {
                return Plan.Refused(leaves.Count == 0
                    ? $"The run never leaves them, so what they set ({Names(givenBack.Select(variable => variable.Name))}) could not be given back."
                    : $"The run can end at several of them, and what they set ({Names(givenBack.Select(variable => variable.Name))}) is given back from one place - move fewer steps, or bring the ends together.");
            }

            (string Step, string? Port)? exit = leaves.Count == 1 ? (leaves[0].Step, leaves[0].Port) : null;
            var called = Called(steps, moved, top, connections, variables, written, readIn, sent, givenBack, entry, exit, graph.Notes, newId);
            return new Plan
            {
                Moved = steps.Where(step => moved.Contains(step.Id)).Select(step => step.Id).ToList(),
                Sent = sent,
                GivenBack = givenBack,
                Called = called,
                Entry = entry,
                Exit = exit,
                Parent = top[0].Parent,
                ParentPort = top[0].ParentPort,
            };
        }

        /// <summary>
        /// The pipeline with the plan's steps moved out: in their place one step running the called pipeline (by
        /// <paramref name="calledDefinitionId"/>) and waiting for it, sent what they read; after it, what they set given back to
        /// the variables the rest reads. What led to them leads to the call; what they led to follows it.
        /// </summary>
        public static PipelineGraph Call(PipelineGraph graph, Plan plan, string calledDefinitionId, string calledName, Func<string> newId, int callVersion = 1)
        {
            if (plan.Refusal is not null || plan.Called is null)
            {
                throw new InvalidOperationException(plan.Refusal ?? "The steps cannot move.");
            }

            var moved = new HashSet<string>(plan.Moved, StringComparer.Ordinal);
            var entryStep = graph.Steps.First(step => step.Id == plan.Entry);
            var result = new PipelineGraph
            {
                Steps = graph.Steps.Where(step => step is not null && !moved.Contains(step.Id)).ToList(),
                Connections = new List<PipelineGraphConnection>(),
                Variables = graph.Variables.ToList(),
                Notes = (graph.Notes ?? new List<PipelineGraphNote>()).Where(note => note?.Parent is null || !moved.Contains(note.Parent)).ToList(),
                RunsAtOnce = graph.RunsAtOnce,
                KeepSuccessfulRuns = graph.KeepSuccessfulRuns,
                SaveProgress = graph.SaveProgress,
                KeepStepData = graph.KeepStepData,
                MinutesSavedPerRun = graph.MinutesSavedPerRun,
                TimeoutMinutes = graph.TimeoutMinutes,
                CalledBy = graph.CalledBy,
                TimeZone = graph.TimeZone,
            };

            var call = new PipelineGraphStep
            {
                Id = newId(),
                Type = CallType,
                Version = callVersion,
                Name = $"Run '{calledName}'",
                Position = new GraphPosition { X = entryStep.Position?.X ?? 0, Y = entryStep.Position?.Y ?? 0 },
                Parent = plan.Parent,
                ParentPort = plan.ParentPort,
                Inputs = { ["PipelineDefinitionId"] = calledDefinitionId, ["WaitForCompletion"] = "true" },
            };
            if (plan.Sent.Count > 0)
            {
                call.InputExpressions["Input"] = "{" + string.Join(", ", plan.Sent.Select(variable => JsonSerializer.Serialize(variable.Name) + ": {{ " + Path(variable.Name) + " | json }}")) + "}";
            }

            result.Steps.Add(call);
            var last = call;
            if (plan.GivenBack.Count > 0)
            {
                var raw = newId();
                var holder = new PipelineGraphVariable { Id = "var-" + (raw.Length > 12 ? raw[..12] : raw), Name = Unique(ResultVariableName, result.Variables), TypeName = "Object" };
                result.Variables.Add(holder);
                call.OutputVariables["Result"] = holder.Id;
                foreach (var variable in plan.GivenBack)
                {
                    var back = new PipelineGraphStep
                    {
                        Id = newId(),
                        Type = SetVariableType,
                        Name = $"{variable.Name} from '{calledName}'",
                        Position = new GraphPosition { X = last.Position.X + Column, Y = last.Position.Y },
                        Parent = plan.Parent,
                        ParentPort = plan.ParentPort,
                        InputVariables = { ["Variable"] = variable.Id },
                        InputExpressions = { ["Value"] = "{{ " + Path(holder.Name, "output", variable.Name) + " }}" },
                    };
                    result.Steps.Add(back);
                    result.Connections.Add(new PipelineGraphConnection { SourceId = last.Id, TargetId = back.Id });
                    last = back;
                }
            }

            foreach (var connection in graph.Connections.Where(connection => connection is not null))
            {
                var fromMoved = moved.Contains(connection.SourceId ?? "");
                var toMoved = moved.Contains(connection.TargetId ?? "");
                if (!fromMoved && !toMoved)
                {
                    result.Connections.Add(connection);
                }
                else if (!fromMoved && connection.TargetId == plan.Entry)
                {
                    result.Connections.Add(new PipelineGraphConnection { SourceId = connection.SourceId, SourcePort = connection.SourcePort, TargetId = call.Id });
                }
                else if (fromMoved && !toMoved)
                {
                    result.Connections.Add(new PipelineGraphConnection { SourceId = last.Id, TargetId = connection.TargetId });
                }
            }

            return result;
        }

        // The called pipeline: "When called by another pipeline", each variable sent set from the call's input, the moved steps
        // as they were (their bodies too), and each variable given back set as its output where the run leaves them.
        private static PipelineGraph Called(List<PipelineGraphStep> steps, HashSet<string> moved, List<PipelineGraphStep> top, List<PipelineGraphConnection> connections,
            List<PipelineGraphVariable> variables, HashSet<string> written, HashSet<string> readIn, List<PipelineGraphVariable> sent, List<PipelineGraphVariable> givenBack,
            string entry, (string Step, string? Port)? exit, List<PipelineGraphNote>? notes, Func<string> newId)
        {
            var graph = new PipelineGraph
            {
                Variables = variables.Where(variable => readIn.Contains(variable.Id) || written.Contains(variable.Id))
                    .Select(variable => new PipelineGraphVariable { Id = variable.Id, Name = variable.Name, TypeName = variable.TypeName, Value = variable.Value, TestValue = variable.TestValue }).ToList(),
            };

            var left = top.Min(step => step.Position?.X ?? 0);
            var upper = top.Min(step => step.Position?.Y ?? 0);
            var start = new PipelineGraphStep { Id = newId(), Type = PipelineCallContracts.WhenCalledType, Name = "When called", Position = new GraphPosition { X = 0, Y = upper } };
            graph.Steps.Add(start);
            var previous = start;
            foreach (var variable in sent)
            {
                var take = new PipelineGraphStep
                {
                    Id = newId(),
                    Type = SetVariableType,
                    Name = $"{variable.Name} from the caller",
                    Position = new GraphPosition { X = previous.Position.X + Column, Y = upper },
                    InputVariables = { ["Variable"] = variable.Id },
                    InputExpressions = { ["Value"] = "{{ " + Path(PipelineBuiltIns.Input, variable.Name) + " }}" },
                };
                graph.Steps.Add(take);
                graph.Connections.Add(new PipelineGraphConnection { SourceId = previous.Id, TargetId = take.Id });
                previous = take;
            }

            // The moved steps keep their places among themselves, to the right of what was added before them.
            var shift = previous.Position.X + Column - left;
            foreach (var step in steps.Where(step => moved.Contains(step.Id)))
            {
                var copy = Copy(step);
                if (top.Contains(step))
                {
                    copy.Parent = null;
                    copy.ParentPort = null;
                    copy.Position = new GraphPosition { X = (step.Position?.X ?? 0) + shift, Y = step.Position?.Y ?? 0 };
                }

                graph.Steps.Add(copy);
            }

            graph.Connections.Add(new PipelineGraphConnection { SourceId = previous.Id, TargetId = entry });
            graph.Connections.AddRange(connections.Where(connection => moved.Contains(connection.SourceId) && moved.Contains(connection.TargetId))
                .Select(connection => new PipelineGraphConnection { SourceId = connection.SourceId, SourcePort = connection.SourcePort, TargetId = connection.TargetId }));
            graph.Notes.AddRange((notes ?? new List<PipelineGraphNote>()).Where(note => note?.Parent is not null && moved.Contains(note.Parent))
                .Select(note => new PipelineGraphNote { Id = note.Id, Text = note.Text, Position = new GraphPosition { X = note.Position?.X ?? 0, Y = note.Position?.Y ?? 0 }, Parent = note.Parent, ParentPort = note.ParentPort, Color = note.Color }));

            if (givenBack.Count > 0 && exit is { } leave)
            {
                var from = graph.Steps.First(step => step.Id == leave.Step);
                (string Id, string? Port) at = (from.Id, leave.Port);
                var x = from.Position.X;
                foreach (var variable in givenBack)
                {
                    x += Column;
                    var give = new PipelineGraphStep
                    {
                        Id = newId(),
                        Type = SetOutputType,
                        Name = $"Give back {variable.Name}",
                        Position = new GraphPosition { X = x, Y = from.Position.Y },
                        Inputs = { ["OutputName"] = variable.Name },
                        InputExpressions = { ["OutputValue"] = "{{ " + Path(variable.Name) + " }}" },
                    };
                    graph.Steps.Add(give);
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = at.Id, SourcePort = at.Port, TargetId = give.Id });
                    at = (give.Id, null);
                }
            }

            return graph;
        }

        // The variables (by id) the steps read: bound to an input, named in an expression, a condition, a case or a value - and a
        // script's words that are a variable's name (a script reads the variables by name).
        private static HashSet<string> Reads(IEnumerable<PipelineGraphStep> steps, List<PipelineGraphVariable> variables, Func<PipelineGraphStep, StepCatalogEntry?> entryOf)
        {
            var read = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in steps)
            {
                var picks = (entryOf(step)?.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.VariablePicker).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var (input, id) in step.InputVariables ?? new Dictionary<string, string>())
                {
                    if (!picks.Contains(input) && !string.IsNullOrWhiteSpace(id))
                    {
                        read.Add(id.Trim());
                    }
                }

                var names = NamesRead(step);
                foreach (var variable in variables)
                {
                    if (names.Contains(variable.Name) || (step.InputScripts ?? new Dictionary<string, string>()).Values.Any(script => script is not null
                        && Regex.IsMatch(script, @"(?<![\w$.])" + Regex.Escape(variable.Name) + @"(?![\w$])", RegexOptions.CultureInvariant)))
                    {
                        read.Add(variable.Id);
                    }
                }
            }

            return read;
        }

        // The variable names a step's expressions, conditions, cases and values read by {{ }} - built-ins among them.
        private static HashSet<string> NamesRead(PipelineGraphStep step)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var template in (step.InputExpressions ?? new Dictionary<string, string>()).Values.Concat((step.Inputs ?? new Dictionary<string, string>()).Values))
            {
                names.UnionWith(PipelineTemplate.Names(template));
            }

            foreach (var condition in (step.InputConditions ?? new Dictionary<string, PipelineCondition>()).Values.Concat((step.Cases ?? new List<PipelineGraphCase>()).Select(@case => @case?.Condition)))
            {
                names.UnionWith(PipelineConditions.Names(condition));
            }

            return names;
        }

        // The variables (by id) a step sets: its outputs saved, the one it sets (Set Variable), the one its failure is saved to.
        private static IEnumerable<string> Writes(PipelineGraphStep step, StepCatalogEntry? entry)
        {
            foreach (var id in (step.OutputVariables ?? new Dictionary<string, string>()).Values)
            {
                yield return id;
            }

            foreach (var input in (entry?.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.VariablePicker))
            {
                if (step.InputVariables is not null && step.InputVariables.TryGetValue(input.Name, out var id))
                {
                    yield return id;
                }
            }

            if (!string.IsNullOrWhiteSpace(step.FailureVariable))
            {
                yield return step.FailureVariable;
            }
        }

        // A connection leaves by the port: the same name - or, from a step with one outcome besides a failure's, either unnamed.
        private static bool SamePort(string? connected, string? port, IReadOnlyList<string?> ports) =>
            string.Equals(connected ?? "", port ?? "", StringComparison.Ordinal)
            || (port != PipelineOutcomes.Failed && connected != PipelineOutcomes.Failed && ports.Count(candidate => candidate != PipelineOutcomes.Failed) == 1
                && (string.IsNullOrEmpty(connected) || string.IsNullOrEmpty(port)));

        // A path an expression reads by: a name, then each field - a name that is not a plain word read as ["name"].
        private static string Path(string name, params string[] fields)
        {
            static string Field(string field) => Regex.IsMatch(field, @"^[A-Za-z_][A-Za-z0-9_]*$") ? "." + field : "[" + JsonSerializer.Serialize(field) + "]";
            return name + string.Concat(fields.Select(Field));
        }

        private static string Unique(string name, IEnumerable<PipelineGraphVariable> variables)
        {
            var taken = variables.Select(variable => variable.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = name;
            for (var n = 2; taken.Contains(candidate); n++)
            {
                candidate = name + n;
            }

            return candidate;
        }

        private static string Names(IEnumerable<string> names)
        {
            var list = names.Select(name => "'" + name + "'").ToList();
            return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
        }

        private static PipelineGraphStep Copy(PipelineGraphStep step) => new()
        {
            Id = step.Id,
            Type = step.Type,
            Version = step.Version,
            Name = step.Name,
            Position = new GraphPosition { X = step.Position?.X ?? 0, Y = step.Position?.Y ?? 0 },
            Comment = step.Comment,
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
            Parent = step.Parent,
            ParentPort = step.ParentPort,
        };
    }
}
