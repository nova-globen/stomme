#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The shape an AI answer is held to (n8n's Structured Output Parser): an EXAMPLE of the answer - every field it names is
    /// required, of the kind its value is (text, a number, true or false, an object, a list; null takes anything), a list's items
    /// held to its first item - or a JSON SCHEMA, as n8n's parser takes one: <c>type</c> (one, or several), <c>properties</c>,
    /// <c>required</c>, <c>items</c>, <c>enum</c>; an integer a whole number. A shape is a schema when it says <c>$schema</c>, or its
    /// <c>type</c> is one of the schema's types with <c>properties</c>, <c>items</c> or <c>enum</c> beside it, or alone. Fields the
    /// shape does not name are allowed. Each problem is said with where it is (<c>$.items[1].sku</c>).
    /// </summary>
    public static class AiAnswerShape
    {
        /// <summary>The most problems said of one answer; the rest counted.</summary>
        public const int MostProblems = 10;

        private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal) { "object", "array", "string", "number", "integer", "boolean", "null" };

        /// <summary>The shape as JSON, or what is wrong with it.</summary>
        public static JsonNode? Read(string? shape, out string? problem)
        {
            problem = null;
            try
            {
                var node = JsonNode.Parse(AskAi.Unfenced(shape ?? ""));
                if (node is null)
                {
                    problem = "The answer shape is null - give an example of the answer, or a JSON Schema.";
                }

                return node;
            }
            catch (JsonException ex)
            {
                problem = "The answer shape is not JSON - give an example of the answer, or a JSON Schema: " + ex.Message;
                return null;
            }
        }

        /// <summary>Whether a shape is a JSON Schema rather than an example of the answer.</summary>
        public static bool IsSchema(JsonNode shape) =>
            shape is JsonObject root && (root.ContainsKey("$schema")
                || (root["type"] is JsonNode type && Types(type) is { Count: > 0 } types && types.All(SchemaTypes.Contains)
                    && (root.ContainsKey("properties") || root.ContainsKey("items") || root.ContainsKey("enum") || root.Count == 1)));

        /// <summary>What the model is told: the shape, and that it answers with JSON of it alone.</summary>
        public static string Instruction(JsonNode shape) =>
            (IsSchema(shape) ? "Answer with JSON only, valid against this JSON Schema:\n" : "Answer with JSON only, of exactly this shape - the same fields, each of the same kind:\n")
            + shape.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        /// <summary>Where the answer does not fit the shape - none when it fits.</summary>
        public static List<string> Problems(JsonNode? answer, JsonNode shape)
        {
            var problems = new List<string>();
            if (IsSchema(shape))
            {
                AgainstSchema(answer, (JsonObject)shape, "$", problems);
            }
            else
            {
                AgainstExample(answer, shape, "$", problems);
            }

            return problems.Count <= MostProblems ? problems : problems.Take(MostProblems).Append($"... and {problems.Count - MostProblems} more.").ToList();
        }

        private static void AgainstExample(JsonNode? answer, JsonNode? example, string path, List<string> problems)
        {
            if (example is null)
            {
                return; // null in the example takes anything
            }

            var wanted = Kind(example) is "integer" ? "number" : Kind(example); // an example's 0 stands for any number
            if (answer is null || !Fits(answer, wanted))
            {
                problems.Add($"{path}: {Article(wanted)}, not {(answer is null ? "null" : Article(Kind(answer)))}.");
                return;
            }

            switch (example)
            {
                case JsonObject fields:
                    var given = (JsonObject)answer;
                    foreach (var (name, value) in fields)
                    {
                        if (!given.ContainsKey(name))
                        {
                            problems.Add($"{path}.{name}: missing.");
                            continue;
                        }

                        AgainstExample(given[name], value, path + "." + name, problems);
                    }

                    break;
                case JsonArray list when list.Count > 0:
                    var items = (JsonArray)answer;
                    for (var at = 0; at < items.Count; at++)
                    {
                        AgainstExample(items[at], list[0], $"{path}[{at}]", problems);
                    }

                    break;
            }
        }

        private static void AgainstSchema(JsonNode? answer, JsonObject schema, string path, List<string> problems)
        {
            if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, answer)))
            {
                problems.Add($"{path}: one of {string.Join(", ", choices.Select(choice => choice?.ToJsonString() ?? "null"))}, not {answer?.ToJsonString() ?? "null"}.");
                return;
            }

            if (schema["type"] is JsonNode type && Types(type) is { Count: > 0 } types)
            {
                var kind = answer is null ? "null" : Kind(answer);
                var fits = types.Any(wanted => wanted == kind || (wanted == "number" && kind == "integer") || (wanted == "integer" && kind == "integer"));
                if (!fits)
                {
                    problems.Add($"{path}: {string.Join(" or ", types.Select(Article))}, not {Article(kind)}.");
                    return;
                }
            }

            if (answer is JsonObject given)
            {
                foreach (var name in (schema["required"] as JsonArray ?? new JsonArray()).Select(item => item?.GetValue<string>()).Where(name => name is not null))
                {
                    if (!given.ContainsKey(name!))
                    {
                        problems.Add($"{path}.{name}: missing.");
                    }
                }

                foreach (var (name, property) in schema["properties"] as JsonObject ?? new JsonObject())
                {
                    if (given.ContainsKey(name) && property is JsonObject inner)
                    {
                        AgainstSchema(given[name], inner, path + "." + name, problems);
                    }
                }
            }

            if (answer is JsonArray items && schema["items"] is JsonObject each)
            {
                for (var at = 0; at < items.Count; at++)
                {
                    AgainstSchema(items[at], each, $"{path}[{at}]", problems);
                }
            }
        }

        private static List<string> Types(JsonNode type) => type switch
        {
            JsonValue value when value.TryGetValue<string>(out var one) => new List<string> { one },
            JsonArray many => many.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : "").ToList(),
            _ => new List<string>(),
        };

        // A node's kind as a schema names it: a whole number is an integer (and so a number too).
        private static string Kind(JsonNode node) => node.GetValueKind() switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => node.AsValue().TryGetValue<decimal>(out var number) && number == decimal.Truncate(number) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null",
        };

        // An example's number takes any number; its other kinds, only themselves.
        private static bool Fits(JsonNode answer, string wanted)
        {
            var kind = Kind(answer);
            return kind == wanted || (wanted is "number" or "integer" && kind is "number" or "integer");
        }

        private static string Article(string kind) => kind switch
        {
            "object" => "an object",
            "array" => "a list",
            "string" => "text",
            "integer" => "a whole number",
            "number" => "a number",
            "boolean" => "true or false",
            _ => "null",
        };
    }
}
