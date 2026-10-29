#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// n8n's Execution Data: key/value pairs a run saves about itself ("order: 42", "customer: Ada") with the "Save run
    /// data" step, shown on its page and in the runs list and found by the runs search. The one rule for what they may be.
    /// </summary>
    public static class PipelineRunData
    {
        public const int MaxKeys = 20;

        public const int MaxKeyLength = 50;

        public const int MaxValueLength = 500;

        /// <summary>
        /// The pairs given: <c>key: value</c> (or <c>key=value</c>) one per line, or a JSON object of plain values. Keys trimmed, a
        /// later key replacing an earlier one; null, with the reason, when the text is not pairs or a pair is too long.
        /// </summary>
        public static Dictionary<string, string>? Parse(string? text, out string? error)
        {
            error = null;
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            var trimmed = (text ?? "").Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        pairs[property.Name.Trim()] = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString() ?? "",
                            JsonValueKind.Null => "",
                            JsonValueKind.Object or JsonValueKind.Array => property.Value.GetRawText(),
                            _ => property.Value.GetRawText(),
                        };
                    }
                }
                catch (JsonException ex)
                {
                    error = "The run data is not a JSON object: " + ex.Message;
                    return null;
                }
            }
            else
            {
                foreach (var line in trimmed.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
                {
                    var at = line.IndexOfAny([':', '=']);
                    if (at <= 0)
                    {
                        error = $"'{line}' is not 'key: value'.";
                        return null;
                    }

                    pairs[line[..at].Trim()] = line[(at + 1)..].Trim();
                }
            }

            return Check(pairs, out error) ? pairs : null;
        }

        /// <summary>Whether the pairs may be kept: keys present and short, values short, not too many.</summary>
        public static bool Check(IReadOnlyDictionary<string, string> pairs, out string? error)
        {
            error = null;
            if (pairs.Count > MaxKeys)
            {
                error = $"A run keeps at most {MaxKeys} pieces of data.";
            }
            else if (pairs.Keys.FirstOrDefault(key => key.Length == 0) is not null)
            {
                error = "A piece of run data needs a key.";
            }
            else if (pairs.Keys.FirstOrDefault(key => key.Length > MaxKeyLength) is { } longKey)
            {
                error = $"'{longKey[..20]}…' is over {MaxKeyLength} characters - a key is a short name.";
            }
            else if (pairs.FirstOrDefault(pair => pair.Value.Length > MaxValueLength) is { Key: not null } longValue)
            {
                error = $"'{longValue.Key}' is over {MaxValueLength} characters.";
            }

            return error is null;
        }

        /// <summary>
        /// Whether a run's data answers the runs search: <c>key: value</c> or <c>key=value</c> - that key holding that value, ignoring
        /// case - else any value equal to the search, ignoring case.
        /// </summary>
        public static bool Matches(IReadOnlyDictionary<string, string>? data, string? search)
        {
            if (data is null || data.Count == 0 || string.IsNullOrWhiteSpace(search))
            {
                return false;
            }

            var term = search.Trim();
            var at = term.IndexOfAny([':', '=']);
            if (at > 0)
            {
                var key = term[..at].Trim();
                var value = term[(at + 1)..].Trim();
                return data.Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));
            }

            return data.Values.Any(value => string.Equals(value, term, StringComparison.OrdinalIgnoreCase));
        }
    }
}
