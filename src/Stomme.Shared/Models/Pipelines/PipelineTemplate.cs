#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// An input's EXPRESSION: text with <c>{{ name }}</c> placeholders, each a pipeline variable and, after
    /// it, a path into what the variable holds - <c>{{ person.town }}</c>, <c>{{ records[0].name }}</c>. No code runs: a
    /// placeholder reads, nothing else. ONE implementation, run by the engine when the step runs and by the editor for its
    /// preview, so what the author sees is what the step gets.
    /// <list type="bullet">
    /// <item>A template that is exactly one placeholder is the value itself - a list stays a list, a number a number.</item>
    /// <item>Otherwise each placeholder is replaced by the value's text: a string as it is, a number in the invariant culture,
    /// true/false, an object or a list as JSON, nothing for null or a path that leads nowhere.</item>
    /// <item>A variable's text that is JSON (how the data steps pass records) is read as the JSON it is.</item>
    /// <item>A name no variable has is an error (<see cref="PipelineTemplateException"/>): a typo fails loudly, never as an
    /// empty value.</item>
    /// <item>After the path, filters turn the value into another - <c>{{ name | upper }}</c>, <c>{{ amount | round: 2 }}</c>
    /// (<see cref="PipelineTemplateFilters"/>) - and a quoted text in place of a path is that text (<c>{{ "a b" | urlencode }}</c>).</item>
    /// </list>
    /// </summary>
    public static class PipelineTemplate
    {
        private static readonly Regex Placeholder = new(@"\{\{(?<path>[^{}]*)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>The variables a template reads, in order, each once.</summary>
        public static IReadOnlyList<string> Names(string? template)
        {
            if (string.IsNullOrEmpty(template))
            {
                return Array.Empty<string>();
            }

            return Placeholder.Matches(template)
                .Select(match => PipelineTemplateFilters.Parse(match.Groups["path"].Value).Path)
                .Where(path => !IsLiteral(path))
                .Select(path => Split(path).Name)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The template with each placeholder that reads the variable <paramref name="from"/> reading <paramref name="to"/> instead
        /// (a variable renamed) - its path and filters as they were; a quoted text, and the same word further along a path,
        /// left alone.
        /// </summary>
        public static string? Rename(string? template, string from, string to)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                return template;
            }

            var name = new Regex(@"^(?<space>\s*)" + Regex.Escape(from) + @"(?=\s*(?:$|[.\[|]))", RegexOptions.CultureInvariant);
            return Placeholder.Replace(template, match => "{{" + name.Replace(match.Groups["path"].Value, found => found.Groups["space"].Value + to, 1) + "}}");
        }

        /// <summary>What each placeholder holds, as written - its path and any filters after it.</summary>
        public static IReadOnlyList<string> Placeholders(string? template) =>
            string.IsNullOrEmpty(template) ? Array.Empty<string>() : Placeholder.Matches(template).Select(match => match.Groups["path"].Value).ToList();

        // A quoted text in place of a path: the value is the text itself ({{ "a b" | urlencode }}).
        private static bool IsLiteral(string path) =>
            path.Length >= 2 && (path[0] == '"' && path[^1] == '"' || path[0] == '\'' && path[^1] == '\'');

        /// <summary>Whether the text holds a placeholder at all.</summary>
        public static bool HasPlaceholders(string? template) => !string.IsNullOrEmpty(template) && Placeholder.IsMatch(template);

        private static readonly Regex OpenPlaceholder = new(@"\{\{(?<partial>[^{}]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// What the author may mean next (the editor's suggestions): each variable as <c>{{ name }}</c> and, for a value that is
        /// a record or a list of records, its fields (<c>{{ person.town }}</c>, <c>{{ people[0].town }}</c>). After an open
        /// <c>{{</c> only the placeholders containing what follows it are offered.
        /// </summary>
        public static IReadOnlyList<string> Suggest(string? text, IEnumerable<(string Name, object? Value)> variables, int max = 12)
        {
            var open = OpenPlaceholder.Match(text ?? string.Empty);
            var typed = open.Success ? open.Groups["partial"].Value.Trim() : string.Empty;

            // After a bar, the filters (PipelineTemplateFilters) whose names start with what follows it, each written out.
            var bar = typed.LastIndexOf('|');
            if (bar >= 0)
            {
                var before = typed[..bar].Trim();
                var started = typed[(bar + 1)..].Trim();
                return PipelineTemplateFilters.All
                    .Where(filter => filter.Name.StartsWith(started, StringComparison.OrdinalIgnoreCase))
                    .Take(max)
                    .Select(filter => "{{ " + before + " | " + filter.Example + " }}")
                    .ToList();
            }

            var paths = new List<string>();
            foreach (var (name, value) in variables ?? Enumerable.Empty<(string, object?)>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                paths.Add(name);
                paths.AddRange(Fields(value).Select(field => name + field));
            }

            return paths
                .Where(path => typed.Length == 0 || path.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Take(max)
                .Select(path => "{{ " + path + " }}")
                .ToList();
        }

        /// <summary>
        /// The text with a suggestion taken: an open <c>{{</c> at its end is completed to the placeholder, else the placeholder
        /// is added at the end.
        /// </summary>
        public static string Accept(string? text, string placeholder)
        {
            text ??= string.Empty;
            var open = OpenPlaceholder.Match(text);
            return open.Success ? text[..open.Index] + placeholder : text + placeholder;
        }

        // The first-level fields of a record (".town"), or of a list's first record ("[0].town").
        private static IEnumerable<string> Fields(object? value)
        {
            if (value is string text && LooksLikeJson(text))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    value = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    yield break;
                }
            }

            if (value is JsonElement { ValueKind: JsonValueKind.Object } record)
            {
                foreach (var property in record.EnumerateObject())
                {
                    yield return "." + property.Name;
                }
            }
            else if (value is JsonElement { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
            {
                yield return "[0]";
                if (list[0].ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in list[0].EnumerateObject())
                    {
                        yield return "[0]." + property.Name;
                    }
                }
            }
            else if (value is IDictionary<string, object?> dictionary)
            {
                foreach (var key in dictionary.Keys)
                {
                    yield return "." + key;
                }
            }
        }

        /// <summary>
        /// The template's value. <paramref name="lookup"/> answers a variable by name: whether there is one, and what it holds.
        /// </summary>
        /// <exception cref="PipelineTemplateException">A placeholder names no variable, or is empty.</exception>
        public static object? Render(string? template, Func<string, (bool Found, object? Value)> lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);
            if (string.IsNullOrEmpty(template))
            {
                return template;
            }

            var matches = Placeholder.Matches(template);
            if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == template.Length)
            {
                return Plain(Value(matches[0].Groups["path"].Value, lookup));
            }

            var text = new StringBuilder();
            var at = 0;
            foreach (Match match in matches)
            {
                text.Append(template, at, match.Index - at);
                text.Append(Text(Value(match.Groups["path"].Value, lookup)));
                at = match.Index + match.Length;
            }

            text.Append(template, at, template.Length - at);
            return text.ToString();
        }

        /// <summary>A value as the template writes it into text.</summary>
        public static string Text(object? value) => Normal(value) switch
        {
            null => string.Empty,
            string text => text,
            bool flag => flag ? "true" : "false",
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText(),
            },
            IFormattable formattable when value is not IEnumerable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => JsonSerializer.Serialize(other),
        };

        // A value held as a JSON node - how the engine gives back a variable of any kind once a paused run is read again - read as
        // the same JSON as an element, so it reads as the value did before the pause (found building sub-pipelines: after a Wait,
        // "{{ who }}" came out "\"Ada\"" and "{{ who.name }}" empty).
        private static object? Normal(object? value) => value is JsonNode node ? JsonSerializer.SerializeToElement(node) : value;

        // A placeholder's value: its path read (or its quoted text), then its filters applied in order (PipelineTemplateFilters).
        private static object? Value(string placeholder, Func<string, (bool Found, object? Value)> lookup)
        {
            var (path, filters) = PipelineTemplateFilters.Parse(placeholder);
            var value = IsLiteral(path) ? path[1..^1] : Resolve(path, lookup);
            return filters.Count == 0 ? value : PipelineTemplateFilters.Apply(value, filters);
        }

        // A placeholder's path: the variable's name, then each .field or [index].
        private static object? Resolve(string path, Func<string, (bool Found, object? Value)> lookup)
        {
            var (name, steps) = Split(path);
            if (name.Length == 0)
            {
                throw new PipelineTemplateException("An empty {{ }} names no variable.");
            }

            var (found, held) = lookup(name);
            if (!found)
            {
                throw new PipelineTemplateException($"There is no variable named '{name}'.");
            }

            var value = Normal(held);
            foreach (var step in steps)
            {
                value = Into(value, step);
                if (value is null)
                {
                    return null;
                }
            }

            return value;
        }

        private static (string Name, List<string> Steps) Split(string path)
        {
            var trimmed = path.Trim();
            var steps = new List<string>();
            var cut = trimmed.IndexOfAny(new[] { '.', '[' });
            var name = (cut < 0 ? trimmed : trimmed[..cut]).Trim();
            var rest = cut < 0 ? string.Empty : trimmed[cut..];
            foreach (Match segment in Regex.Matches(rest, @"\.\s*(?<field>[^.\[\]]+)|\[\s*(?<index>[^\]]*)\]"))
            {
                steps.Add(segment.Groups["field"].Success ? segment.Groups["field"].Value.Trim() : segment.Groups["index"].Value.Trim().Trim('\'', '"'));
            }

            return (name, steps);
        }

        // One step into a value: a field of an object, an item of a list - whatever the value is made of.
        private static object? Into(object? value, string step)
        {
            value = Normal(value);
            if (value is JsonElement { ValueKind: JsonValueKind.String } textElement)
            {
                value = textElement.GetString(); // JSON text kept as a JSON string - a record saved as text, read after a pause
            }

            if (value is string text && LooksLikeJson(text))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    value = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            switch (value)
            {
                case JsonElement { ValueKind: JsonValueKind.Object } element:
                    return element.TryGetProperty(step, out var property) ? property : FindCaseInsensitive(element, step);
                case JsonElement { ValueKind: JsonValueKind.Array } element:
                    return int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < element.GetArrayLength() ? element[index] : null;
                case IDictionary<string, object?> dictionary:
                    return dictionary.TryGetValue(step, out var held) ? held : dictionary.FirstOrDefault(pair => string.Equals(pair.Key, step, StringComparison.OrdinalIgnoreCase)).Value;
                case IDictionary dictionary:
                    return dictionary.Contains(step) ? dictionary[step] : null;
                case IList list:
                    return int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) && position >= 0 && position < list.Count ? list[position] : null;
                case null:
                    return null;
                default:
                    return value.GetType().GetProperty(step, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(value);
            }
        }

        private static object? FindCaseInsensitive(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static bool LooksLikeJson(string text)
        {
            var trimmed = text.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[');
        }

        // The value a whole-placeholder template gives: a JSON scalar as the .NET value it is; anything else as it is.
        private static object? Plain(object? value) => Normal(value) switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            // Each branch boxed: "? whole : GetDouble()" alone is a double, and a whole number would come back as 85.0.
            JsonElement { ValueKind: JsonValueKind.Number } element => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
            JsonElement element => element.GetRawText(),
            _ => value,
        };
    }

    /// <summary>A template that cannot be read: a placeholder naming no variable, or an empty one.</summary>
    public sealed class PipelineTemplateException(string message) : Exception(message);
}
