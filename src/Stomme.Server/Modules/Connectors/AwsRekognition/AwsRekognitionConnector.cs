#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsRekognition
{
    /// <summary>
    /// Amazon Rekognition (n8n's AWS Rekognition node: an image analysed) over Rekognition's JSON protocol
    /// (<c>https://rekognition.{region}.amazonaws.com</c>, <c>X-Amz-Target: RekognitionService.*</c>), every call signed with AWS
    /// Signature Version 4 by an IAM user's access key and secret, over the host's egress. A test lists the account's face
    /// collections, one.
    /// </summary>
    /// <remarks>
    /// <c>detect_labels</c> (what an image shows), <c>detect_text</c> (its lines - and words when asked), <c>detect_faces</c>
    /// (each face's age range, its expression and what it wears), <c>detect_moderation_labels</c> (what may be unsafe) and
    /// <c>recognize_celebrities</c> - n8n's five analyses. An image is a file of the pipeline run's folder - up to 5 MB, sent with
    /// the call - or an object in S3, which Rekognition reads itself. A failure is thrown with its status, AWS's error type and
    /// its message.
    /// </remarks>
    public sealed class AwsRekognitionConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        /// <summary>The most an image sent with the call may hold; a larger one is read from S3.</summary>
        public const long MaxBytes = 5L * 1024 * 1024;

        private static readonly (string Name, string Action)[] Known =
        {
            ("detect_labels", "DetectLabels"), ("detect_text", "DetectText"), ("detect_faces", "DetectFaces"), ("detect_moderation_labels", "DetectModerationLabels"), ("recognize_celebrities", "RecognizeCelebrities"),
        };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awsrekognition";

        public string DisplayName => "Amazon Rekognition";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://rekognition.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/rekognition/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/rekognition/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region Rekognition is called in - eu-west-1. The address is Rekognition's in it when left empty."),
        });

        private static ConnectorConfigField[] Source(params ConnectorConfigField[] more) => new[]
        {
            new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: false, Description: "The image in the site's pipeline folder - foton/kassa.jpg. Or an S3 object."),
            new ConnectorConfigField("bucket", "S3 bucket", ConnectorConfigFieldType.Text, Required: false, Description: "The bucket an S3 image is in - kafe-foton."),
            new ConnectorConfigField("object", "S3 object", ConnectorConfigFieldType.Text, Required: false, Description: "Its name - 2026/kassa.jpg."),
            new ConnectorConfigField("version", "S3 version", ConnectorConfigFieldType.Text, Required: false, Description: "Its version, when the bucket keeps versions."),
        }.Concat(more).ToArray();

        private static ConnectorConfigField MinConfidence() => new("min_confidence", "Least confidence", ConnectorConfigFieldType.Integer, Required: false, Description: "The least confidence, 0 to 100, a result needs. Empty: Rekognition's own.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("detect_labels", "Detect labels", "What an image shows - objects, scenes, concepts - n8n's Detect Labels.")
            {
                Parameters = new(Source(new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many labels, at most."), MinConfidence())),
            },
            new ConnectorOperation("detect_text", "Detect text", "An image's lines - and words when asked - n8n's Detect Text.")
            {
                Parameters = new(Source(new ConnectorConfigField("words", "Words too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Each word as well as each line.") { Default = "false" }, MinConfidence())),
            },
            new ConnectorOperation("detect_faces", "Detect faces", "Each face: where it is, its age range, its expression, what it wears - n8n's Detect Faces.")
            {
                Parameters = new(Source(new ConnectorConfigField("details", "Every detail", ConnectorConfigFieldType.Boolean, Required: false, Description: "Age, expression, glasses, beard... rather than where it is alone.") { Default = "true" })),
            },
            new ConnectorOperation("detect_moderation_labels", "Detect unsafe content", "What may be unsafe in an image - n8n's Detect Moderation Labels.") { Parameters = new(Source(MinConfidence())) },
            new ConnectorOperation("recognize_celebrities", "Recognize celebrities", "The celebrities an image shows - n8n's Recognize Celebrity.") { Parameters = new(Source()) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = AwsJsonSession.Of("Amazon Rekognition", "rekognition", "RekognitionService", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                await session.CallAsync("ListCollections", new JsonObject { ["MaxResults"] = 1 }, cancellationToken);
                return ConnectionTestResult.Ok($"Amazon Rekognition in {session.Region} answers.");
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
            var known = Known.FirstOrDefault(entry => string.Equals(entry.Name, (context.OperationName ?? "detect_labels").Trim(), StringComparison.OrdinalIgnoreCase));
            if (known.Name is null)
            {
                throw new InvalidOperationException($"Amazon Rekognition has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1].Select(entry => entry.Name))} or {Known[^1].Name}.");
            }

            var p = context.Parameters;
            AwsMediaSource.FromFile(p);
            int? Whole(string name, int low, int high) => Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= low && number <= high ? number : throw new InvalidOperationException($"'{given}' is not a number from {low} to {high} ({name}).")
                : null;
            var max = Whole("max", 1, 1000);
            var confidence = Whole("min_confidence", 0, 100);
            var words = Bool(p, "words", false);
            var details = Bool(p, "details", true);

            var session = AwsJsonSession.Of("Amazon Rekognition", "rekognition", "RekognitionService", context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            var body = new JsonObject { ["Image"] = await AwsMediaSource.ReadAsync(p, context.Files, MaxBytes, "Rekognition", cancellationToken) };
            switch (known.Name)
            {
                case "detect_labels":
                    if (max is { } most)
                    {
                        body["MaxLabels"] = most;
                    }

                    if (confidence is { } least)
                    {
                        body["MinConfidence"] = least;
                    }

                    break;
                case "detect_text" when confidence is { } textLeast:
                    body["Filters"] = new JsonObject { ["WordFilter"] = new JsonObject { ["MinConfidence"] = textLeast } };
                    break;
                case "detect_faces":
                    body["Attributes"] = new JsonArray(details ? "ALL" : "DEFAULT");
                    break;
                case "detect_moderation_labels" when confidence is { } unsafeLeast:
                    body["MinConfidence"] = unsafeLeast;
                    break;
            }

            var answer = await session.CallAsync(known.Action, body, cancellationToken) as JsonObject ?? new JsonObject();
            switch (known.Name)
            {
                case "detect_labels":
                    foreach (var label in Items(answer["Labels"]))
                    {
                        yield return Record(
                            ("name", Text(label["Name"])), ("confidence", Number(label["Confidence"])),
                            ("parents", Joined(label["Parents"], "Name")), ("categories", Joined(label["Categories"], "Name")), ("instances", (label["Instances"] as JsonArray)?.Count));
                    }

                    break;
                case "detect_text":
                    foreach (var detection in Items(answer["TextDetections"]).Where(detection => words || Text(detection["Type"]) == "LINE"))
                    {
                        yield return Record(("text", Text(detection["DetectedText"])), ("type", Text(detection["Type"])?.ToLowerInvariant()), ("confidence", Number(detection["Confidence"])), ("id", Integer(detection["Id"])), ("parentId", Integer(detection["ParentId"])));
                    }

                    break;
                case "detect_faces":
                    foreach (var face in Items(answer["FaceDetails"]))
                    {
                        var emotion = Items(face["Emotions"]).OrderByDescending(entry => Number(entry["Confidence"]) ?? 0).FirstOrDefault();
                        yield return Record(
                            ("confidence", Number(face["Confidence"])), ("boundingBox", (face["BoundingBox"] as JsonObject)?.ToJsonString()),
                            ("ageLow", Integer(face["AgeRange"]?["Low"])), ("ageHigh", Integer(face["AgeRange"]?["High"])),
                            ("emotion", emotion is null ? null : Text(emotion["Type"])?.ToLowerInvariant()),
                            ("smile", Flag(face["Smile"])), ("eyeglasses", Flag(face["Eyeglasses"])), ("sunglasses", Flag(face["Sunglasses"])),
                            ("beard", Flag(face["Beard"])), ("mustache", Flag(face["Mustache"])), ("eyesOpen", Flag(face["EyesOpen"])), ("mouthOpen", Flag(face["MouthOpen"])),
                            ("faceOccluded", Flag(face["FaceOccluded"])));
                    }

                    break;
                case "detect_moderation_labels":
                    foreach (var label in Items(answer["ModerationLabels"]))
                    {
                        yield return Record(("name", Text(label["Name"])), ("parent", Text(label["ParentName"]) is { Length: > 0 } parent ? parent : null), ("confidence", Number(label["Confidence"])), ("level", Integer(label["TaxonomyLevel"])));
                    }

                    break;
                default:
                    foreach (var celebrity in Items(answer["CelebrityFaces"]))
                    {
                        yield return Record(
                            ("name", Text(celebrity["Name"])), ("id", Text(celebrity["Id"])), ("confidence", Number(celebrity["MatchConfidence"])),
                            ("urls", string.Join(", ", (celebrity["Urls"] as JsonArray ?? new JsonArray()).Select(url => Text(url)).Where(url => url is not null))));
                    }

                    if ((answer["UnrecognizedFaces"] as JsonArray)?.Count is > 0 and var unknown)
                    {
                        yield return Record(("name", null), ("unrecognizedFaces", unknown));
                    }

                    break;
            }
        }

        private static string? Joined(JsonNode? node, string field) =>
            Items(node).Select(item => Text(item[field])).Where(text => text is not null).ToList() is { Count: > 0 } names ? string.Join(", ", names) : null;

        private static bool? Flag(JsonNode? node) => node?["Value"] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static bool Bool(IReadOnlyDictionary<string, string> p, string name, bool fallback) => Values.Get(p, name) switch
        {
            null => fallback,
            var value when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            var value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            var value => throw new InvalidOperationException($"'{value}' is not true or false ({name})."),
        };

        private static IEnumerable<JsonObject> Items(JsonNode? node) => (node as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

        private static long? Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
    }
}
