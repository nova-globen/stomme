#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsTextract
{
    /// <summary>
    /// Amazon Textract (n8n's AWS Textract node: a receipt's or an invoice's fields) over Textract's JSON protocol
    /// (<c>https://textract.{region}.amazonaws.com</c>, <c>X-Amz-Target: Textract.*</c>), every call signed with AWS Signature
    /// Version 4 by an IAM user's access key and secret, over the host's egress.
    /// </summary>
    /// <remarks>
    /// <c>analyze_expense</c> (n8n's Analyze Receipt or Invoice: each document's fields as Textract names them - VENDOR_NAME,
    /// TOTAL, INVOICE_RECEIPT_DATE - and its line items), and two n8n's node has not: <c>detect_text</c> (a document's lines) and
    /// <c>analyze_document</c> (a form's keys and values, and the answers to questions asked of it). A document is a file of the
    /// pipeline run's folder - up to 10 MB, sent with the call - or an object in S3, which Textract reads itself. A failure is
    /// thrown with its status, AWS's error type and its message.
    /// </remarks>
    public sealed class AwsTextractConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        /// <summary>The most a document sent with the call may hold; a larger one is read from S3.</summary>
        public const long MaxBytes = 10L * 1024 * 1024;

        private static readonly string[] Known = { "analyze_expense", "detect_text", "analyze_document" };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awstextract";

        public string DisplayName => "Amazon Textract";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://textract.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/textract/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/textract/latest/dg/API_Reference.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region Textract is called in - eu-west-1. The address is Textract's in it when left empty."),
        });

        private static ConnectorConfigField[] Source() => new[]
        {
            new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: false, Description: "The document in the site's pipeline folder - kvitton/1042.png. Or an S3 object."),
            new ConnectorConfigField("bucket", "S3 bucket", ConnectorConfigFieldType.Text, Required: false, Description: "The bucket an S3 document is in - kafe-kvitton."),
            new ConnectorConfigField("object", "S3 object", ConnectorConfigFieldType.Text, Required: false, Description: "Its name - 2026/1042.pdf."),
            new ConnectorConfigField("version", "S3 version", ConnectorConfigFieldType.Text, Required: false, Description: "Its version, when the bucket keeps versions."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("analyze_expense", "Analyze a receipt or invoice", "Its fields - vendor, total, date - and its line items - n8n's Analyze Receipt or Invoice.") { Parameters = new(Source()) },
            new ConnectorOperation("detect_text", "Detect text", "A document's lines, each with its page and confidence.") { Parameters = new(Source()) },
            new ConnectorOperation("analyze_document", "Analyze a form", "A form's keys and values - and, when questions are asked, their answers.")
            {
                Parameters = new(Source().Append(new ConnectorConfigField("questions", "Questions", ConnectorConfigFieldType.Multiline, Required: false, Description: "One question a line - What is the customer's name? Empty: the form's keys and values.")).ToArray()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                // No call Textract answers without a document: a deliberately empty one, refused as invalid, proves the key and region.
                var session = AwsJsonSession.Of("Amazon Textract", "textract", "Textract", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                try
                {
                    await session.CallAsync("DetectDocumentText", new JsonObject { ["Document"] = new JsonObject { ["Bytes"] = string.Empty } }, cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("InvalidParameterException", StringComparison.Ordinal) || ex.Message.Contains("UnsupportedDocumentException", StringComparison.Ordinal) || ex.Message.Contains("ValidationException", StringComparison.Ordinal))
                {
                    // The key was taken; the empty document was not - as meant.
                }

                return ConnectionTestResult.Ok($"Amazon Textract in {session.Region} answers.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "analyze_expense").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Amazon Textract has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            AwsMediaSource.FromFile(p);
            var questions = (p.TryGetValue("questions", out var asked) ? asked : string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (questions.Count > 15 || questions.Any(question => question.Length > 200))
            {
                throw new InvalidOperationException("Ask at most 15 questions, each at most 200 characters (questions).");
            }

            var session = AwsJsonSession.Of("Amazon Textract", "textract", "Textract", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            var document = await AwsMediaSource.ReadAsync(p, context.Files, MaxBytes, "Textract", cancellationToken);
            switch (operation)
            {
                case "analyze_expense":
                    {
                        var answer = await session.CallAsync("AnalyzeExpense", new JsonObject { ["Document"] = document }, cancellationToken) as JsonObject ?? new JsonObject();
                        var index = 0;
                        foreach (var expense in Items(answer["ExpenseDocuments"]))
                        {
                            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["document"] = ++index };
                            foreach (var field in Items(expense["SummaryFields"]))
                            {
                                if (Text(field["Type"]?["Text"]) is { Length: > 0 } type && !fields.ContainsKey(type))
                                {
                                    fields[type] = Text(field["ValueDetection"]?["Text"]);
                                }
                            }

                            var lines = new JsonArray();
                            foreach (var group in Items(expense["LineItemGroups"]))
                            {
                                foreach (var item in Items(group["LineItems"]))
                                {
                                    var line = new JsonObject();
                                    foreach (var field in Items(item["LineItemExpenseFields"]))
                                    {
                                        if (Text(field["Type"]?["Text"]) is { Length: > 0 } type && type != "EXPENSE_ROW")
                                        {
                                            line[type] = Text(field["ValueDetection"]?["Text"]);
                                        }
                                    }

                                    lines.Add(line);
                                }
                            }

                            fields["lineItems"] = lines.ToJsonString();
                            yield return new ConnectorRecord(fields);
                        }

                        break;
                    }

                case "detect_text":
                    {
                        var answer = await session.CallAsync("DetectDocumentText", new JsonObject { ["Document"] = document }, cancellationToken) as JsonObject ?? new JsonObject();
                        foreach (var line in Items(answer["Blocks"]).Where(block => Text(block["BlockType"]) == "LINE"))
                        {
                            yield return Record(("text", Text(line["Text"])), ("confidence", Number(line["Confidence"])), ("page", Integer(line["Page"]) ?? 1));
                        }

                        break;
                    }

                default:
                    {
                        var body = new JsonObject { ["Document"] = document, ["FeatureTypes"] = new JsonArray(questions.Count > 0 ? "QUERIES" : "FORMS") };
                        if (questions.Count > 0)
                        {
                            body["QueriesConfig"] = new JsonObject { ["Queries"] = new JsonArray(questions.Select(question => (JsonNode)new JsonObject { ["Text"] = question }).ToArray()) };
                        }

                        var answer = await session.CallAsync("AnalyzeDocument", body, cancellationToken) as JsonObject ?? new JsonObject();
                        var blocks = Items(answer["Blocks"]).Where(block => Text(block["Id"]) is not null).ToDictionary(block => Text(block["Id"])!, StringComparer.Ordinal);
                        if (questions.Count > 0)
                        {
                            foreach (var query in blocks.Values.Where(block => Text(block["BlockType"]) == "QUERY"))
                            {
                                var result = Related(query, "ANSWER", blocks).FirstOrDefault();
                                yield return Record(("question", Text(query["Query"]?["Text"])), ("answer", result is null ? null : Text(result["Text"])), ("confidence", result is null ? null : Number(result["Confidence"])));
                            }

                            break;
                        }

                        // A form: each KEY block's words, its VALUE block's words.
                        foreach (var key in blocks.Values.Where(block => Text(block["BlockType"]) == "KEY_VALUE_SET" && block["EntityTypes"] is JsonArray types && types.Any(type => Text(type) == "KEY")))
                        {
                            var value = Related(key, "VALUE", blocks).FirstOrDefault();
                            yield return Record(("key", Words(key, blocks)), ("value", value is null ? null : Words(value, blocks)), ("confidence", Number(key["Confidence"])), ("page", Integer(key["Page"]) ?? 1));
                        }

                        break;
                    }
            }
        }

        // The blocks a block names in its relationships of one type.
        private static IEnumerable<JsonObject> Related(JsonObject block, string type, IReadOnlyDictionary<string, JsonObject> blocks) =>
            Items(block["Relationships"]).Where(relationship => Text(relationship["Type"]) == type)
                .SelectMany(relationship => (relationship["Ids"] as JsonArray ?? new JsonArray()).Select(id => Text(id)))
                .Where(id => id is not null && blocks.ContainsKey(id)).Select(id => blocks[id!]);

        // A block's text: its child words, a ticked box as X.
        private static string Words(JsonObject block, IReadOnlyDictionary<string, JsonObject> blocks) =>
            string.Join(" ", Related(block, "CHILD", blocks).Select(child => Text(child["BlockType"]) == "SELECTION_ELEMENT" ? (Text(child["SelectionStatus"]) == "SELECTED" ? "X" : string.Empty) : Text(child["Text"]) ?? string.Empty).Where(word => word.Length > 0));

        private static IEnumerable<JsonObject> Items(JsonNode? node) => (node as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

        private static long? Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
    }
}
