#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Engine.Pipelines;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Expressions;
using InputDescriptor = Stomme.Engine.Pipelines.Models.InputDescriptor;
using StepDescriptor = Stomme.Engine.Pipelines.Models.StepDescriptor;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// The engine's Flowchart document to the editor's <see cref="PipelineGraph"/> and back (BL-95 increment (c)).
    /// The wire format is the engine's own, measured 2026-09-26 by serialising a flowchart through the engine's step
    /// serializer: a step is <c>{"id","type","version","name","metadata":{"designer":{"position":{"x","y"}}},
    /// "&lt;input&gt;":{"typeName","expression":{"type":"Literal","value"}}}</c>, a connection is
    /// <c>{"source":{"step","port"},"target":{"step"}}</c>, and the start step is resolved by the engine
    /// (an explicit Start step, else the step nothing points at), so the graph carries no start marker. Only LITERAL
    /// inputs are edited here; an input holding another expression kind is kept as the engine wrote it.
    /// </summary>
    internal static class PipelineGraphCodec
    {
        public const string FlowchartType = "Stomme.Flowchart";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private static readonly HashSet<string> StepEnvelope = new(StringComparer.Ordinal)
        {
            "id", "nodeId", "name", "type", "version", "customProperties", "metadata",
        };

        /// <summary>
        /// BL-229 (n8n's pinned data): a body without the pinned test values and the steps' pinned results - what anyone but an
        /// author is given. Both can be real data (a webhook call heard, a failure, an API's answer), so they reach an author only,
        /// as a run's data does.
        /// </summary>
        public static string? WithoutTestValues(string? body)
        {
            if (string.IsNullOrEmpty(body) || !(body.Contains("\"testValues\"", StringComparison.Ordinal) || body.Contains("\"pinnedResults\"", StringComparison.Ordinal)))
            {
                return body;
            }

            try
            {
                if (JsonNode.Parse(body) is JsonObject flowchart && flowchart["metadata"]?["designer"] is JsonObject designer
                    && designer.Remove("testValues") | designer.Remove("pinnedResults"))
                {
                    return flowchart.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // not a body this codec reads: it holds no pinned values it put there
            }

            return body;
        }

        /// <summary>The graph without the pinned test values and results (see <see cref="WithoutTestValues(string?)"/>).</summary>
        public static PipelineGraph? WithoutTestValues(PipelineGraph? graph)
        {
            foreach (var variable in graph?.Variables ?? new List<PipelineGraphVariable>())
            {
                variable.TestValue = null;
            }

            foreach (var step in graph?.Steps ?? new List<PipelineGraphStep>())
            {
                step.PinnedResult = null;
            }

            return graph;
        }

        /// <summary>The graph of a body; a body that is not a flowchart (a new pipeline's empty sequence) is an empty graph.</summary>
        public static PipelineGraph ToGraph(string? body)
        {
            var graph = new PipelineGraph();
            if (string.IsNullOrWhiteSpace(body))
            {
                return graph;
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return graph;
            }

            if (root is not JsonObject flowchart || flowchart["type"]?.GetValue<string>() != FlowchartType)
            {
                return graph;
            }

            ReadScope(flowchart, parent: null, port: null, graph);

            foreach (var node in flowchart["variables"] as JsonArray ?? new JsonArray())
            {
                if (node is JsonObject variable && variable["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    graph.Variables.Add(new PipelineGraphVariable
                    {
                        Id = id,
                        Name = variable["name"]?.GetValue<string>() ?? id,
                        TypeName = variable["typeName"]?.GetValue<string>() ?? "Object",
                        Value = variable["value"] is { } value ? LiteralText(value) : null,
                    });
                }
            }

            // BL-229: the pinned test values, back onto their variables (a value for a variable no longer there is dropped).
            if (flowchart["metadata"]?["designer"]?["testValues"] is JsonObject pinned)
            {
                foreach (var variable in graph.Variables)
                {
                    if (pinned[variable.Id] is JsonValue testValue && testValue.TryGetValue<string>(out var text))
                    {
                        variable.TestValue = text;
                    }
                }
            }

            // BL-229: the steps' pinned results, back onto their steps (one for a step no longer there is dropped).
            if (flowchart["metadata"]?["designer"]?["pinnedResults"] is JsonObject pins)
            {
                foreach (var step in graph.Steps)
                {
                    if (pins[step.Id] is JsonValue pin && pin.TryGetValue<string>(out var result))
                    {
                        step.PinnedResult = result;
                    }
                }
            }

            if (flowchart["metadata"]?["designer"]?["runsAtOnce"] is JsonValue runsAtOnce && runsAtOnce.TryGetValue<string>(out var setting)
                && PipelineRunsAtOnce.Normalize(setting) is { Length: > 0 } known)
            {
                graph.RunsAtOnce = known;
            }

            graph.KeepSuccessfulRuns = KeepsSuccessfulRuns(flowchart);
            graph.SaveProgress = flowchart["metadata"]?["designer"]?["saveProgress"] is JsonValue progress && progress.TryGetValue<bool>(out var saves) && saves ? true : null;
            graph.KeepStepData = KeepsStepData(flowchart);
            graph.MinutesSavedPerRun = MinutesSaved(flowchart);
            graph.TimeoutMinutes = flowchart["metadata"]?["designer"]?["timeoutMinutes"] is JsonValue limit && limit.TryGetValue<int>(out var limitMinutes)
                ? PipelineRunTimeout.Minutes(limitMinutes)
                : null;
            graph.CalledBy = CalledBy(flowchart);
            graph.TimeZone = flowchart["metadata"]?["designer"]?["timeZone"] is JsonValue zone && zone.TryGetValue<string>(out var zoneName)
                ? PipelineTimeZone.Normalize(zoneName)
                : null;
            return graph;
        }

        /// <summary>
        /// BL-229: the time zone a RUNNING pipeline reads time in, from its root step's designer metadata (as <see cref="TimeoutMinutes"/>
        /// reads its limit) - null for UTC.
        /// </summary>
        public static string? TimeZone(IDictionary<string, object>? rootMetadata)
        {
            if (rootMetadata is null || !rootMetadata.TryGetValue("designer", out var designer) || designer is null)
            {
                return null;
            }

            var value = designer switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } element => element.TryGetProperty("timeZone", out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null,
                JsonObject node => node["timeZone"] is JsonValue text && text.TryGetValue<string>(out var named) ? named : null,
                IDictionary<string, object> dictionary => dictionary.TryGetValue("timeZone", out var entry) ? entry as string : null,
                _ => null,
            };
            return PipelineTimeZone.Normalize(value);
        }

        /// <summary>BL-229: which pipelines a pipeline's body says may call it (<see cref="PipelineCallers"/>) - null for any.</summary>
        public static string? CalledBy(string? body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) is JsonObject flowchart ? CalledBy(flowchart) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A value there that is not text admits nobody: a setting that cannot be read does not open the pipeline to every caller.
        private static string? CalledBy(JsonObject flowchart) => flowchart["metadata"]?["designer"]?["calledBy"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var setting) => PipelineCallers.Normalize(setting),
            _ => PipelineCallers.Nobody,
        };

        /// <summary>
        /// BL-229: the time limit a RUNNING pipeline keeps on its root step's designer metadata - as the engine materialised it from
        /// the body (a JSON element), or as a pipeline built in code holds it (a JSON node or a dictionary); null for none.
        /// </summary>
        public static int? TimeoutMinutes(IDictionary<string, object>? rootMetadata)
        {
            if (rootMetadata is null || !rootMetadata.TryGetValue("designer", out var designer) || designer is null)
            {
                return null;
            }

            object? value = designer switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } element => element.TryGetProperty("timeoutMinutes", out var property) ? property : null,
                JsonObject node => node["timeoutMinutes"],
                IDictionary<string, object> dictionary => dictionary.TryGetValue("timeoutMinutes", out var entry) ? entry : null,
                _ => null,
            };
            int? minutes = value switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var whole) => whole,
                JsonValue node when node.TryGetValue<int>(out var whole) => whole,
                int whole => whole,
                long whole when whole is > 0 and <= int.MaxValue => (int)whole,
                _ => null,
            };
            return PipelineRunTimeout.Minutes(minutes);
        }

        /// <summary>BL-229: the minutes a pipeline's body says one run that finishes well saves, or null.</summary>
        public static int? MinutesSaved(string? body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) is JsonObject flowchart ? MinutesSaved(flowchart) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int? MinutesSaved(JsonObject flowchart) =>
            flowchart["metadata"]?["designer"]?["minutesSaved"] is JsonValue saved && saved.TryGetValue<int>(out var minutes) ? PipelineInsights.MinutesSaved(minutes) : null;

        /// <summary>BL-229: whether a pipeline's body says to keep the runs that finish well - false only when it says not to.</summary>
        public static bool? KeepsSuccessfulRuns(string? body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) is JsonObject flowchart ? KeepsSuccessfulRuns(flowchart) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool? KeepsSuccessfulRuns(JsonObject flowchart) =>
            flowchart["metadata"]?["designer"]?["keepSuccessfulRuns"] is JsonValue keep && keep.TryGetValue<bool>(out var value) && !value ? false : null;

        /// <summary>BL-229: what a pipeline's body says its runs keep of their steps' data - null for always (PipelineStepDataKeeping).</summary>
        public static string? KeepsStepData(string? body)
        {
            try
            {
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) is JsonObject flowchart ? KeepsStepData(flowchart) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? KeepsStepData(JsonObject flowchart) =>
            flowchart["metadata"]?["designer"]?["keepStepData"] is JsonValue keep && keep.TryGetValue<string>(out var value) ? PipelineStepDataKeeping.Stored(value) : null;

        // One flowchart's steps and connections - the pipeline's own, or a body's - into the flat graph, each step naming the
        // step and port it sits in. A body's steps follow the step that holds them.
        private static void ReadScope(JsonObject flowchart, string? parent, string? port, PipelineGraph graph)
        {
            // BL-229 increment 8: the flowchart's sticky notes, kept in its designer metadata - read before its steps, so a
            // flowchart's notes come before those of the bodies its steps hold.
            foreach (var node in flowchart["metadata"]?["designer"]?["notes"] as JsonArray ?? new JsonArray())
            {
                if (node is JsonObject note && note["id"]?.GetValue<string>() is { Length: > 0 } noteId)
                {
                    graph.Notes.Add(new PipelineGraphNote
                    {
                        Id = noteId,
                        Text = note["text"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : string.Empty,
                        Position = new GraphPosition { X = note["position"]?["x"]?.GetValue<double>() ?? 0, Y = note["position"]?["y"]?.GetValue<double>() ?? 0 },
                        Parent = parent,
                        ParentPort = port,
                        Color = PipelineNoteColors.Normalize(note["color"] is JsonValue color && color.TryGetValue<string>(out var name) ? name : null),
                    });
                }
            }

            foreach (var node in flowchart["steps"] as JsonArray ?? new JsonArray())
            {
                if (node is JsonObject step)
                {
                    ReadStep(step, parent, port, graph);
                }
            }

            foreach (var node in flowchart["connections"] as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject connection)
                {
                    continue;
                }

                graph.Connections.Add(new PipelineGraphConnection
                {
                    SourceId = connection["source"]?["step"]?.GetValue<string>(),
                    SourcePort = connection["source"]?["port"]?.GetValue<string>(),
                    TargetId = connection["target"]?["step"]?.GetValue<string>(),
                });
            }
        }

        private static void ReadStep(JsonObject step, string? parent, string? port, PipelineGraph graph)
        {
            var view = new PipelineGraphStep
            {
                Id = step["id"]?.GetValue<string>() ?? string.Empty,
                Type = step["type"]?.GetValue<string>() ?? string.Empty,
                Version = step["version"]?.GetValue<int>() ?? 1,
                Name = step["name"]?.GetValue<string>(),
                Position = ReadPosition(step),
                Comment = step["metadata"]?["designer"]?["comment"] is JsonValue comment && comment.TryGetValue<string>(out var commentText) ? commentText : null,
                Parent = parent,
                ParentPort = port,
            };
            if (step["customProperties"] is JsonObject custom)
            {
                var properties = custom.ToDictionary(pair => pair.Key, pair => (object)(pair.Value is null ? null! : JsonSerializer.Deserialize<JsonElement>(pair.Value.ToJsonString())));
                (view.Retries, view.RetryDelaySeconds, view.ContinueOnError) = Reliability.StepReliability.Of(properties);
                (view.FailedPath, view.FailureVariable) = Reliability.StepReliability.OnFailure(properties);
                view.ContinueOnError &= !view.FailedPath;
                view.Disabled = Reliability.StepReliability.IsDisabled(properties);
            }

            graph.Steps.Add(view);

            var bodies = new List<(string Port, JsonObject Body)>();
            foreach (var property in step)
            {
                // BL-229 increment 10: a Switch (flow)'s cases - a list the step holds directly, each a label and a condition.
                if (property.Key == "cases" && property.Value is JsonArray cases && view.Type == FlowSwitchType)
                {
                    view.Cases = cases.OfType<JsonObject>().Select(ReadCase).ToList();
                    continue;
                }

                if (StepEnvelope.Contains(property.Key) || property.Value is not JsonObject input)
                {
                    continue;
                }

                if (IsStep(input))
                {
                    bodies.Add((Pascal(property.Key), input));
                }
                else if (input["expression"] is JsonObject expression)
                {
                    var kind = expression["type"]?.GetValue<string>();
                    if (kind == "Literal")
                    {
                        view.Inputs[Pascal(property.Key)] = LiteralText(expression["value"]);
                    }
                    else if (kind == "Variable" && (expression["value"] as JsonObject)?["id"]?.GetValue<string>() is { Length: > 0 } variableId)
                    {
                        view.InputVariables[Pascal(property.Key)] = variableId;
                    }
                    else if (kind == TemplateExpressionHandler.Kind)
                    {
                        view.InputExpressions[Pascal(property.Key)] = expression["value"] is JsonValue template && template.TryGetValue<string>(out var text) ? text : string.Empty;
                    }
                    else if (kind == ScriptExpressionHandler.Kind)
                    {
                        view.InputScripts[Pascal(property.Key)] = expression["value"] is JsonValue script && script.TryGetValue<string>(out var scriptText) ? scriptText : string.Empty;
                    }
                    else if (kind == ConditionExpressionHandler.Kind)
                    {
                        view.InputConditions[Pascal(property.Key)] = PipelineConditions.Read(expression["value"] is JsonValue condition && condition.TryGetValue<string>(out var conditionText) ? conditionText : null);
                    }
                }
                else if (input["memoryReference"]?["id"]?.GetValue<string>() is { Length: > 0 } boundTo)
                {
                    // An output saved to a variable: {"typeName", "memoryReference": {"id"}} and no expression.
                    view.OutputVariables[Pascal(property.Key)] = boundTo;
                }
                else if (IsVariableReference(input) && input["id"] is JsonValue referenced && referenced.TryGetValue<string>(out var referencedId) && referencedId.Length > 0)
                {
                    // A step's variable itself - Set Variable's which-variable: the variable it names.
                    view.InputVariables[Pascal(property.Key)] = referencedId;
                }
            }

            // A body is a flowchart (what the editor writes), or a single step (what code may hold): either way its steps
            // come back as the body's, and a single step is written back as a flowchart holding it - the same run.
            foreach (var (bodyPort, body) in bodies)
            {
                if (body["type"]?.GetValue<string>() == FlowchartType)
                {
                    ReadScope(body, view.Id, bodyPort, graph);
                }
                else
                {
                    ReadStep(body, view.Id, bodyPort, graph);
                }
            }
        }

        /// <summary>The engine's Switch (flow): its cases are a list it holds directly (BL-229 increment 10).</summary>
        public const string FlowSwitchType = "Stomme.FlowSwitch";

        // A case as the engine holds it: {"label", "condition": {"type", "value"}}. A condition of a kind the editor does not author
        // reads as an empty one - the case keeps its label, the author sees there is nothing to match on.
        private static PipelineGraphCase ReadCase(JsonObject @case) => new()
        {
            Label = @case["label"] is JsonValue label && label.TryGetValue<string>(out var text) ? text : string.Empty,
            Condition = @case["condition"]?["type"]?.GetValue<string>() == ConditionExpressionHandler.Kind && @case["condition"]?["value"] is JsonValue value && value.TryGetValue<string>(out var conditionText)
                ? PipelineConditions.Read(conditionText)
                : new PipelineCondition(),
        };

        // A step held in a port: {"id", "type", ...}. An input is {"typeName", "expression"} and an output {"typeName", "memoryReference"}.
        private static bool IsStep(JsonObject value) =>
            value["type"] is JsonValue type && type.TryGetValue<string>(out _) && value["id"] is not null && value["typeName"] is null;

        /// <summary>
        /// The body for a graph. Inputs the graph does not name keep what the current body holds for that step (a
        /// non-literal expression survives a save that did not touch it); everything else is rebuilt.
        /// </summary>
        public static string ToBody(PipelineGraph graph, string? currentBody, IStepRegistry registry, IIdentityGenerator identities)
        {
            ArgumentNullException.ThrowIfNull(graph);
            var current = ParseFlowchart(currentBody);

            // Every step the current document holds, at any depth, and the id of each body's flowchart, so a save keeps both.
            var currentSteps = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var currentBodies = new Dictionary<(string, string), string>();
            if (current is not null)
            {
                IndexCurrent(current, currentSteps, currentBodies);
            }

            var variables = Variables(graph.Variables ?? new List<PipelineGraphVariable>());
            var connectionList = graph.Connections ?? new List<PipelineGraphConnection>();

            // Resolve every step first - its type, its id, the body it sits in - so a body can be written under the step
            // that holds it whatever order the graph lists them in.
            var resolved = new List<(PipelineGraphStep Step, string Id, StepDescriptor Descriptor, string? Parent, string? Port)>();
            var byId = new Dictionary<string, (PipelineGraphStep Step, StepDescriptor Descriptor)>(StringComparer.Ordinal);
            foreach (var step in graph.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Type))
                {
                    throw new ArgumentException("Every step names its type.", nameof(graph));
                }

                var descriptor = registry.Find(step.Type, step.Version <= 0 ? 1 : step.Version)
                    ?? throw new ArgumentException($"'{step.Type}' version {step.Version} is not a step type this installation offers.", nameof(graph));
                var id = string.IsNullOrWhiteSpace(step.Id) ? identities.GenerateId() : step.Id.Trim();
                if (byId.ContainsKey(id))
                {
                    throw new ArgumentException($"Step id '{id}' is used twice.", nameof(graph));
                }

                var parent = string.IsNullOrWhiteSpace(step.Parent) ? null : step.Parent.Trim();
                var port = parent is null ? null : step.ParentPort?.Trim();
                byId[id] = (step, descriptor);
                resolved.Add((step, id, descriptor, parent, port));
            }

            // A step sits in a body its parent HAS, and no step sits inside itself, however deep.
            foreach (var (step, id, _, parent, port) in resolved)
            {
                if (parent is null)
                {
                    continue;
                }

                if (!byId.TryGetValue(parent, out var holder))
                {
                    throw new ArgumentException($"Step '{step.Name ?? id}' sits in a step that is not in the graph.", nameof(graph));
                }

                if (string.IsNullOrEmpty(port) || !PlaceableBodies(holder.Descriptor).Contains(port, StringComparer.Ordinal))
                {
                    throw new ArgumentException($"Step '{step.Name ?? id}' sits in '{port}', which '{holder.Step.Name ?? parent}' does not hold steps in.", nameof(graph));
                }

                var seen = new HashSet<string>(StringComparer.Ordinal) { id };
                for (var above = parent; above is not null; above = byId.TryGetValue(above, out var next) ? Scope(next.Step).Parent : null)
                {
                    if (!seen.Add(above))
                    {
                        throw new ArgumentException($"Step '{step.Name ?? id}' sits inside itself.", nameof(graph));
                    }
                }
            }

            // A connection joins two steps of ONE flowchart: the pipeline's, or one body's.
            foreach (var connection in connectionList)
            {
                if (string.IsNullOrWhiteSpace(connection.SourceId) || string.IsNullOrWhiteSpace(connection.TargetId))
                {
                    throw new ArgumentException("A connection names its source and target steps.", nameof(graph));
                }

                if (!byId.TryGetValue(connection.SourceId, out var from) || !byId.TryGetValue(connection.TargetId, out var to))
                {
                    throw new ArgumentException($"The connection {connection.SourceId} -> {connection.TargetId} names a step that is not in the graph.", nameof(graph));
                }

                if (Scope(from.Step) != Scope(to.Step))
                {
                    throw new ArgumentException($"The connection from '{from.Step.Name ?? connection.SourceId}' to '{to.Step.Name ?? connection.TargetId}' crosses into another body; a body's steps connect among themselves.", nameof(graph));
                }
            }

            var root = WriteFlowchart(null, null, current?["id"]?.GetValue<string>() ?? identities.GenerateId());
            root["variables"] = new JsonArray(variables.Values.Select(variable => (JsonNode?)variable.DeepClone()).ToArray());
            return root.ToJsonString(Json);

            JsonObject WriteFlowchart(string? parent, string? port, string flowchartId)
            {
                var steps = new JsonArray();
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (step, id, descriptor, stepParent, stepPort) in resolved)
                {
                    if (stepParent == parent && stepPort == port)
                    {
                        members.Add(id);
                        steps.Add(WriteStep(step, id, descriptor, parent is null));
                    }
                }

                var connections = new JsonArray();
                foreach (var connection in connectionList.Where(connection => members.Contains(connection.SourceId)))
                {
                    var source = new JsonObject { ["step"] = connection.SourceId };
                    if (!string.IsNullOrWhiteSpace(connection.SourcePort))
                    {
                        source["port"] = connection.SourcePort;
                    }

                    connections.Add(new JsonObject
                    {
                        ["source"] = source,
                        ["target"] = new JsonObject { ["step"] = connection.TargetId },
                        ["vertices"] = new JsonArray(),
                    });
                }

                // BL-229 increment 8: the sticky notes on this flowchart, in its designer metadata (the engine runs none of it).
                var metadata = new JsonObject();
                var notes = NotesIn(parent, port).Select(note =>
                {
                    var written = new JsonObject
                    {
                        ["id"] = note.Id.Trim(),
                        ["text"] = note.Text ?? string.Empty,
                        ["position"] = new JsonObject { ["x"] = note.Position?.X ?? 0, ["y"] = note.Position?.Y ?? 0 },
                    };
                    if (PipelineNoteColors.Normalize(note.Color) is { } color)
                    {
                        written["color"] = color; // BL-229: n8n's note colours; none for the accent
                    }

                    return (JsonNode?)written;
                }).ToArray();
                if (notes.Length > 0)
                {
                    metadata["designer"] = new JsonObject { ["notes"] = new JsonArray(notes) };
                }

                // BL-229 (n8n's pinned data): the variables' pinned test values, on the pipeline's own flowchart - for the editor,
                // never read by a run.
                if (parent is null)
                {
                    var pinned = new JsonObject();
                    foreach (var variable in (graph.Variables ?? new List<PipelineGraphVariable>()).Where(variable => !string.IsNullOrEmpty(variable?.Id) && variable.TestValue is not null))
                    {
                        pinned[variable.Id] = variable.TestValue;
                    }

                    if (pinned.Count > 0)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["testValues"] = pinned;
                        metadata["designer"] = designer;
                    }

                    // And the steps' pinned results, by step id - a test hands one on in place of running its step; a run never
                    // reads them.
                    var pins = new JsonObject();
                    foreach (var step in (graph.Steps ?? new List<PipelineGraphStep>()).Where(step => !string.IsNullOrEmpty(step?.Id) && step.PinnedResult is not null))
                    {
                        pins[step.Id] = step.PinnedResult;
                    }

                    if (pins.Count > 0)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["pinnedResults"] = pins;
                        metadata["designer"] = designer;
                    }

                    // BL-229: how many runs may go at once - kept with the graph, and set on the definition's engine options at
                    // each save (PipelineRunPolicy), which is where the engine's trigger starts read it.
                    if (PipelineRunsAtOnce.Normalize(graph.RunsAtOnce) is { Length: > 0 } runsAtOnce)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["runsAtOnce"] = runsAtOnce;
                        metadata["designer"] = designer;
                    }

                    // BL-229: not keeping the runs that finish well - read from the published body by PipelineRunSettingsTask.
                    if (graph.KeepSuccessfulRuns == false)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["keepSuccessfulRuns"] = false;
                        metadata["designer"] = designer;
                    }

                    // BL-229: storing a run after each step - kept with the graph, and set on the definition's engine options at each
                    // save (PipelineRunPolicy), which is where the engine's step scheduler reads it.
                    if (graph.SaveProgress == true)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["saveProgress"] = true;
                        metadata["designer"] = designer;
                    }

                    // BL-229: what the runs keep of their steps' data - read from the published body by PipelineRunSettingsTask.
                    if (PipelineStepDataKeeping.Stored(graph.KeepStepData) is { } keepStepData)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["keepStepData"] = keepStepData;
                        metadata["designer"] = designer;
                    }

                    // BL-229: the minutes a run that finishes well saves - read from the published body by the Insights query.
                    if (PipelineInsights.MinutesSaved(graph.MinutesSavedPerRun) is { } minutesSaved)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["minutesSaved"] = minutesSaved;
                        metadata["designer"] = designer;
                    }

                    // BL-229: how long a run may be busy in one go - read from the RUN's own pipeline by the run-control step
                    // middleware (Reliability/RunControl.cs), so a run keeps the limit of the version it runs.
                    if (PipelineRunTimeout.Minutes(graph.TimeoutMinutes) is { } timeout)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["timeoutMinutes"] = timeout;
                        metadata["designer"] = designer;
                    }

                    // BL-229: which pipelines may call this one - read from the CALLED pipeline's published body when a call
                    // starts (Reliability/CallerPolicy.cs).
                    if (PipelineCallers.Normalize(graph.CalledBy) is { } calledBy)
                    {
                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["calledBy"] = calledBy;
                        metadata["designer"] = designer;
                    }

                    // BL-229: the zone the pipeline reads time in - read from the RUN's own pipeline for its built-ins, and at
                    // publish for a Schedule with no zone of its own. One this server does not know is refused here.
                    if (PipelineTimeZone.Normalize(graph.TimeZone) is { } zone)
                    {
                        if (PipelineTimeZone.Problem(zone) is { } problem)
                        {
                            throw new ArgumentException(problem);
                        }

                        var designer = metadata["designer"] as JsonObject ?? new JsonObject();
                        designer["timeZone"] = zone;
                        metadata["designer"] = designer;
                    }
                }

                return new JsonObject
                {
                    ["id"] = flowchartId,
                    ["type"] = FlowchartType,
                    ["version"] = 1,
                    ["customProperties"] = new JsonObject(),
                    ["metadata"] = metadata,
                    ["steps"] = steps,
                    ["variables"] = new JsonArray(),
                    ["connections"] = connections,
                };
            }

            // The notes of one flowchart; a note in a body of a step the graph no longer has is dropped with it.
            IEnumerable<PipelineGraphNote> NotesIn(string? parent, string? port) =>
                (graph.Notes ?? new List<PipelineGraphNote>()).Where(note =>
                    !string.IsNullOrWhiteSpace(note?.Id)
                    && (string.IsNullOrWhiteSpace(note.Parent) ? parent is null : note.Parent.Trim() == parent && note.ParentPort?.Trim() == port));

            JsonObject WriteStep(PipelineGraphStep step, string id, StepDescriptor descriptor, bool atTop)
            {
                var node = new JsonObject
                {
                    ["id"] = id,
                    ["nodeId"] = null,
                    ["name"] = string.IsNullOrWhiteSpace(step.Name) ? null : step.Name.Trim(),
                    ["type"] = descriptor.TypeName,
                    ["version"] = descriptor.Version,
                    ["customProperties"] = new JsonObject(),
                    ["metadata"] = new JsonObject
                    {
                        ["designer"] = new JsonObject
                        {
                            ["position"] = new JsonObject { ["x"] = step.Position?.X ?? 0, ["y"] = step.Position?.Y ?? 0 },
                        },
                    },
                };
                if (!string.IsNullOrWhiteSpace(step.Comment))
                {
                    ((JsonObject)node["metadata"]!["designer"]!)["comment"] = step.Comment; // BL-229 increment 8: for the reader, never run
                }

                // BL-229 increment 9: the step's retry and continue-on-error, which the module's step middleware reads.
                var custom = (JsonObject)node["customProperties"]!;
                if (step.Retries > 0)
                {
                    custom[Reliability.StepReliability.RetriesKey] = Math.Clamp(step.Retries, 0, Reliability.StepReliability.MaxRetries);
                    custom[Reliability.StepReliability.RetryDelayKey] = Math.Clamp(step.RetryDelaySeconds, 0, Reliability.StepReliability.MaxRetryDelaySeconds);
                }

                // n8n's error output wins over going on: a step takes its Failed path, or goes on, or neither.
                if (step.FailedPath)
                {
                    custom[Reliability.StepReliability.FailedPathKey] = true;
                }
                else if (step.ContinueOnError)
                {
                    custom[Reliability.StepReliability.ContinueOnErrorKey] = true;
                }

                if ((step.FailedPath || step.ContinueOnError) && !string.IsNullOrWhiteSpace(step.FailureVariable))
                {
                    custom[Reliability.StepReliability.FailureVariableKey] = step.FailureVariable.Trim();
                }

                // A step switched off in the editor: the same middleware skips it when the run reaches it.
                if (step.Disabled)
                {
                    custom[Reliability.StepReliability.DisabledKey] = true;
                }

                // A TRIGGER with nothing pointing at it is a start of the pipeline: the engine indexes only triggers
                // marked so (the designer's canStartPipeline custom property), and a trigger placed mid-flow is a wait -
                // as is every trigger inside a body, which runs only when the step holding it does. A trigger switched off
                // starts nothing.
                if (atTop && !step.Disabled && descriptor.Kind == StepKind.Trigger && !connectionList.Any(c => string.Equals(c.TargetId?.Trim(), id, StringComparison.Ordinal)))
                {
                    ((JsonObject)node["customProperties"]!)["canStartPipeline"] = true;
                }

                currentSteps.TryGetValue(id, out var existing);
                foreach (var input in descriptor.Inputs)
                {
                    var key = Camel(input.Name);
                    if (IsVariablePicker(input))
                    {
                        // BL-229: a step's VARIABLE - Set Variable's which-variable - is a property holding the variable itself, not
                        // an input holding an expression: written as the input form, the engine read a variable of its own making
                        // and every run failed "Variable '...' not found" (measured through the engine). None chosen, none written.
                        if (step.InputVariables is not null && step.InputVariables.TryGetValue(input.Name, out var picked) && !string.IsNullOrWhiteSpace(picked))
                        {
                            node[key] = Known(variables, picked, step, input.Name).DeepClone();
                        }

                        continue;
                    }

                    if (ListStepsQueryHandler.OutcomesFrom(descriptor) == StepCatalogEntry.CaseOutcomes && input.Name == StepCatalogEntry.CaseOutcomes)
                    {
                        // BL-229 increment 10: a Switch (flow)'s cases, a list the step holds directly - the engine's own shape,
                        // {"label", "condition": {"type": "Condition", "value"}} each.
                        node[key] = Cases(step);
                        continue;
                    }

                    if (step.InputScripts is not null && step.InputScripts.TryGetValue(input.Name, out var script) && script is not null)
                    {
                        // BL-229 increment 5b: a JavaScript expression, run in the bounded sandbox when the step runs (ScriptExpressionHandler).
                        node[key] = new JsonObject
                        {
                            ["typeName"] = (Nullable.GetUnderlyingType(input.Type) ?? input.Type).Name,
                            ["expression"] = new JsonObject { ["type"] = ScriptExpressionHandler.Kind, ["value"] = script },
                        };
                    }
                    else if (step.InputConditions is not null && step.InputConditions.TryGetValue(input.Name, out var condition) && condition is not null)
                    {
                        // BL-229 increment 10: a condition built from rules, evaluated when the step runs (ConditionExpressionHandler).
                        node[key] = new JsonObject
                        {
                            ["typeName"] = (Nullable.GetUnderlyingType(input.Type) ?? input.Type).Name,
                            ["expression"] = new JsonObject { ["type"] = ConditionExpressionHandler.Kind, ["value"] = PipelineConditions.Write(condition) },
                        };
                    }
                    else if (step.InputExpressions is not null && step.InputExpressions.TryGetValue(input.Name, out var template) && template is not null)
                    {
                        // BL-229 increment 5: the input's template, rendered when the step runs (TemplateExpressionHandler).
                        node[key] = new JsonObject
                        {
                            ["typeName"] = (Nullable.GetUnderlyingType(input.Type) ?? input.Type).Name,
                            ["expression"] = new JsonObject { ["type"] = TemplateExpressionHandler.Kind, ["value"] = template },
                        };
                    }
                    else if (step.InputVariables is not null && step.InputVariables.TryGetValue(input.Name, out var readsVariable) && !string.IsNullOrWhiteSpace(readsVariable))
                    {
                        node[key] = VariableInput(input, Known(variables, readsVariable, step, input.Name));
                    }
                    else if (step.Inputs is not null && step.Inputs.TryGetValue(input.Name, out var text))
                    {
                        node[key] = LiteralInput(descriptor.TypeName, input, text);
                    }
                    else if (existing?[key] is JsonObject kept && kept["expression"]?["type"]?.GetValue<string>() is not ("Variable" or TemplateExpressionHandler.Kind or ConditionExpressionHandler.Kind or ScriptExpressionHandler.Kind))
                    {
                        // Another expression kind the editor does not author survives a save that did not touch it; a
                        // variable or a template the graph no longer names was removed by the author, and goes.
                        node[key] = kept.DeepClone();
                    }
                }

                foreach (var output in descriptor.Outputs)
                {
                    if (step.OutputVariables is not null && step.OutputVariables.TryGetValue(output.Name, out var savesTo) && !string.IsNullOrWhiteSpace(savesTo))
                    {
                        Known(variables, savesTo, step, output.Name);
                        node[Camel(output.Name)] = new JsonObject
                        {
                            ["typeName"] = TypeAlias(ValueType(output.Type)),
                            ["memoryReference"] = new JsonObject { ["id"] = savesTo.Trim() },
                        };
                    }
                }

                // Each body holding at least one step is a flowchart of its own in the port, the engine's shape (measured
                // 2026-10-05 through its step serializer: "body": {"id","type":"Stomme.Flowchart",...,"steps","connections"});
                // an empty body is left out - the step then holds nothing there, which the engine runs as an empty body.
                foreach (var bodyPort in PlaceableBodies(descriptor))
                {
                    if (resolved.Any(candidate => candidate.Parent == id && candidate.Port == bodyPort) || NotesIn(id, bodyPort).Any())
                    {
                        node[Camel(bodyPort)] = WriteFlowchart(id, bodyPort, currentBodies.TryGetValue((id, bodyPort), out var bodyId) ? bodyId : identities.GenerateId());
                    }
                }

                return node;
            }
        }

        // The step a graph step sits in and which of its bodies - null for the pipeline's own flowchart.
        private static (string? Parent, string? Port) Scope(PipelineGraphStep step) =>
            string.IsNullOrWhiteSpace(step.Parent) ? (null, null) : (step.Parent.Trim(), step.ParentPort?.Trim());

        private static IEnumerable<string> PlaceableBodies(StepDescriptor descriptor) =>
            ListStepsQueryHandler.Bodies(descriptor);

        // Every step of a document at any depth by id, and each body flowchart's id by the step and port holding it.
        private static void IndexCurrent(JsonObject flowchart, Dictionary<string, JsonObject> steps, Dictionary<(string, string), string> bodies)
        {
            foreach (var step in (flowchart["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (step["id"]?.GetValue<string>() is not { Length: > 0 } id)
                {
                    continue;
                }

                steps[id] = step;
                foreach (var property in step)
                {
                    if (StepEnvelope.Contains(property.Key) || property.Value is not JsonObject held || !IsStep(held))
                    {
                        continue;
                    }

                    if (held["type"]?.GetValue<string>() == FlowchartType)
                    {
                        if (held["id"]?.GetValue<string>() is { Length: > 0 } bodyId)
                        {
                            bodies[(id, Pascal(property.Key))] = bodyId;
                        }

                        IndexCurrent(held, steps, bodies);
                    }
                    else
                    {
                        IndexCurrent(new JsonObject { ["steps"] = new JsonArray(held.DeepClone()) }, steps, bodies);
                    }
                }
            }
        }

        private static JsonObject? ParseFlowchart(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(body) is JsonObject root && root["type"]?.GetValue<string>() == FlowchartType ? root : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static GraphPosition ReadPosition(JsonObject step)
        {
            var position = step["metadata"]?["designer"]?["position"];
            return new GraphPosition
            {
                X = position?["x"]?.GetValue<double>() ?? 0,
                Y = position?["y"]?.GetValue<double>() ?? 0,
            };
        }

        // A Switch (flow)'s cases in the engine's shape. Each needs a label - it is the outcome a connection leaves by - and no two
        // may share one (ignoring case: two ports a person cannot tell apart).
        private static JsonArray Cases(PipelineGraphStep step)
        {
            var cases = new JsonArray();
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var @case in step.Cases ?? new List<PipelineGraphCase>())
            {
                var label = @case?.Label?.Trim();
                if (string.IsNullOrEmpty(label))
                {
                    throw new ArgumentException($"A case of '{step.Name ?? step.Id}' needs a label: it is the outcome its connection leaves by.", "graph");
                }

                if (!labels.Add(label))
                {
                    throw new ArgumentException($"'{step.Name ?? step.Id}' has the case '{label}' twice.", "graph");
                }

                cases.Add(new JsonObject
                {
                    ["label"] = label,
                    ["condition"] = new JsonObject { ["type"] = ConditionExpressionHandler.Kind, ["value"] = PipelineConditions.Write(@case!.Condition) },
                });
            }

            return cases;
        }

        // A literal input in the engine's format, its value typed by the descriptor: a checkbox is a boolean, a number
        // input a number, everything else the text as given. A value the type cannot hold is refused, not coerced.
        private static JsonObject LiteralInput(string stepType, InputDescriptor input, string? text)
        {
            var type = Nullable.GetUnderlyingType(input.Type) ?? input.Type;
            JsonNode? value;
            if (type == typeof(bool))
            {
                value = bool.TryParse(text, out var b) ? JsonValue.Create(b) : string.IsNullOrWhiteSpace(text) ? null : throw Refuse(input, text);
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            {
                value = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : string.IsNullOrWhiteSpace(text) ? null : throw Refuse(input, text);
            }
            else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            {
                value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : string.IsNullOrWhiteSpace(text) ? null : throw Refuse(input, text);
            }
            else if (type.IsEnum)
            {
                // A dropdown input: the canonical member name, so what the engine reads back is what the catalog lists.
                value = string.IsNullOrWhiteSpace(text) ? null : Enum.TryParse(type, text, ignoreCase: true, out var member) ? JsonValue.Create(member!.ToString()) : throw Refuse(input, text);
            }
            else
            {
                value = text is null ? null : JsonValue.Create(text);
            }

            // A trigger's input is judged here, at the save, so an author sees the refusal in the editor rather than
            // at publish (where the engine's indexer would throw it) - a cron that does not parse, a webhook name that
            // cannot be a URL segment.
            if (!string.IsNullOrWhiteSpace(text) && stepType == "Stomme.Schedule" && input.Name == nameof(Steps.Schedule.Cron) && Steps.Schedule.TryParseAll(text, out var cronError) is null)
            {
                throw new ArgumentException(cronError);
            }

            if (!string.IsNullOrWhiteSpace(text) && stepType == "Stomme.Schedule" && input.Name == nameof(Steps.Schedule.TimeZone) && Steps.Schedule.FindZone(text, out var zoneError) is null)
            {
                throw new ArgumentException(zoneError);
            }

            // BL-229: an "Email received" trigger's connection is a connection's id.
            if (!string.IsNullOrWhiteSpace(text) && stepType == PipelineEmailEvent.StepType && input.Name == nameof(Steps.EmailReceived.Connection) && Steps.EmailReceived.ConnectionProblem(text) is { } connectionProblem)
            {
                throw new ArgumentException(connectionProblem);
            }

            // BL-229: so is a "Telegram message received" trigger's.
            if (!string.IsNullOrWhiteSpace(text) && stepType == PipelineTelegramEvent.StepType && input.Name == nameof(Steps.TelegramMessageReceived.Connection) && Steps.TelegramMessageReceived.ConnectionProblem(text) is { } botProblem)
            {
                throw new ArgumentException(botProblem);
            }

            // BL-229: a "File added" trigger's folder is one of the site's pipeline folder, its names to match names.
            if (!string.IsNullOrWhiteSpace(text) && stepType == PipelineFileEvent.StepType && input.Name == nameof(Steps.FileAdded.Folder) && PipelineFileEvent.FolderProblem(text) is { } folderProblem)
            {
                throw new ArgumentException(folderProblem);
            }

            if (!string.IsNullOrWhiteSpace(text) && stepType == PipelineFileEvent.StepType && input.Name == nameof(Steps.FileAdded.Pattern) && PipelineFileEvent.PatternProblem(text) is { } patternProblem)
            {
                throw new ArgumentException(patternProblem);
            }

            if (!string.IsNullOrWhiteSpace(text) && stepType == "Stomme.Webhook" && input.Name == nameof(Steps.Webhook.WebhookName) && !Steps.Webhook.IsValidName(text))
            {
                throw new ArgumentException($"'{text}' is not a webhook name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            // BL-229, the Form trigger: a name that can be its address, and fields the editor wrote. A form still being built (no
            // field yet, a dropdown with no choice) saves - the problems panel says what is missing, and publishing refuses it.
            if (!string.IsNullOrWhiteSpace(text) && stepType == Steps.Form.StepTypeName && input.Name == nameof(Steps.Form.FormName) && !Steps.Webhook.IsValidName(text))
            {
                throw new ArgumentException($"'{text}' is not a form name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            if (!string.IsNullOrWhiteSpace(text) && stepType == Steps.Form.StepTypeName && input.Name == nameof(Steps.Form.Fields) && PipelineForms.Parse(text, out var fieldsError) is null)
            {
                throw new ArgumentException(fieldsError);
            }

            // BL-229, n8n's redirect after a submission: only a web address a person can be sent on to - never javascript: or a path.
            if (!string.IsNullOrWhiteSpace(text) && stepType == Steps.Form.StepTypeName && input.Name == nameof(Steps.Form.Redirect) && !Steps.Form.IsRedirect(text))
            {
                throw new ArgumentException($"Then go to '{text.Trim()}' is not an https:// or http:// address.");
            }

            if (!string.IsNullOrWhiteSpace(text) && stepType == Steps.ChatTrigger.StepTypeName && input.Name == nameof(Steps.ChatTrigger.ChatName) && !Steps.Webhook.IsValidName(text))
            {
                throw new ArgumentException($"'{text}' is not a chat name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            // BL-229, the AI agent: a tool's name is what the model calls it by - the model APIs' own rule.
            if (!string.IsNullOrWhiteSpace(text) && stepType == Steps.AgentTool.StepTypeName && input.Name == nameof(Steps.AgentTool.ToolName) && !Steps.AgentTool.IsValidName(text))
            {
                throw new ArgumentException($"'{text}' is not a tool name (letters, digits, '_' and '-', at most 64).");
            }

            var expression = new JsonObject { ["type"] = "Literal" };
            if (value is not null)
            {
                expression["value"] = value;
            }

            return new JsonObject
            {
                ["typeName"] = type.Name,
                ["expression"] = expression,
            };
        }

        // The variables in the engine's own shape (measured 2026-10-02 by serialising a flowchart through its step serializer):
        // {"id","name","typeName","value"}, in the order the graph names them. An id and a name are each unique.
        private static readonly HashSet<string> VariableTypes = new(StringComparer.Ordinal) { "Object", "String", "Int32", "Double", "Boolean" };

        private static Dictionary<string, JsonObject> Variables(IEnumerable<PipelineGraphVariable> declared)
        {
            var variables = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in declared)
            {
                var id = variable.Id?.Trim();
                var name = variable.Name?.Trim();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                {
                    throw new ArgumentException("Every variable has an id and a name.", "graph");
                }

                if (!names.Add(name) || variables.ContainsKey(id))
                {
                    throw new ArgumentException($"The variable '{name}' is declared twice.", "graph");
                }

                var typeName = string.IsNullOrWhiteSpace(variable.TypeName) ? "Object" : variable.TypeName.Trim();
                if (!VariableTypes.Contains(typeName))
                {
                    throw new ArgumentException($"A variable holds {string.Join(", ", VariableTypes)} - not '{typeName}'.", "graph");
                }

                variables[id] = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["typeName"] = typeName,
                    ["value"] = VariableValue(name, typeName, variable.Value),
                    // Kept with the run, so a pause or a retry from a failed step finds what was saved (PipelineVariableStorage).
                    ["storageDriverTypeName"] = PipelineVariableStorage.DriverTypeName,
                };
            }

            return variables;
        }

        // A variable's starting value, typed as it holds it; none when the text is empty.
        private static JsonNode? VariableValue(string name, string typeName, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return typeName == "String" && text is not null ? JsonValue.Create(string.Empty) : null;
            }

            return typeName switch
            {
                "Int32" => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? JsonValue.Create(i) : throw new ArgumentException($"'{text}' is not a whole number for the variable '{name}'.", "graph"),
                "Double" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : throw new ArgumentException($"'{text}' is not a number for the variable '{name}'.", "graph"),
                "Boolean" => bool.TryParse(text, out var b) ? JsonValue.Create(b) : throw new ArgumentException($"'{text}' is not true or false for the variable '{name}'.", "graph"),
                _ => JsonValue.Create(text),
            };
        }

        // A step property that holds a pipeline variable itself (Set Variable's Variable) - not an Input<T> - which the editor
        // picks among the pipeline's variables.
        internal static bool IsVariablePicker(InputDescriptor input) =>
            !input.IsWrapped && typeof(Stomme.Engine.Pipelines.Memory.Variable).IsAssignableFrom(Nullable.GetUnderlyingType(input.Type) ?? input.Type);

        // How the engine writes such a property: the variable itself - {"id", "name", "typeName", ...} - neither a step nor an input.
        private static bool IsVariableReference(JsonObject value) =>
            value["expression"] is null && value["memoryReference"] is null && value["type"] is null
            && value["id"] is JsonValue && value["name"] is not null && value["typeName"] is not null;

        private static JsonObject Known(IReadOnlyDictionary<string, JsonObject> variables, string id, PipelineGraphStep step, string what) =>
            variables.TryGetValue(id.Trim(), out var variable)
                ? variable
                : throw new ArgumentException($"'{what}' of step '{step.Name ?? step.Id}' names a variable the pipeline does not declare.", "graph");

        // An input that reads a variable, in the engine's shape: the expression's value is the variable itself.
        private static JsonObject VariableInput(InputDescriptor input, JsonObject variable) => new()
        {
            ["typeName"] = TypeAlias(Nullable.GetUnderlyingType(input.Type) ?? input.Type),
            ["expression"] = new JsonObject { ["type"] = "Variable", ["value"] = variable.DeepClone() },
        };

        // An output property's value type: Output<T>'s T, unwrapped from Nullable.
        private static Type ValueType(Type type)
        {
            var valueType = type.IsGenericType && typeof(Stomme.Engine.Pipelines.Models.Output).IsAssignableFrom(type) ? type.GetGenericArguments()[0] : type;
            return Nullable.GetUnderlyingType(valueType) ?? valueType;
        }

        private static string TypeAlias(Type type) => type == typeof(object) ? "Object" : type.Name;
        private static ArgumentException Refuse(InputDescriptor input, string? text) =>
            new($"'{text}' is not a {input.Type.Name} for the input '{input.Name}'.", "graph");

        private static string LiteralText(JsonNode? value) => value switch
        {
            null => string.Empty,
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            _ => value.ToJsonString(Json),
        };

        private static string Camel(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

        private static string Pascal(string name) => string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
