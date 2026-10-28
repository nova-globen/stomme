#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A CONDITION built from rules (BL-229 increment 10, n8n's IF and Switch): each rule compares two sides - text with
    /// <c>{{ variable.path }}</c> placeholders, as an expression is - and the condition holds when all of them do, or any.
    /// Nothing runs but the comparison. It is what a Decision, an If and a While test, and what a Switch case matches on.
    /// </summary>
    public class PipelineCondition
    {
        /// <summary><see cref="PipelineConditions.All"/> or <see cref="PipelineConditions.Any"/>.</summary>
        public string Match { get; set; } = PipelineConditions.All;

        /// <summary>Text compares ignoring case (numbers and dates compare as numbers and dates either way).</summary>
        public bool IgnoreCase { get; set; }

        public List<PipelineConditionRule> Rules { get; set; } = new List<PipelineConditionRule>();
    }

    public class PipelineConditionRule
    {
        /// <summary>The value tested: a template, e.g. <c>{{ order.status }}</c>.</summary>
        public string Left { get; set; } = "";

        /// <summary>One of <see cref="PipelineConditions.Operators"/>' codes.</summary>
        public string Operator { get; set; } = PipelineConditions.EqualsOperator;

        /// <summary>What it is compared with: a template or plain text; unused by an operator that takes one side.</summary>
        public string Right { get; set; } = "";
    }

    /// <summary>An operator as the editor offers it: its code, its words, and whether it needs a second side.</summary>
    public sealed record PipelineConditionOperator(string Code, string Label, bool TakesValue);

    /// <summary>
    /// The ONE evaluator of a <see cref="PipelineCondition"/> - the engine's (the module's <c>Condition</c> expression kind) and
    /// the editor's preview. Two sides that both read as numbers compare as numbers, both as dates (ISO 8601) as dates, else as
    /// text (ordinal, or ignoring case when the condition says so). "is empty" is true of nothing, an empty text, an empty list or
    /// record. A placeholder naming no variable is an error, as in an expression.
    /// </summary>
    public static class PipelineConditions
    {
        public const string All = "all";
        public const string Any = "any";

        public const string EqualsOperator = "equals";

        public static readonly IReadOnlyList<PipelineConditionOperator> Operators = new[]
        {
            new PipelineConditionOperator(EqualsOperator, "is equal to", true),
            new PipelineConditionOperator("not-equals", "is not equal to", true),
            new PipelineConditionOperator("contains", "contains", true),
            new PipelineConditionOperator("not-contains", "does not contain", true),
            new PipelineConditionOperator("starts-with", "starts with", true),
            new PipelineConditionOperator("ends-with", "ends with", true),
            new PipelineConditionOperator("greater", "is greater than", true),
            new PipelineConditionOperator("at-least", "is at least", true),
            new PipelineConditionOperator("less", "is less than", true),
            new PipelineConditionOperator("at-most", "is at most", true),
            new PipelineConditionOperator("empty", "is empty", false),
            new PipelineConditionOperator("not-empty", "is not empty", false),
            new PipelineConditionOperator("true", "is true", false),
            new PipelineConditionOperator("false", "is false", false),
        };

        public static PipelineConditionOperator? Operator(string? code) => Operators.FirstOrDefault(candidate => candidate.Code == code);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        /// <summary>The condition as the text the engine's document carries.</summary>
        public static string Write(PipelineCondition condition) => JsonSerializer.Serialize(condition ?? new PipelineCondition(), Json);

        /// <summary>The condition a document carries; an empty condition for text that is not one.</summary>
        public static PipelineCondition Read(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new PipelineCondition();
            }

            try
            {
                var condition = JsonSerializer.Deserialize<PipelineCondition>(text, Json) ?? new PipelineCondition();
                condition.Rules = (condition.Rules ?? new List<PipelineConditionRule>()).Where(rule => rule is not null).ToList();
                condition.Match = condition.Match == Any ? Any : All;
                return condition;
            }
            catch (JsonException)
            {
                return new PipelineCondition();
            }
        }

        /// <summary>
        /// A condition held as a value (Filter records' input): nothing yet is an empty condition, a condition the editor wrote is
        /// that condition, and anything else is not one - never silently read as "no rule".
        /// </summary>
        public static bool TryRead(string? text, out PipelineCondition condition)
        {
            condition = new PipelineCondition();
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            condition = Read(text);
            return true;
        }

        /// <summary>A copy, so an editor's change to one never reaches another.</summary>
        public static PipelineCondition Copy(PipelineCondition? condition) => Read(Write(condition ?? new PipelineCondition()));

        /// <summary>What the condition is not yet: no rule, a rule with no operator this evaluator knows, a side left empty.</summary>
        public static string? Incomplete(PipelineCondition? condition)
        {
            if (condition?.Rules is not { Count: > 0 } rules)
            {
                return "needs at least one rule.";
            }

            foreach (var rule in rules)
            {
                var op = Operator(rule.Operator);
                if (op is null)
                {
                    return $"'{rule.Operator}' is not a comparison.";
                }

                if (string.IsNullOrWhiteSpace(rule.Left))
                {
                    return "has a rule that tests nothing - give it a value, e.g. {{ status }}.";
                }
            }

            return null;
        }

        /// <summary>The variables the condition reads, each once.</summary>
        public static IReadOnlyList<string> Names(PipelineCondition? condition) =>
            (condition?.Rules ?? new List<PipelineConditionRule>())
                .SelectMany(rule => PipelineTemplate.Names(rule.Left).Concat(Operator(rule.Operator)?.TakesValue == false ? Array.Empty<string>() : PipelineTemplate.Names(rule.Right)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>The condition in words, e.g. <c>{{ status }} is equal to ok and {{ total }} is greater than 100</c>.</summary>
        public static string Describe(PipelineCondition? condition)
        {
            if (condition?.Rules is not { Count: > 0 } rules)
            {
                return "(no rule)";
            }

            return string.Join(condition.Match == Any ? " or " : " and ", rules.Select(rule =>
            {
                var op = Operator(rule.Operator);
                return op is null ? rule.Left + " ? " + rule.Right
                    : op.TakesValue ? $"{rule.Left} {op.Label} {rule.Right}" : $"{rule.Left} {op.Label}";
            }));
        }

        /// <summary>Whether the condition holds, each side rendered from the variables.</summary>
        /// <exception cref="PipelineTemplateException">A side names no variable, or the condition is incomplete.</exception>
        public static bool Evaluate(PipelineCondition? condition, Func<string, (bool Found, object? Value)> lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);
            if (Incomplete(condition) is { } incomplete)
            {
                throw new PipelineTemplateException("The condition " + incomplete);
            }

            var results = condition!.Rules.Select(rule => Holds(rule, condition.IgnoreCase, lookup));
            return condition.Match == Any ? results.Any(result => result) : results.All(result => result);
        }

        /// <summary>
        /// Whether the condition holds for one record (BL-229, n8n's Filter): <c>{{ field }}</c> reads the record's field, a name
        /// the record lacks reads <paramref name="variables"/>, and a name neither has is empty - a record without a field is one
        /// whose field is empty, not an error.
        /// </summary>
        /// <exception cref="PipelineTemplateException">The condition is incomplete.</exception>
        public static bool HoldsFor(PipelineCondition? condition, JsonElement record, Func<string, (bool Found, object? Value)> variables)
        {
            ArgumentNullException.ThrowIfNull(variables);
            return Evaluate(condition, name =>
            {
                if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var field))
                {
                    return (true, field);
                }

                var variable = variables(name);
                return variable.Found ? variable : (true, null);
            });
        }

        /// <summary>Whether a text is a date as a condition compares one: ISO 8601 (so "3" and "12" stay numbers and "May" text).</summary>
        public static bool IsDate(string text) => LooksLikeDate(text);

        private static bool Holds(PipelineConditionRule rule, bool ignoreCase, Func<string, (bool Found, object? Value)> lookup)
        {
            var left = PipelineTemplate.Render(rule.Left, lookup);
            var op = Operator(rule.Operator)!;
            if (!op.TakesValue)
            {
                return op.Code switch
                {
                    "empty" => IsEmpty(left),
                    "not-empty" => !IsEmpty(left),
                    "true" => IsTrue(left),
                    _ => IsFalse(left),
                };
            }

            var right = PipelineTemplate.Render(rule.Right, lookup);
            var leftText = PipelineTemplate.Text(left);
            var rightText = PipelineTemplate.Text(right);
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return op.Code switch
            {
                "equals" => Compare(leftText, rightText, comparison) == 0,
                "not-equals" => Compare(leftText, rightText, comparison) != 0,
                "contains" => Contains(left, leftText, rightText, comparison),
                "not-contains" => !Contains(left, leftText, rightText, comparison),
                "starts-with" => leftText.StartsWith(rightText, comparison),
                "ends-with" => leftText.EndsWith(rightText, comparison),
                "greater" => Compare(leftText, rightText, comparison) > 0,
                "at-least" => Compare(leftText, rightText, comparison) >= 0,
                "less" => Compare(leftText, rightText, comparison) < 0,
                _ => Compare(leftText, rightText, comparison) <= 0,
            };
        }

        // Numbers as numbers, dates as dates, anything else as text.
        private static int Compare(string left, string right, StringComparison comparison)
        {
            if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber)
                && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            if (LooksLikeDate(left) && LooksLikeDate(right)
                && DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var leftDate)
                && DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rightDate))
            {
                return leftDate.CompareTo(rightDate);
            }

            return Math.Sign(string.Compare(left, right, comparison));
        }

        // An ISO 8601 date: four digits, a dash - so "3" and "12" stay numbers and "May" stays text.
        private static bool LooksLikeDate(string text) => text.Length >= 10 && char.IsAsciiDigit(text[0]) && text.Length > 4 && text[4] == '-';

        // A list contains an ITEM equal to the other side; a text contains the other side as text.
        private static bool Contains(object? left, string leftText, string rightText, StringComparison comparison)
        {
            if (AsArray(left) is { } items)
            {
                return items.Any(item => string.Equals(PipelineTemplate.Text(item), rightText, comparison));
            }

            return leftText.Contains(rightText, comparison);
        }

        private static List<JsonElement>? AsArray(object? value)
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
            {
                return array.EnumerateArray().ToList();
            }

            if (value is string text && text.TrimStart().StartsWith('['))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.Clone().EnumerateArray().ToList() : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            return null;
        }

        private static bool IsEmpty(object? value)
        {
            if (value is null)
            {
                return true;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => true,
                    JsonValueKind.String => string.IsNullOrEmpty(element.GetString()),
                    JsonValueKind.Array => element.GetArrayLength() == 0,
                    JsonValueKind.Object => !element.EnumerateObject().Any(),
                    _ => false,
                };
            }

            var text = PipelineTemplate.Text(value).Trim();
            return text.Length == 0 || text is "[]" or "{}";
        }

        private static bool IsTrue(object? value) => PipelineTemplate.Text(value).Trim() is var text && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1");

        private static bool IsFalse(object? value) => PipelineTemplate.Text(value).Trim() is var text && (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0");
    }
}
