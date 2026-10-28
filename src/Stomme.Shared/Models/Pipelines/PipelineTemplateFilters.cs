#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Stomme.Models.Pipelines
{
    /// <summary>A filter as the editor offers it: its name, what it does, and how it is written with an example argument.</summary>
    public sealed record PipelineTemplateFilter(string Name, string Description, string Example);

    /// <summary>
    /// FILTERS in an expression (BL-229, n8n's expression functions, written as Liquid writes them): after a placeholder's path,
    /// each <c>| name</c> or <c>| name: argument, argument</c> turns the value into another - <c>{{ name | upper }}</c>,
    /// <c>{{ amount | round: 2 }}</c>, <c>{{ $now | date: "dd MMM yyyy" }}</c>, <c>{{ tags | join: ", " }}</c>. Arguments are
    /// literals - "quoted text" or a bare number or word. No code runs: each filter is one of the fixed list below, run by the
    /// one renderer the engine and the editor share. A filter that is not one, or a number filter given no number, is an
    /// error saying which.
    /// </summary>
    public static class PipelineTemplateFilters
    {
        public static readonly IReadOnlyList<PipelineTemplateFilter> All = new[]
        {
            new PipelineTemplateFilter("upper", "the text in capitals", "upper"),
            new PipelineTemplateFilter("lower", "the text in small letters", "lower"),
            new PipelineTemplateFilter("capitalize", "the text with its first letter a capital", "capitalize"),
            new PipelineTemplateFilter("trim", "the text without spaces at its ends", "trim"),
            new PipelineTemplateFilter("default", "the argument when the value is empty", "default: \"none\""),
            new PipelineTemplateFilter("length", "how many characters, or items in a list", "length"),
            new PipelineTemplateFilter("truncate", "the text cut to so many characters", "truncate: 20"),
            new PipelineTemplateFilter("replace", "the text with one text replaced by another", "replace: \"a\", \"b\""),
            new PipelineTemplateFilter("split", "the text as a list, cut at the argument", "split: \",\""),
            new PipelineTemplateFilter("join", "a list as text, the argument between items", "join: \", \""),
            new PipelineTemplateFilter("first", "a list's first item", "first"),
            new PipelineTemplateFilter("last", "a list's last item", "last"),
            new PipelineTemplateFilter("number", "the text as a number", "number"),
            new PipelineTemplateFilter("round", "the number rounded to so many decimals", "round: 2"),
            new PipelineTemplateFilter("fixed", "the number written with exactly so many decimals", "fixed: 2"),
            new PipelineTemplateFilter("plus", "the number plus the argument", "plus: 1"),
            new PipelineTemplateFilter("minus", "the number minus the argument", "minus: 1"),
            new PipelineTemplateFilter("times", "the number times the argument", "times: 2"),
            new PipelineTemplateFilter("divide", "the number divided by the argument", "divide: 2"),
            new PipelineTemplateFilter("date", "a date written with a pattern, in a time zone if given", "date: \"dd MMM yyyy HH:mm\""),
            new PipelineTemplateFilter("json", "the value as JSON", "json"),
            new PipelineTemplateFilter("urlencode", "the text made safe in an address", "urlencode"),
            new PipelineTemplateFilter("html", "the text made safe in HTML - its <, >, & and quotes encoded", "html"),
            new PipelineTemplateFilter("raw", "the value as it is, on purpose - HTML you made, put in a page unencoded", "raw"),
        };

        /// <summary>The path before the first filter, and each filter after it with its arguments.</summary>
        public static (string Path, IReadOnlyList<(string Name, IReadOnlyList<string> Arguments)> Filters) Parse(string placeholder)
        {
            var parts = SplitOutsideQuotes(placeholder, '|');
            var filters = new List<(string, IReadOnlyList<string>)>();
            foreach (var part in parts.Skip(1))
            {
                var cut = IndexOutsideQuotes(part, ':');
                var name = (cut < 0 ? part : part[..cut]).Trim();
                var arguments = cut < 0
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : SplitOutsideQuotes(part[(cut + 1)..], ',').Select(Unquote).ToList();
                filters.Add((name, arguments));
            }

            return (parts[0].Trim(), filters);
        }

        /// <summary>What is wrong with a template's filters, before it runs: a name that is not a filter, a number argument that is no number.</summary>
        public static IReadOnlyList<string> Problems(string? template)
        {
            var problems = new List<string>();
            foreach (var placeholder in PipelineTemplate.Placeholders(template))
            {
                foreach (var (name, arguments) in Parse(placeholder).Filters)
                {
                    if (All.All(filter => filter.Name != name))
                    {
                        problems.Add(NotAFilter(name));
                    }
                    else if (name is "round" or "fixed" or "truncate" or "plus" or "minus" or "times" or "divide"
                        && arguments.Count > 0 && !TryNumber(arguments[0], out _))
                    {
                        problems.Add($"{name}: '{arguments[0]}' is not a number.");
                    }
                }
            }

            return problems;
        }

        /// <summary>The value with the filters applied in order.</summary>
        /// <exception cref="PipelineTemplateException">A filter is not one, or cannot take the value or its argument.</exception>
        public static object? Apply(object? value, IReadOnlyList<(string Name, IReadOnlyList<string> Arguments)> filters)
        {
            foreach (var (name, arguments) in filters)
            {
                value = ApplyOne(value, name, arguments);
            }

            return value;
        }

        private static object? ApplyOne(object? value, string name, IReadOnlyList<string> arguments)
        {
            string Text() => PipelineTemplate.Text(value);
            string Argument(int index, string fallback) => arguments.Count > index ? arguments[index] : fallback;
            switch (name)
            {
                case "upper":
                    return Text().ToUpperInvariant();
                case "lower":
                    return Text().ToLowerInvariant();
                case "capitalize":
                    var text = Text();
                    return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
                case "trim":
                    return Text().Trim();
                case "default":
                    return value is null || Text().Length == 0 ? Argument(0, string.Empty) : value;
                case "length":
                    return List(value) is { } items ? items.Count : Text().Length;
                case "truncate":
                    var whole = Text();
                    var keep = (int)Number(Argument(0, "50"), name);
                    return whole.Length <= keep ? whole : whole[..Math.Max(0, keep)] + "…";
                case "replace":
                    return arguments.Count == 0 || arguments[0].Length == 0 ? Text() : Text().Replace(arguments[0], Argument(1, string.Empty), StringComparison.Ordinal);
                case "split":
                    return JsonSerializer.SerializeToElement(Text().Split(Argument(0, ","), StringSplitOptions.None));
                case "join":
                    return List(value) is { } joined ? string.Join(Argument(0, ", "), joined.Select(PipelineTemplate.Text)) : Text();
                case "first":
                    return List(value) is { Count: > 0 } firstOf ? firstOf[0] : null;
                case "last":
                    return List(value) is { Count: > 0 } lastOf ? lastOf[^1] : null;
                case "number":
                    return Number(Text(), name);
                case "round":
                    return Math.Round(Number(Text(), name), (int)Math.Clamp(Number(Argument(0, "0"), name), 0, 28), MidpointRounding.AwayFromZero);
                case "fixed":
                    return Number(Text(), name).ToString("F" + (int)Math.Clamp(Number(Argument(0, "2"), name), 0, 28), CultureInfo.InvariantCulture);
                case "plus":
                    return Number(Text(), name) + Number(Argument(0, "0"), name);
                case "minus":
                    return Number(Text(), name) - Number(Argument(0, "0"), name);
                case "times":
                    return Number(Text(), name) * Number(Argument(0, "1"), name);
                case "divide":
                    var by = Number(Argument(0, "1"), name);
                    return by == 0 ? throw new PipelineTemplateException("divide: cannot divide by 0.") : Number(Text(), name) / by;
                case "date":
                    return Date(Text(), Argument(0, "yyyy-MM-dd'T'HH:mm:ss'Z'"), arguments.Count > 1 ? arguments[1] : null);
                case "json":
                    return value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);
                case "urlencode":
                    return Uri.EscapeDataString(Text());
                case "html":
                    return System.Net.WebUtility.HtmlEncode(Text());
                case "raw":
                    return value;
                default:
                    throw new PipelineTemplateException(NotAFilter(name));
            }
        }

        private static string NotAFilter(string name) =>
            $"'{name}' is not a filter - use {string.Join(", ", All.Select(filter => filter.Name))}.";

        private static bool TryNumber(string text, out decimal number) =>
            decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

        private static decimal Number(string text, string filter) =>
            TryNumber(text, out var number) ? number : throw new PipelineTemplateException($"{filter}: '{text}' is not a number.");

        // A list's items - from JSON, from the JSON text the data steps pass, or from a list - or null for anything else.
        private static IReadOnlyList<object?>? List(object? value)
        {
            if (value is string text && text.TrimStart().StartsWith('['))
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

            return value switch
            {
                JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(item => (object?)item).ToList(),
                string => null,
                IEnumerable items => items.Cast<object?>().ToList(),
                _ => null,
            };
        }

        // Nothing is nothing - a value not set yet is no typo - but text that is no date fails, saying so.
        private static string Date(string text, string pattern, string? zoneName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment))
            {
                throw new PipelineTemplateException($"date: '{text}' is not a date.");
            }

            var zone = TimeZoneInfo.Utc;
            if (!string.IsNullOrWhiteSpace(zoneName))
            {
                try
                {
                    zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName.Trim());
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    throw new PipelineTemplateException($"date: '{zoneName}' is not a time zone - use an IANA name such as Europe/Stockholm.");
                }
            }

            try
            {
                return TimeZoneInfo.ConvertTime(moment, zone).ToString(pattern, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new PipelineTemplateException($"date: '{pattern}' is not a date pattern.");
            }
        }

        private static string Unquote(string argument)
        {
            var trimmed = argument.Trim();
            return trimmed.Length >= 2 && (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '\'' && trimmed[^1] == '\'')
                ? trimmed[1..^1]
                : trimmed;
        }

        private static List<string> SplitOutsideQuotes(string text, char separator)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            char? quote = null;
            foreach (var character in text)
            {
                if (quote is null && (character == '"' || character == '\''))
                {
                    quote = character;
                }
                else if (quote == character)
                {
                    quote = null;
                }

                if (quote is null && character == separator)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(character);
            }

            parts.Add(current.ToString());
            return parts;
        }

        private static int IndexOutsideQuotes(string text, char wanted)
        {
            char? quote = null;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (quote is null && (character == '"' || character == '\''))
                {
                    quote = character;
                }
                else if (quote == character)
                {
                    quote = null;
                }
                else if (quote is null && character == wanted)
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
