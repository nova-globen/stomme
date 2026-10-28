#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Domain;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>The result of inferring a schema and a summary from sample rows, plus the bounded sample kept for preview.</summary>
    public sealed record InferenceResult(DatasetSchema Schema, DatasetSummary Summary, string SampleRowsJson);

    /// <summary>
    /// Infers a dataset's schema and metadata-only summary from a JSON array of records (BL-94 increment 9, ported from
    /// the predecessor platform): a column's type from its observed values, a few sample values, min and max, unique and null counts, and
    /// the first <see cref="DatasetLimits.MaxSampleRows"/> rows as the preview sample. A sample over
    /// <see cref="DatasetLimits.MaxSampleBytes"/> is REFUSED before anything is parsed.
    /// </summary>
    public static class SchemaInference
    {
        private const int SampleValueCount = 3;

        public static InferenceResult Infer(string? sampleJson)
        {
            if (sampleJson is not null && sampleJson.Length > DatasetLimits.MaxSampleBytes)
            {
                throw new ArgumentException($"A dataset sample must be at most {DatasetLimits.MaxSampleBytes} bytes.", nameof(sampleJson));
            }

            var rows = ParseRows(sampleJson);
            var columns = new List<DatasetColumn>();
            var summaries = new List<DatasetColumnSummary>();
            foreach (var name in OrderedColumnNames(rows))
            {
                var values = rows.Select(row => row.TryGetPropertyValue(name, out var node) ? node?.ToString() : null).ToList();
                var present = values.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToList();
                var dataType = InferType(present);
                var (min, max) = MinMax(present, dataType);
                columns.Add(new DatasetColumn(name, dataType, SemanticTypeHint(name, dataType)));
                summaries.Add(new DatasetColumnSummary(name, present.Distinct(StringComparer.Ordinal).Take(SampleValueCount).ToList(), min, max, present.Distinct(StringComparer.Ordinal).Count(), values.Count(string.IsNullOrEmpty)));
            }

            var bounded = new JsonArray(rows.Take(DatasetLimits.MaxSampleRows).Select(row => (JsonNode)row.DeepClone()).ToArray());
            return new InferenceResult(DatasetSchema.From(columns), DatasetSummary.From(summaries, rows.Count), bounded.ToJsonString());
        }

        private static List<JsonObject> ParseRows(string? sampleJson)
        {
            if (string.IsNullOrWhiteSpace(sampleJson))
            {
                return new List<JsonObject>();
            }

            try
            {
                return JsonNode.Parse(sampleJson) switch
                {
                    JsonArray array => array.OfType<JsonObject>().Select(row => row.DeepClone().AsObject()).ToList(),
                    JsonObject single => new List<JsonObject> { single.DeepClone().AsObject() },
                    _ => new List<JsonObject>(),
                };
            }
            catch (JsonException)
            {
                return new List<JsonObject>();
            }
        }

        private static List<string> OrderedColumnNames(List<JsonObject> rows)
        {
            var names = new List<string>();
            foreach (var row in rows)
            {
                foreach (var property in row)
                {
                    if (!names.Contains(property.Key, StringComparer.Ordinal))
                    {
                        names.Add(property.Key);
                    }
                }
            }

            return names;
        }

        private static ColumnDataType InferType(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return ColumnDataType.String;
            }

            if (values.All(value => bool.TryParse(value, out _)))
            {
                return ColumnDataType.Boolean;
            }

            if (values.All(value => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
            {
                return ColumnDataType.Number;
            }

            if (values.All(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)))
            {
                return ColumnDataType.Date;
            }

            if (values.All(LooksLikeJson))
            {
                return ColumnDataType.Json;
            }

            return ColumnDataType.String;
        }

        private static (string? Min, string? Max) MinMax(IReadOnlyList<string> values, ColumnDataType type)
        {
            if (values.Count == 0)
            {
                return (null, null);
            }

            switch (type)
            {
                case ColumnDataType.Number:
                    var numbers = values.Select(value => decimal.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture)).ToList();
                    return (numbers.Min().ToString(CultureInfo.InvariantCulture), numbers.Max().ToString(CultureInfo.InvariantCulture));
                case ColumnDataType.Date:
                    var dates = values.Select(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).ToList();
                    return (dates.Min().ToString("O", CultureInfo.InvariantCulture), dates.Max().ToString("O", CultureInfo.InvariantCulture));
                default:
                    return (null, null);
            }
        }

        private static string? SemanticTypeHint(string name, ColumnDataType type)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("email", StringComparison.Ordinal))
            {
                return "email";
            }

            if (lower.Contains("ip", StringComparison.Ordinal))
            {
                return "ip_address";
            }

            if (lower == "url" || lower.EndsWith("url", StringComparison.Ordinal))
            {
                return "url";
            }

            if (type == ColumnDataType.Number && (lower.Contains("price", StringComparison.Ordinal) || lower.Contains("amount", StringComparison.Ordinal) || lower.Contains("cost", StringComparison.Ordinal)))
            {
                return "currency";
            }

            return null;
        }

        private static bool LooksLikeJson(string value)
        {
            var trimmed = value.AsSpan().TrimStart();
            return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
        }
    }
}
