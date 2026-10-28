#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Stomme.Collaboration;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A pipeline graph as FIELDS for the conflict resolver (BL-97 increment 12, D114): every step, its name, its position and
    /// each literal input, and every connection, is one path with one value - so two editors changing different steps, or
    /// different inputs of one step, never collide, and a concurrent change to the same field is found. BCL-only, so the
    /// editor describes a conflict's path with the same rules the server merged it by.
    /// <list type="bullet">
    /// <item><c>step/{id}</c> - <c>{type}@{version}</c>: the step exists (structured)</item>
    /// <item><c>step/{id}/name</c> (structured), <c>step/{id}/position</c> - <c>x,y</c> (text: last-writer-wins, it is layout)</item>
    /// <item><c>step/{id}/input/{name}</c> - the literal (text for a multi-line input, a script or an expression; structured otherwise)</item>
    /// <item><c>connection/{source}/{port}/{target}</c> - <c>1</c> (structured); an empty port is the step's one outcome</item>
    /// <item><c>variable/{id}</c> - its name (structured), <c>variable/{id}/type</c>, <c>variable/{id}/value</c> (structured),
    /// <c>variable/{id}/test-value</c> - its pinned test value (text, BL-229)</item>
    /// <item><c>step/{id}/input-variable/{name}</c> and <c>step/{id}/output/{name}</c> - the variable's id (structured)</item>
    /// <item><c>step/{id}/input-expression/{name}</c> - the input's template (text: BL-229 increment 5)</item>
    /// <item><c>step/{id}/input-script/{name}</c> - the input's JavaScript (text: BL-229 increment 5b)</item>
    /// <item><c>step/{id}/input-condition/{name}</c> - the input's condition, and <c>step/{id}/cases</c> - a Switch's cases, each as JSON
    /// (structured: BL-229 increment 10 - two editors changing one condition is a conflict)</item>
    /// <item><c>step/{id}/retry</c>, <c>step/{id}/continue-on-error</c>, <c>step/{id}/failed-path</c> and <c>step/{id}/failure-variable</c>
    /// - what happens when it fails (structured: BL-229 increment 9, and n8n's error output)</item>
    /// <item><c>step/{id}/pinned</c> - the result pinned for its tests (text, BL-229, n8n's pinned data)</item>
    /// <item><c>step/{id}/comment</c> (text), and a sticky note: <c>note/{id}</c> - <c>1</c> (structured), <c>note/{id}/text</c> and
    /// <c>note/{id}/position</c> (text), <c>note/{id}/parent</c> (structured) - BL-229 increment 8</item>
    /// <item><c>step/{id}/parent</c> - <c>{parent}/{port}</c>, the body the step sits in (structured); absent for a step of the
    /// pipeline itself, so a graph with no bodies has exactly the fields it always had</item>
    /// <item><c>pipeline/runs-at-once</c> - how many runs may go at once (structured, BL-229); absent for any number</item>
    /// <item><c>pipeline/keep-successful-runs</c> - <c>false</c> when the runs that finish well are not kept (structured, BL-229);
    /// <item><c>pipeline/save-progress</c> - <c>true</c> when a run is stored after each step (structured, BL-229);
    /// absent when they are</item>
    /// <item><c>pipeline/minutes-saved</c> - the minutes one run that finishes well saves (structured, BL-229); absent when not said</item>
    /// <item><c>pipeline/timeout-minutes</c> - how long a run may be busy in one go before it is stopped (structured, BL-229);
    /// absent for no limit</item>
    /// </list>
    /// Every id and name inside a path is escaped, so a slash in one cannot fake a path.
    /// </summary>
    public static class PipelineGraphFields
    {
        public const string Present = "1";

        /// <summary>The graph's fields; an absent path and a null value are the same.</summary>
        public static Dictionary<string, string?> ToFields(PipelineGraph? graph)
        {
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (graph is null)
            {
                return fields;
            }

            foreach (var step in graph.Steps ?? new List<PipelineGraphStep>())
            {
                if (string.IsNullOrEmpty(step?.Id))
                {
                    continue;
                }

                var root = StepPath(step.Id);
                fields[root] = (step.Type ?? string.Empty) + "@" + step.Version.ToString(CultureInfo.InvariantCulture);
                fields[root + "/name"] = step.Name;
                fields[root + "/position"] = Position(step.Position);
                if (!string.IsNullOrEmpty(step.Comment))
                {
                    fields[root + "/comment"] = step.Comment;
                }

                if (step.PinnedResult is not null)
                {
                    fields[root + "/pinned"] = step.PinnedResult; // BL-229: the result pinned for its tests (text)
                }

                if (step.Retries > 0)
                {
                    fields[root + "/retry"] = step.Retries.ToString(CultureInfo.InvariantCulture) + "/" + step.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture);
                }

                if (step.ContinueOnError)
                {
                    fields[root + "/continue-on-error"] = Present;
                }

                if (step.FailedPath)
                {
                    fields[root + "/failed-path"] = Present; // n8n's error output (structured)
                }

                if (!string.IsNullOrEmpty(step.FailureVariable))
                {
                    fields[root + "/failure-variable"] = step.FailureVariable; // the variable's id (structured)
                }

                if (step.Disabled)
                {
                    fields[root + "/disabled"] = Present;
                }
                if (!string.IsNullOrEmpty(step.Parent))
                {
                    fields[root + "/parent"] = Escape(step.Parent) + "/" + Escape(step.ParentPort ?? string.Empty);
                }

                foreach (var input in step.Inputs ?? new Dictionary<string, string>())
                {
                    fields[root + "/input/" + Escape(input.Key)] = input.Value;
                }

                foreach (var input in step.InputVariables ?? new Dictionary<string, string>())
                {
                    fields[root + "/input-variable/" + Escape(input.Key)] = input.Value;
                }

                foreach (var input in step.InputExpressions ?? new Dictionary<string, string>())
                {
                    fields[root + "/input-expression/" + Escape(input.Key)] = input.Value;
                }

                foreach (var input in step.InputScripts ?? new Dictionary<string, string>())
                {
                    fields[root + "/input-script/" + Escape(input.Key)] = input.Value;
                }

                foreach (var input in step.InputConditions ?? new Dictionary<string, PipelineCondition>())
                {
                    fields[root + "/input-condition/" + Escape(input.Key)] = PipelineConditions.Write(input.Value);
                }

                if (step.Cases is { Count: > 0 })
                {
                    fields[root + "/cases"] = PipelineOutcomes.WriteCases(step.Cases);
                }

                foreach (var output in step.OutputVariables ?? new Dictionary<string, string>())
                {
                    fields[root + "/output/" + Escape(output.Key)] = output.Value;
                }
            }

            foreach (var variable in graph.Variables ?? new List<PipelineGraphVariable>())
            {
                if (string.IsNullOrEmpty(variable?.Id))
                {
                    continue;
                }

                var root = VariablePath(variable.Id);
                fields[root] = variable.Name ?? string.Empty;
                fields[root + "/type"] = variable.TypeName;
                fields[root + "/value"] = variable.Value;
                if (variable.TestValue is not null)
                {
                    fields[root + "/test-value"] = variable.TestValue; // BL-229: its pinned test value (text)
                }
            }

            foreach (var connection in graph.Connections ?? new List<PipelineGraphConnection>())
            {
                if (!string.IsNullOrEmpty(connection?.SourceId) && !string.IsNullOrEmpty(connection.TargetId))
                {
                    fields[ConnectionPath(connection.SourceId, connection.SourcePort, connection.TargetId)] = Present;
                }
            }

            foreach (var note in graph.Notes ?? new List<PipelineGraphNote>())
            {
                if (string.IsNullOrEmpty(note?.Id))
                {
                    continue;
                }

                var root = NotePath(note.Id);
                fields[root] = Present;
                fields[root + "/text"] = note.Text ?? string.Empty;
                fields[root + "/position"] = Position(note.Position);
                if (!string.IsNullOrEmpty(note.Parent))
                {
                    fields[root + "/parent"] = Escape(note.Parent) + "/" + Escape(note.ParentPort ?? string.Empty);
                }

                if (PipelineNoteColors.Normalize(note.Color) is { } color)
                {
                    fields[root + "/color"] = color; // BL-229: a field of its own, so one editor's colour and another's text both stay
                }
            }

            if (PipelineRunsAtOnce.Normalize(graph.RunsAtOnce) is { Length: > 0 } runsAtOnce)
            {
                fields[RunsAtOncePath] = runsAtOnce;
            }

            if (graph.KeepSuccessfulRuns == false)
            {
                fields[KeepSuccessfulRunsPath] = "false";
            }

            if (graph.SaveProgress == true)
            {
                fields[SaveProgressPath] = "true";
            }

            if (PipelineStepDataKeeping.Normalize(graph.KeepStepData) is { Length: > 0 } keepStepData)
            {
                fields[KeepStepDataPath] = keepStepData;
            }

            if (PipelineInsights.MinutesSaved(graph.MinutesSavedPerRun) is { } minutesSaved)
            {
                fields[MinutesSavedPath] = minutesSaved.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (PipelineRunTimeout.Minutes(graph.TimeoutMinutes) is { } timeout)
            {
                fields[TimeoutPath] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (PipelineCallers.Normalize(graph.CalledBy) is { } calledBy)
            {
                fields[CalledByPath] = calledBy;
            }

            if (PipelineTimeZone.Normalize(graph.TimeZone) is { } zone)
            {
                fields[TimeZonePath] = zone;
            }

            return fields;
        }

        private const string RunsAtOncePath = "pipeline/runs-at-once";

        private const string KeepSuccessfulRunsPath = "pipeline/keep-successful-runs";

        private const string SaveProgressPath = "pipeline/save-progress";

        private const string KeepStepDataPath = "pipeline/keep-step-data";

        private const string MinutesSavedPath = "pipeline/minutes-saved";

        private const string TimeoutPath = "pipeline/timeout-minutes";

        private const string CalledByPath = "pipeline/called-by";

        private const string TimeZonePath = "pipeline/time-zone";

        /// <summary>A version token for a field set: a digest of its non-null fields in path order - equal graphs, equal tokens.</summary>
        public static string Version(IReadOnlyDictionary<string, string?> fields)
        {
            ArgumentNullException.ThrowIfNull(fields);
            var canonical = new StringBuilder();
            foreach (var field in fields.Where(field => field.Value is not null).OrderBy(field => field.Key, StringComparer.Ordinal))
            {
                canonical.Append(field.Key).Append('\u0000').Append(field.Value).Append('\u0001');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        }

        /// <summary>
        /// What the editor changed: every path whose value differs between the graph it loaded and the graph it saves, with
        /// how each merges. <paramref name="isTextInput"/> answers whether a step type's input is free text.
        /// </summary>
        public static IReadOnlyList<FieldChange> Changes(PipelineGraph? loaded, PipelineGraph? saved, Func<string, string, bool>? isTextInput = null)
        {
            var before = ToFields(loaded);
            var after = ToFields(saved);
            var changes = new List<FieldChange>();
            foreach (var path in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
            {
                var was = before.GetValueOrDefault(path);
                var now = after.GetValueOrDefault(path);
                if (!string.Equals(was, now, StringComparison.Ordinal))
                {
                    changes.Add(FieldChange.Of(path, now, KindOf(path, after, before, isTextInput)));
                }
            }

            return changes;
        }

        /// <summary>
        /// The graph a merged field set describes. Steps and connections keep the saving editor's order, then the stored
        /// graph's, so an unchanged graph comes back as it was drawn.
        /// </summary>
        public static PipelineGraph ToGraph(IReadOnlyDictionary<string, string?> fields, params PipelineGraph?[] orderHints)
        {
            ArgumentNullException.ThrowIfNull(fields);
            var stepOrder = new List<string>();
            var connectionOrder = new List<string>();
            foreach (var hint in orderHints.Where(hint => hint is not null))
            {
                stepOrder.AddRange(hint!.Steps.Where(step => !string.IsNullOrEmpty(step.Id)).Select(step => StepPath(step.Id)));
                connectionOrder.AddRange(hint.Connections.Where(c => !string.IsNullOrEmpty(c.SourceId) && !string.IsNullOrEmpty(c.TargetId)).Select(c => ConnectionPath(c.SourceId, c.SourcePort, c.TargetId)));
            }

            stepOrder.AddRange(fields.Keys.Where(IsStepMarker).OrderBy(path => path, StringComparer.Ordinal));
            connectionOrder.AddRange(fields.Keys.Where(path => path.StartsWith("connection/", StringComparison.Ordinal)).OrderBy(path => path, StringComparer.Ordinal));

            var graph = new PipelineGraph();
            foreach (var marker in stepOrder.Distinct(StringComparer.Ordinal))
            {
                if (fields.GetValueOrDefault(marker) is not { } typeAndVersion)
                {
                    continue;
                }

                var at = typeAndVersion.LastIndexOf('@');
                var step = new PipelineGraphStep
                {
                    Id = Unescape(marker["step/".Length..]),
                    Type = at < 0 ? typeAndVersion : typeAndVersion[..at],
                    Version = at >= 0 && int.TryParse(typeAndVersion[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 1,
                    Name = fields.GetValueOrDefault(marker + "/name"),
                    Position = ParsePosition(fields.GetValueOrDefault(marker + "/position")),
                    Comment = fields.GetValueOrDefault(marker + "/comment"),
                    PinnedResult = fields.GetValueOrDefault(marker + "/pinned"),
                    ContinueOnError = fields.GetValueOrDefault(marker + "/continue-on-error") is not null,
                    FailedPath = fields.GetValueOrDefault(marker + "/failed-path") is not null,
                    FailureVariable = fields.GetValueOrDefault(marker + "/failure-variable"),
                    Disabled = fields.GetValueOrDefault(marker + "/disabled") is not null,
                };
                if (fields.GetValueOrDefault(marker + "/retry")?.Split('/') is [var retries, var delay])
                {
                    step.Retries = int.TryParse(retries, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
                    step.RetryDelaySeconds = int.TryParse(delay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
                }
                if (fields.GetValueOrDefault(marker + "/parent") is { } sitsIn && sitsIn.Split('/') is [var parent, var port])
                {
                    step.Parent = Unescape(parent);
                    step.ParentPort = port.Length == 0 ? null : Unescape(port);
                }

                var inputPrefix = marker + "/input/";
                foreach (var input in fields.Where(field => field.Value is not null && field.Key.StartsWith(inputPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.Inputs[Unescape(input.Key[inputPrefix.Length..])] = input.Value!;
                }

                var readsPrefix = marker + "/input-variable/";
                foreach (var input in fields.Where(field => field.Value is not null && field.Key.StartsWith(readsPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.InputVariables[Unescape(input.Key[readsPrefix.Length..])] = input.Value!;
                }

                var expressionPrefix = marker + "/input-expression/";
                foreach (var input in fields.Where(field => field.Value is not null && field.Key.StartsWith(expressionPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.InputExpressions[Unescape(input.Key[expressionPrefix.Length..])] = input.Value!;
                }

                var scriptPrefix = marker + "/input-script/";
                foreach (var input in fields.Where(field => field.Value is not null && field.Key.StartsWith(scriptPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.InputScripts[Unescape(input.Key[scriptPrefix.Length..])] = input.Value!;
                }

                var conditionPrefix = marker + "/input-condition/";
                foreach (var input in fields.Where(field => field.Value is not null && field.Key.StartsWith(conditionPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.InputConditions[Unescape(input.Key[conditionPrefix.Length..])] = PipelineConditions.Read(input.Value);
                }

                step.Cases = PipelineOutcomes.ReadCases(fields.GetValueOrDefault(marker + "/cases"));

                var savesPrefix = marker + "/output/";
                foreach (var output in fields.Where(field => field.Value is not null && field.Key.StartsWith(savesPrefix, StringComparison.Ordinal)).OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    step.OutputVariables[Unescape(output.Key[savesPrefix.Length..])] = output.Value!;
                }

                graph.Steps.Add(step);
            }

            var variableOrder = new List<string>();
            foreach (var hint in orderHints.Where(hint => hint is not null))
            {
                variableOrder.AddRange((hint!.Variables ?? new List<PipelineGraphVariable>()).Where(variable => !string.IsNullOrEmpty(variable.Id)).Select(variable => VariablePath(variable.Id)));
            }

            variableOrder.AddRange(fields.Keys.Where(IsVariableMarker).OrderBy(path => path, StringComparer.Ordinal));
            foreach (var marker in variableOrder.Distinct(StringComparer.Ordinal))
            {
                if (fields.GetValueOrDefault(marker) is not { } name)
                {
                    continue;
                }

                graph.Variables.Add(new PipelineGraphVariable
                {
                    Id = Unescape(marker["variable/".Length..]),
                    Name = name,
                    TypeName = fields.GetValueOrDefault(marker + "/type") ?? "Object",
                    Value = fields.GetValueOrDefault(marker + "/value"),
                    TestValue = fields.GetValueOrDefault(marker + "/test-value"),
                });
            }

            foreach (var path in connectionOrder.Distinct(StringComparer.Ordinal))
            {
                if (fields.GetValueOrDefault(path) is null)
                {
                    continue;
                }

                var parts = path.Split('/');
                graph.Connections.Add(new PipelineGraphConnection
                {
                    SourceId = Unescape(parts[1]),
                    SourcePort = parts[2].Length == 0 ? null : Unescape(parts[2]),
                    TargetId = Unescape(parts[3]),
                });
            }

            var noteOrder = new List<string>();
            foreach (var hint in orderHints.Where(hint => hint is not null))
            {
                noteOrder.AddRange((hint!.Notes ?? new List<PipelineGraphNote>()).Where(note => !string.IsNullOrEmpty(note.Id)).Select(note => NotePath(note.Id)));
            }

            noteOrder.AddRange(fields.Keys.Where(IsNoteMarker).OrderBy(path => path, StringComparer.Ordinal));
            foreach (var marker in noteOrder.Distinct(StringComparer.Ordinal))
            {
                if (fields.GetValueOrDefault(marker) is null)
                {
                    continue;
                }

                var note = new PipelineGraphNote
                {
                    Id = Unescape(marker["note/".Length..]),
                    Text = fields.GetValueOrDefault(marker + "/text") ?? string.Empty,
                    Position = ParsePosition(fields.GetValueOrDefault(marker + "/position")),
                    Color = PipelineNoteColors.Normalize(fields.GetValueOrDefault(marker + "/color")),
                };
                if (fields.GetValueOrDefault(marker + "/parent") is { } sitsIn && sitsIn.Split('/') is [var parent, var port])
                {
                    note.Parent = Unescape(parent);
                    note.ParentPort = port.Length == 0 ? null : Unescape(port);
                }

                graph.Notes.Add(note);
            }

            graph.RunsAtOnce = PipelineRunsAtOnce.Normalize(fields.GetValueOrDefault(RunsAtOncePath)) is { Length: > 0 } runsAtOnce ? runsAtOnce : null;
            graph.KeepSuccessfulRuns = fields.GetValueOrDefault(KeepSuccessfulRunsPath) == "false" ? false : null;
            graph.SaveProgress = fields.GetValueOrDefault(SaveProgressPath) == "true" ? true : null;
            graph.KeepStepData = PipelineStepDataKeeping.Stored(fields.GetValueOrDefault(KeepStepDataPath));
            graph.MinutesSavedPerRun = int.TryParse(fields.GetValueOrDefault(MinutesSavedPath), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutesSaved)
                ? PipelineInsights.MinutesSaved(minutesSaved)
                : null;
            graph.TimeoutMinutes = int.TryParse(fields.GetValueOrDefault(TimeoutPath), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout)
                ? PipelineRunTimeout.Minutes(timeout)
                : null;
            graph.CalledBy = PipelineCallers.Normalize(fields.GetValueOrDefault(CalledByPath));
            graph.TimeZone = PipelineTimeZone.Normalize(fields.GetValueOrDefault(TimeZonePath));
            return graph;
        }

        /// <summary>
        /// What a merge left pointing at a step that is gone - an input or a name of a removed step, a connection to or from
        /// one - as conflicts: one editor removed a step the other still used. The value each conflict proposes is what the
        /// merged set holds, and the current one what the stored graph holds.
        /// </summary>
        public static IReadOnlyList<FieldConflict> Dangling(IReadOnlyDictionary<string, string?> merged, IReadOnlyDictionary<string, string?> current)
        {
            ArgumentNullException.ThrowIfNull(merged);
            ArgumentNullException.ThrowIfNull(current);
            var conflicts = new List<FieldConflict>();
            foreach (var field in merged.Where(field => field.Value is not null).OrderBy(field => field.Key, StringComparer.Ordinal))
            {
                var stepsItNeeds = field.Key.StartsWith("step/", StringComparison.Ordinal) && !IsStepMarker(field.Key)
                    ? new[] { "step/" + field.Key.Split('/')[1] }
                    : field.Key.StartsWith("connection/", StringComparison.Ordinal) && field.Key.Split('/') is { Length: 4 } parts
                        ? new[] { "step/" + parts[1], "step/" + parts[3] }
                        : field.Key.StartsWith("variable/", StringComparison.Ordinal) && !IsVariableMarker(field.Key)
                            ? new[] { "variable/" + field.Key.Split('/')[1] }
                            : field.Key.StartsWith("note/", StringComparison.Ordinal) && !IsNoteMarker(field.Key)
                                ? new[] { "note/" + field.Key.Split('/')[1] }
                                : Array.Empty<string>();

                // A note in a body needs the step holding it, as a step in a body does.
                if (field.Key.Split('/') is ["note", _, "parent"] && field.Value!.Split('/') is [var noteHolder, _])
                {
                    stepsItNeeds = stepsItNeeds.Append("step/" + noteHolder).ToArray();
                }

                // A step that reads or saves to a variable needs the variable too: one editor removed it, the other bound it.
                if (field.Key.Split('/') is ["step", _, "input-variable" or "output", _])
                {
                    stepsItNeeds = stepsItNeeds.Append(VariablePath(field.Value!)).ToArray();
                }

                // A step in a body needs the step holding it: one editor removed the loop, the other added a step inside it.
                if (field.Key.Split('/') is ["step", _, "parent"] && field.Value!.Split('/') is [var holder, _])
                {
                    stepsItNeeds = stepsItNeeds.Append("step/" + holder).ToArray();
                }

                if (stepsItNeeds.Any(marker => merged.GetValueOrDefault(marker) is null))
                {
                    conflicts.Add(new FieldConflict(field.Key, field.Value, current.GetValueOrDefault(field.Key)));
                }
            }

            return conflicts;
        }

        /// <summary>A path as a person reads it, naming the step by its name in <paramref name="graph"/> when it has one.</summary>
        public static string Describe(string path, PipelineGraph? graph)
        {
            var parts = (path ?? string.Empty).Split('/');
            string StepName(string escapedId)
            {
                var id = Unescape(escapedId);
                var name = graph?.Steps.FirstOrDefault(step => step.Id == id)?.Name;
                return string.IsNullOrWhiteSpace(name) ? $"step {id}" : $"step '{name}'";
            }

            string NoteName(string escapedId)
            {
                var id = Unescape(escapedId);
                var text = graph?.Notes?.FirstOrDefault(note => note.Id == id)?.Text?.Trim();
                return string.IsNullOrEmpty(text) ? $"note {id}" : $"note '{(text.Length > 30 ? text[..30] + "…" : text)}'";
            }

            string VariableName(string escapedId)
            {
                var id = Unescape(escapedId);
                var name = graph?.Variables?.FirstOrDefault(variable => variable.Id == id)?.Name;
                return string.IsNullOrWhiteSpace(name) ? $"variable {id}" : $"variable '{name}'";
            }

            return parts switch
            {
                ["step", var id] => StepName(id),
                ["step", var id, "name"] => StepName(id) + ", its name",
                ["step", var id, "position"] => StepName(id) + ", its position",
                ["step", var id, "comment"] => StepName(id) + ", its comment",
                ["step", var id, "pinned"] => StepName(id) + ", its pinned result",
                ["step", var id, "retry"] => StepName(id) + ", how often and how far apart it is tried again",
                ["step", var id, "continue-on-error"] => StepName(id) + ", whether the pipeline goes on when it fails",
                ["step", var id, "failed-path"] => StepName(id) + ", whether a failure takes its Failed path",
                ["step", var id, "failure-variable"] => StepName(id) + ", the variable its error is saved to",
                ["step", var id, "disabled"] => StepName(id) + ", whether it is switched off",
                ["note", var id] => NoteName(id),
                ["note", var id, "text"] => NoteName(id) + ", its text",
                ["note", var id, "position"] => NoteName(id) + ", its position",
                ["note", var id, "parent"] => NoteName(id) + ", the step it sits in",
                ["step", var id, "input", var input] => StepName(id) + ", input '" + Unescape(input) + "'",
                ["step", var id, "input-variable", var input] => StepName(id) + ", the variable input '" + Unescape(input) + "' reads",
                ["step", var id, "input-expression", var input] => StepName(id) + ", the expression of input '" + Unescape(input) + "'",
                ["step", var id, "input-script", var input] => StepName(id) + ", the JavaScript of input '" + Unescape(input) + "'",
                ["step", var id, "input-condition", var input] => StepName(id) + ", the condition of input '" + Unescape(input) + "'",
                ["step", var id, "cases"] => StepName(id) + ", its cases",
                ["step", var id, "output", var output] => StepName(id) + ", the variable its output '" + Unescape(output) + "' is saved to",
                ["step", var id, "parent"] => StepName(id) + ", the step it sits in",
                ["variable", var id] => VariableName(id),
                ["variable", var id, "type"] => VariableName(id) + ", what it holds",
                ["variable", var id, "value"] => VariableName(id) + ", its starting value",
                ["variable", var id, "test-value"] => VariableName(id) + ", its kept test value",
                ["pipeline", "runs-at-once"] => "how many runs of the pipeline may go at once",
                ["pipeline", "keep-successful-runs"] => "whether the pipeline keeps the runs that finish well",
                ["pipeline", "save-progress"] => "whether the pipeline stores a run after each step",
                ["pipeline", "keep-step-data"] => "what the pipeline's runs keep of their steps' data",
                ["pipeline", "minutes-saved"] => "the minutes a run of the pipeline saves",
                ["pipeline", "timeout-minutes"] => "how long a run may be busy before it is stopped",
                ["pipeline", "called-by"] => "which pipelines may call the pipeline",
                ["pipeline", "time-zone"] => "the time zone the pipeline reads time in",
                ["connection", var source, var port, var target] => "the connection from " + StepName(source) + (port.Length == 0 ? string.Empty : " (" + Unescape(port) + ")") + " to " + StepName(target),
                _ => path ?? string.Empty,
            };
        }

        private static FieldKind KindOf(string path, IReadOnlyDictionary<string, string?> after, IReadOnlyDictionary<string, string?> before, Func<string, string, bool>? isTextInput)
        {
            var parts = path.Split('/');
            if (parts is ["step", _, "position"] or ["step", _, "input-expression" or "input-script", _] or ["step", _, "comment" or "pinned"] or ["note", _, "text" or "position" or "color"] or ["variable", _, "test-value"])
            {
                return FieldKind.Text; // layout, and free text - an expression, a comment, a note and its colour: last-writer-wins, with a warning
            }

            if (parts is ["step", var id, "input", var input] && isTextInput is not null)
            {
                var typeAndVersion = after.GetValueOrDefault("step/" + id) ?? before.GetValueOrDefault("step/" + id) ?? string.Empty;
                var at = typeAndVersion.LastIndexOf('@');
                return isTextInput(at < 0 ? typeAndVersion : typeAndVersion[..at], Unescape(input)) ? FieldKind.Text : FieldKind.Structured;
            }

            return FieldKind.Structured;
        }

        private static bool IsStepMarker(string path) => path.StartsWith("step/", StringComparison.Ordinal) && path.IndexOf('/', "step/".Length) < 0;

        private static bool IsVariableMarker(string path) => path.StartsWith("variable/", StringComparison.Ordinal) && path.IndexOf('/', "variable/".Length) < 0;

        private static string VariablePath(string id) => "variable/" + Escape(id);

        private static bool IsNoteMarker(string path) => path.StartsWith("note/", StringComparison.Ordinal) && path.IndexOf('/', "note/".Length) < 0;

        private static string NotePath(string id) => "note/" + Escape(id);

        private static string StepPath(string id) => "step/" + Escape(id);

        private static string ConnectionPath(string source, string? port, string target) => "connection/" + Escape(source) + "/" + Escape(port ?? string.Empty) + "/" + Escape(target);

        private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

        internal static string Unescape(string value) => Uri.UnescapeDataString(value);

        private static string Position(GraphPosition? position) =>
            (position?.X ?? 0).ToString("R", CultureInfo.InvariantCulture) + "," + (position?.Y ?? 0).ToString("R", CultureInfo.InvariantCulture);

        private static GraphPosition ParsePosition(string? value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                ? new GraphPosition { X = x, Y = y }
                : new GraphPosition();
        }
    }

    /// <summary>The editor's save while it collaborates (BL-97 increment 12): the graph it saves, and the graph it loaded - the base its changes are measured from.</summary>
    public class GraphSaveRequest
    {
        public PipelineGraph Graph { get; set; } = new PipelineGraph();

        public PipelineGraph Base { get; set; } = new PipelineGraph();
    }

    /// <summary>
    /// What a collaborating save did: <c>Applied</c> (nothing changed underneath), <c>Merged</c> (another editor's save was
    /// kept beside this one; any text it overwrote is a warning) or <c>Rejected</c> (a field this editor changed was changed
    /// by someone else - nothing was stored, and each conflict carries the value stored now). The graph as stored is the
    /// editor's next base.
    /// </summary>
    public class GraphSaveResult
    {
        public string Status { get; set; } = string.Empty;

        public PipelineView? Pipeline { get; set; }

        public PipelineGraph Graph { get; set; } = new PipelineGraph();

        public List<FieldConflict> Conflicts { get; set; } = new List<FieldConflict>();

        public List<ConflictWarning> Warnings { get; set; } = new List<ConflictWarning>();
    }
}
