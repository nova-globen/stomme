#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What changed from one graph to another, in words (a version compared with the canvas): steps,
    /// variables and connections added or removed, and every name, input, expression, binding and body that changed - read
    /// off the collaboration fields (<see cref="PipelineGraphFields"/>), so it sees exactly what a merge sees. A step's
    /// position is layout, not a change of what the pipeline does, and is left out.
    /// </summary>
    public static class PipelineGraphDiff
    {
        /// <summary>A step, a variable, a note or a connection the second graph has and the first has not.</summary>
        public const string Added = "added";

        /// <summary>One the first graph has and the second has not.</summary>
        public const string Removed = "removed";

        /// <summary>Something on one both have: its name, an input, a binding, its cases.</summary>
        public const string Changed = "changed";

        public static List<string> Lines(PipelineGraph? from, PipelineGraph? to) => Changes(from, to).Select(change => change.Line).ToList();

        /// <summary>
        /// The same changes, each with what it is about, for the canvas to mark while a version is compared: the step
        /// (a connection's: the step it leaves) and, for a connection, the outcome it leaves by and the step it goes to.
        /// </summary>
        public static List<PipelineGraphChange> Changes(PipelineGraph? from, PipelineGraph? to)
        {
            var before = PipelineGraphFields.ToFields(from);
            var after = PipelineGraphFields.ToFields(to);
            var changes = new List<PipelineGraphChange>();
            var addedOrRemoved = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
            {
                var was = before.GetValueOrDefault(path);
                var now = after.GetValueOrDefault(path);
                if (string.Equals(was, now, StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = path.Split('/');
                switch (parts)
                {
                    case ["step", _] or ["variable", _] or ["note", _]:
                        addedOrRemoved.Add(path);
                        changes.Add(Of(parts, (now is null ? "Removed " : "Added ") + PipelineGraphFields.Describe(path, now is null ? from : to), now is null ? Removed : Added));
                        break;
                    case ["step", var id, ..] when addedOrRemoved.Contains("step/" + id):
                    case ["variable", var variable, ..] when addedOrRemoved.Contains("variable/" + variable):
                    case ["note", var note, ..] when addedOrRemoved.Contains("note/" + note):
                    case ["step", _, "position"] or ["note", _, "position"]:
                        break; // said with the step, variable or note itself, or layout
                    case ["connection", ..]:
                        changes.Add(Of(parts, (now is null ? "Removed " : "Added ") + PipelineGraphFields.Describe(path, now is null ? from : to), now is null ? Removed : Added));
                        break;
                    case ["step", _, "input-condition", _]:
                        // A condition in words, not its JSON.
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + Said(was is null ? null : PipelineConditions.Describe(PipelineConditions.Read(was))) + " → " + Said(now is null ? null : PipelineConditions.Describe(PipelineConditions.Read(now))), Changed));
                        break;
                    case ["step", _, "cases"]:
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + Said(CasesInWords(was)) + " → " + Said(CasesInWords(now)), Changed));
                        break;
                    case ["step", _, "input-variable" or "output", _]:
                        // A binding's value is a variable's id: say its name.
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + Named(was, from) + " → " + Named(now, to), Changed));
                        break;
                    case ["step", _, "disabled" or "continue-on-error" or "failed-path"]:
                        // A switch is there or not: yes or no (found driving the compare on the canvas: "(none) → '1'").
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + (was is null ? "no" : "yes") + " → " + (now is null ? "no" : "yes"), Changed));
                        break;
                    case ["step", _, "retry"]:
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + Retried(was) + " → " + Retried(now), Changed));
                        break;
                    case ["pipeline", "keep-successful-runs"]:
                        // Kept unless it says otherwise: the field is there only when they are not.
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + (was is null ? "yes" : "no") + " → " + (now is null ? "yes" : "no"), Changed));
                        break;
                    default:
                        changes.Add(Of(parts, PipelineGraphFields.Describe(path, to) + ": " + Shown(was) + " → " + Shown(now), Changed));
                        break;
                }
            }

            return changes;
        }

        /// <summary>
        /// The changes counted, as the canvas says them while a version is compared: "1 new step · 2 changed steps · 1 removed
        /// connection" - a variable, a note or a setting of the pipeline counts as one of the "other changes"; "no difference" for none.
        /// </summary>
        public static string Summary(IReadOnlyCollection<PipelineGraphChange> changes)
        {
            var steps = changes.Where(change => !change.IsConnection && change.StepId is not null).GroupBy(change => change.StepId, StringComparer.Ordinal).ToList();
            var added = steps.Count(step => step.Any(change => change.Kind == Added));
            var removed = steps.Count(step => step.Any(change => change.Kind == Removed));
            var parts = new[]
            {
                Counted(added, "new step", "new steps"),
                Counted(steps.Count - added - removed, "changed step", "changed steps"),
                Counted(removed, "removed step", "removed steps"),
                Counted(changes.Count(change => change.IsConnection && change.Kind == Added), "new connection", "new connections"),
                Counted(changes.Count(change => change.IsConnection && change.Kind == Removed), "removed connection", "removed connections"),
                Counted(changes.Count(change => change.StepId is null), "other change", "other changes"),
            }.Where(part => part is not null).ToList();
            return parts.Count == 0 ? "no difference" : string.Join(" · ", parts);
        }

        private static string? Counted(int count, string one, string many) => count == 0 ? null : count == 1 ? "1 " + one : count + " " + many;

        private static PipelineGraphChange Of(string[] parts, string line, string kind) => new()
        {
            Line = line,
            Kind = kind,
            StepId = parts is ["step" or "connection", var id, ..] ? PipelineGraphFields.Unescape(id) : null,
            SourcePort = parts is ["connection", _, var port, _] ? PipelineGraphFields.Unescape(port) : null,
            TargetId = parts is ["connection", _, _, var target] ? PipelineGraphFields.Unescape(target) : null,
        };

        // A step's retry field ("3/5": three more tries, five seconds apart) as it reads.
        private static string Retried(string? value)
        {
            var parts = (value ?? string.Empty).Split('/');
            if (value is null || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tries) || tries <= 0)
            {
                return "not again";
            }

            var seconds = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var delay) ? delay : 0;
            return (tries == 1 ? "once" : tries + " times") + (seconds > 0 ? ", " + seconds + " s apart" : string.Empty);
        }

        private static string Named(string? variableId, PipelineGraph? graph) =>
            variableId is null ? "(none)" : graph?.Variables?.FirstOrDefault(variable => variable.Id == variableId)?.Name is { Length: > 0 } name ? "'" + name + "'" : Shown(variableId);

        private static string? CasesInWords(string? text) =>
            text is null ? null : string.Join("; ", PipelineOutcomes.ReadCases(text).Select(@case => @case.Label + ": " + PipelineConditions.Describe(@case.Condition)));

        private static string Said(string? words) =>
            words is null ? "(none)" : words.Length == 0 ? "(empty)" : "'" + (words.Length > 160 ? words[..160] + "…" : words) + "'";

        private static string Shown(string? value) =>
            value is null ? "(none)" : value.Length == 0 ? "(empty)" : "'" + (value.Length > 60 ? value[..60] + "…" : value) + "'";
    }

    /// <summary>
    /// One change from a graph to another (<see cref="PipelineGraphDiff.Changes"/>): in words; whether it added, removed or changed
    /// something (<see cref="PipelineGraphDiff.Added"/>, ...); the step it is about - a connection's: the step it leaves - and, for a
    /// connection, the outcome it leaves by ("" for the step's one way on) and the step it goes to. A variable, a note and a setting
    /// of the pipeline are about no step.
    /// </summary>
    public sealed class PipelineGraphChange
    {
        public string Line { get; init; } = string.Empty;

        public string Kind { get; init; } = PipelineGraphDiff.Changed;

        public string? StepId { get; init; }

        public string? SourcePort { get; init; }

        public string? TargetId { get; init; }

        public bool IsConnection => TargetId is not null;
    }
}
