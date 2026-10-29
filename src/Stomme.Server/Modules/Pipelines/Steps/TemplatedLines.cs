#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Expressions;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>One entry of a line-per-entry input: the line as the author wrote it, its name (null when the line has no separator) and its value.</summary>
    public sealed record TemplatedLine(string Line, string? Name, string Value);

    /// <summary>
    /// A line-per-entry input of <c>name = value</c> (or <c>Name: value</c>) pairs, read so that a value from a run stays ONE
    /// value. The engine renders an input's template as a whole before the step reads it, so a value holding a line
    /// break, or text shaped like another entry, became lines the author never wrote: a second <c>query =</c> replacing the
    /// statement the author fixed, an <c>Authorization:</c> header, another recipient. Here, when the author wrote the input
    /// as a template, its lines are split as the author wrote them and each line's name and value rendered on their own - so
    /// whatever a run's value holds, it is the value it fills and nothing more. A line the author wrote with no separator
    /// outside its placeholders - a placeholder standing for lines of its own - is rendered and split as before: there the
    /// author handed those lines to the run on purpose. An input that is not a template (text as typed, a script, a
    /// variable) is read as it always was.
    /// </summary>
    public static class TemplatedLines
    {
        public static async ValueTask<IReadOnlyList<TemplatedLine>> ReadAsync(Input<string?>? input, StepExecutionContext context, char separator)
        {
            if (input?.Expression is not { Type: TemplateExpressionHandler.Kind } expression)
            {
                return Split(input?.GetOrDefault(context), separator);
            }

            var template = expression.Value as string ?? expression.Value?.ToString();
            var lookup = await BuiltInValues.LookupAsync(context.ExpressionExecutionContext).ConfigureAwait(false);
            var lines = new List<TemplatedLine>();
            foreach (var line in Records.Lines(template))
            {
                var at = SeparatorAt(line, separator);
                if (at > 0)
                {
                    lines.Add(new TemplatedLine(line, Render(line[..at], lookup), Render(line[(at + 1)..], lookup)));
                }
                else if (at < 0 && PipelineTemplate.HasPlaceholders(line))
                {
                    lines.AddRange(Split(Render(line, lookup), separator));
                }
                else
                {
                    lines.Add(new TemplatedLine(line, null, string.Empty));
                }
            }

            return lines;
        }

        /// <summary>
        /// <c>Name: value</c> header lines read as <see cref="ReadAsync"/> reads them - the headers, or why not: a line that is not
        /// a header, or one holding a line break (a header is one line), refused quoting the author's line, never the run's value.
        /// </summary>
        public static async ValueTask<(List<(string Name, string Value)>? Headers, string? Refusal)> HeadersAsync(Input<string?>? input, StepExecutionContext context)
        {
            var headers = new List<(string Name, string Value)>();
            foreach (var line in await ReadAsync(input, context, ':').ConfigureAwait(false))
            {
                if (line.Name is null)
                {
                    return (null, $"'{line.Line}' is not a 'Name: value' header.");
                }

                if (BreaksLine(line.Name) || BreaksLine(line.Value))
                {
                    return (null, $"'{line.Line}' gives a header holding a line break; a header is one line.");
                }

                headers.Add((line.Name, line.Value));
            }

            return (headers, null);
        }

        public static bool BreaksLine(string text) => text.Contains('\n') || text.Contains('\r');

        // The text's lines, each split at its first separator - how every line-per-entry input was read before.
        private static List<TemplatedLine> Split(string? text, char separator)
        {
            var lines = new List<TemplatedLine>();
            foreach (var line in Records.Lines(text))
            {
                var at = line.IndexOf(separator);
                lines.Add(at > 0 ? new TemplatedLine(line, line[..at].Trim(), line[(at + 1)..].Trim()) : new TemplatedLine(line, null, string.Empty));
            }

            return lines;
        }

        private static string Render(string part, System.Func<string, (bool Found, object? Value)> lookup) =>
            PipelineTemplate.Text(PipelineTemplate.Render(part.Trim(), lookup)).Trim();

        // The first separator outside a {{ }} - a filter's argument (round: 2) or a quoted text is not the line's separator.
        private static int SeparatorAt(string line, char separator)
        {
            var inside = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (i + 1 < line.Length && line[i] == (inside ? '}' : '{') && line[i + 1] == line[i])
                {
                    inside = !inside;
                    i++;
                }
                else if (!inside && line[i] == separator)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
