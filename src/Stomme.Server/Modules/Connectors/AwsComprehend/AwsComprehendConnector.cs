#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsComprehend
{
    /// <summary>
    /// Amazon Comprehend (BL-229, n8n's AWS Comprehend node: a text's language, sentiment and entities) over Comprehend's JSON
    /// protocol (<c>https://comprehend.{region}.amazonaws.com</c>, <c>X-Amz-Target: Comprehend_20171127.*</c>), every call signed
    /// with AWS Signature Version 4 by an IAM user's access key and secret, over the host's egress. A test detects the language of
    /// a word.
    /// </summary>
    /// <remarks>
    /// <c>detect_language</c> (each language a text is in, with its score), <c>detect_sentiment</c> (positive, negative, neutral or
    /// mixed, and each one's score), <c>detect_entities</c> (people, places, organisations, dates, quantities...), and two n8n's
    /// node has not - <c>detect_key_phrases</c> and <c>detect_pii</c> (the personal data a text holds: names, addresses,
    /// account numbers...). A text is the step's - over several lines from its Query box - held to Comprehend's limit in bytes
    /// before anything is signed. A failure is thrown with its status, AWS's error type and its message.
    /// </remarks>
    public sealed class AwsComprehendConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        // The languages Comprehend reads a text in, for sentiment, entities, key phrases (PII: English and Spanish alone).
        private static readonly string[] Languages = { "en", "es", "fr", "de", "it", "pt", "ar", "hi", "ja", "ko", "zh", "zh-TW" };

        private static readonly (string Name, string Action, int MaxBytes)[] Known =
        {
            ("detect_language", "DetectDominantLanguage", 100_000), ("detect_sentiment", "DetectSentiment", 5_000), ("detect_entities", "DetectEntities", 100_000),
            ("detect_key_phrases", "DetectKeyPhrases", 100_000), ("detect_pii", "DetectPiiEntities", 100_000),
        };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awscomprehend";

        public string DisplayName => "Amazon Comprehend";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://comprehend.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/comprehend/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/comprehend/latest/APIReference/welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region Comprehend is called in - eu-west-1. The address is Comprehend's in it when left empty."),
        });

        private static ConnectorConfigField TextField() => new("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The text - or over several lines in the step's Query box.");

        private static ConnectorConfigField LanguageField() => new("language", "Language", ConnectorConfigFieldType.Text, Required: false, Description: "The text's language - en (the default), sv is not one: es, fr, de, it, pt, ar, hi, ja, ko, zh or zh-TW.") { Default = "en" };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("detect_language", "Detect the language", "Each language a text is in, with its score - n8n's Detect Dominant Language.") { Parameters = new(new[] { TextField() }) },
            new ConnectorOperation("detect_sentiment", "Detect the sentiment", "Positive, negative, neutral or mixed, and each one's score - n8n's Detect Sentiment. Up to 5,000 bytes.") { Parameters = new(new[] { TextField(), LanguageField() }) },
            new ConnectorOperation("detect_entities", "Detect entities", "The people, places, organisations, dates and quantities a text names - n8n's Detect Entities.") { Parameters = new(new[] { TextField(), LanguageField() }) },
            new ConnectorOperation("detect_key_phrases", "Detect key phrases", "The phrases a text is about.") { Parameters = new(new[] { TextField(), LanguageField() }) },
            new ConnectorOperation("detect_pii", "Detect personal data", "The personal data a text holds - names, addresses, account numbers - by where it stands. English or Spanish.") { Parameters = new(new[] { TextField(), LanguageField() }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = AwsJsonSession.Of("Amazon Comprehend", "comprehend", "Comprehend_20171127", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                var answer = await session.CallAsync("DetectDominantLanguage", new JsonObject { ["Text"] = "Hej" }, cancellationToken);
                return ConnectionTestResult.Ok($"Amazon Comprehend in {session.Region} answers ({(answer?["Languages"] as JsonArray)?.Count ?? 0} language(s) for a word).");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var known = Known.FirstOrDefault(entry => string.Equals(entry.Name, (context.OperationName ?? "detect_language").Trim(), StringComparison.OrdinalIgnoreCase));
            if (known.Name is null)
            {
                throw new InvalidOperationException($"Amazon Comprehend has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1].Select(entry => entry.Name))} or {Known[^1].Name}.");
            }

            var p = context.Parameters;
            var text = p.TryGetValue("text", out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw
                : p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box
                : throw new InvalidOperationException("Give the text - or write it in the step's Query box.");
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > known.MaxBytes)
            {
                throw new InvalidOperationException($"The text is {bytes} bytes; Comprehend reads at most {known.MaxBytes} in {known.Name}.");
            }

            var body = new JsonObject { ["Text"] = text };
            if (known.Name != "detect_language")
            {
                var language = Values.GetOrDefault(p, "language", "en");
                var allowed = known.Name == "detect_pii" ? new[] { "en", "es" } : Languages;
                body["LanguageCode"] = allowed.FirstOrDefault(code => string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"'{language}' is not a language Comprehend reads here - {string.Join(", ", allowed)} (language).");
            }

            var session = AwsJsonSession.Of("Amazon Comprehend", "comprehend", "Comprehend_20171127", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            var answer = await session.CallAsync(known.Action, body, cancellationToken) as JsonObject ?? new JsonObject();
            switch (known.Name)
            {
                case "detect_language":
                    foreach (var language in Items(answer["Languages"]))
                    {
                        yield return Record(("languageCode", Text(language["LanguageCode"])), ("score", Number(language["Score"])));
                    }

                    break;
                case "detect_sentiment":
                    {
                        var scores = answer["SentimentScore"] as JsonObject;
                        yield return Record(
                            ("sentiment", Text(answer["Sentiment"])), ("positive", Number(scores?["Positive"])), ("negative", Number(scores?["Negative"])),
                            ("neutral", Number(scores?["Neutral"])), ("mixed", Number(scores?["Mixed"])));
                        break;
                    }

                case "detect_entities":
                    foreach (var entity in Items(answer["Entities"]))
                    {
                        yield return Record(("text", Text(entity["Text"])), ("type", Text(entity["Type"])), ("score", Number(entity["Score"])), ("beginOffset", Integer(entity["BeginOffset"])), ("endOffset", Integer(entity["EndOffset"])));
                    }

                    break;
                case "detect_key_phrases":
                    foreach (var phrase in Items(answer["KeyPhrases"]))
                    {
                        yield return Record(("text", Text(phrase["Text"])), ("score", Number(phrase["Score"])), ("beginOffset", Integer(phrase["BeginOffset"])), ("endOffset", Integer(phrase["EndOffset"])));
                    }

                    break;
                default: // detect_pii: where each stands - its text read from the text itself, as Comprehend gives offsets alone
                    foreach (var entity in Items(answer["Entities"]))
                    {
                        var begin = Integer(entity["BeginOffset"]);
                        var end = Integer(entity["EndOffset"]);
                        var found = begin is { } from && end is { } to && from >= 0 && to <= text.Length && from < to ? text[(int)from..(int)to] : null;
                        yield return Record(("text", found), ("type", Text(entity["Type"])), ("score", Number(entity["Score"])), ("beginOffset", begin), ("endOffset", end));
                    }

                    break;
            }
        }

        internal static IEnumerable<JsonObject> Items(JsonNode? node) => (node as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        internal static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        internal static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

        internal static long? Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
    }
}
