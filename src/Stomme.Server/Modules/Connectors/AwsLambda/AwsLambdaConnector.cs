#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.AwsLambda
{
    /// <summary>
    /// AWS Lambda (n8n's AWS Lambda node: a function invoked): an account's functions over Lambda's REST API
    /// (<c>https://lambda.{region}.amazonaws.com</c>), every call signed with AWS Signature Version 4 by an IAM user's access key
    /// and secret, over the host's egress. A test names how many functions the key sees.
    /// </summary>
    /// <remarks>
    /// <c>invoke</c> (a function by its name or ARN, a version or alias, a JSON payload; waited for - its answer a record, an
    /// object's fields or a value as <c>result</c>, a function that failed thrown with its error - or queued as an event);
    /// <c>functions</c> (page by page by Lambda's marker) and <c>function</c> (one's configuration). Every value held to its shape
    /// before anything is signed or sent. A throttled call waits as AWS says; a failure is thrown with its status, AWS's error
    /// type and its message.
    /// </remarks>
    public sealed class AwsLambdaConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string InvokeOperation = "invoke";

        public const string FunctionsOperation = "functions";

        public const string FunctionOperation = "function";

        public const int DefaultMax = 1000;

        private const int PageSize = 50;

        private static readonly Regex Region = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        // A function's name, its partial ARN or its ARN - a version or an alias after a colon allowed - as Lambda documents it.
        private static readonly Regex FunctionName = new("^(arn:aws[a-z-]*:lambda:[a-z0-9-]+:)?([0-9]{12}:)?(function:)?[A-Za-z0-9_-]{1,64}(:(\\$LATEST|[A-Za-z0-9_-]{1,128}))?$", RegexOptions.CultureInvariant);

        private static readonly Regex Qualifier = new("^(\\$LATEST|[A-Za-z0-9_-]{1,128})$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { InvokeOperation, FunctionsOperation, FunctionOperation };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awslambda";

        public string DisplayName => "AWS Lambda";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://lambda.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/lambda/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/lambda/latest/api/welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region of the functions - eu-north-1. The address is Lambda's in it when left empty."),
        });

        private static readonly ConnectorConfigField FunctionField = new("function", "Function", ConnectorConfigFieldType.Text, Required: true, Description: "Its name or ARN - resize-images, or arn:aws:lambda:eu-north-1:123456789012:function:resize-images.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(InvokeOperation, "Invoke a function", "Runs a function with a JSON payload - waits for its answer, or queues it as an event; n8n's AWS Lambda Invoke.")
            {
                Parameters = Schema(
                    FunctionField,
                    new ConnectorConfigField("qualifier", "Version or alias", ConnectorConfigFieldType.Text, Required: false, Description: "$LATEST when left empty - or 3, or live."),
                    new ConnectorConfigField("payload", "Payload", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"image\": \"photo.jpg\"}; none sends {}."),
                    new ConnectorConfigField("event", "As an event", ConnectorConfigFieldType.Boolean, Required: false, Description: "Queue it and go on - its answer not waited for.")),
            },
            new ConnectorOperation(FunctionsOperation, "Get functions", "The functions the key sees - each its runtime, handler, memory, timeout and when it last changed.")
            {
                Parameters = Schema(new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(FunctionOperation, "Get a function", "One function's configuration.")
            {
                Parameters = Schema(FunctionField, new ConnectorConfigField("qualifier", "Version or alias", ConnectorConfigFieldType.Text, Required: false, Description: "$LATEST when left empty.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var (_, found, _) = await session.SendAsync(HttpMethod.Get, "/2015-03-31/functions/?MaxItems=50", null, null, cancellationToken);
                var count = (found?["Functions"] as JsonArray ?? new JsonArray()).Count;
                var more = Text(found?["NextMarker"]) is { Length: > 0 };
                return ConnectionTestResult.Ok($"Lambda in {session.Region}: {(more ? $"{count} or more functions" : count == 1 ? "one function" : $"{count} functions")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? InvokeOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"AWS Lambda has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? function = null;
            var query = "";
            if (operation != FunctionsOperation)
            {
                function = Values.Get(p, "function") is { } named ? FunctionName.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a function - its name, letters, digits, - and _, or its ARN.") : throw new InvalidOperationException("Give the function - its name or ARN.");
                if (Values.Get(p, "qualifier") is { } qualifier)
                {
                    query = "?Qualifier=" + AwsSigV4.Encode(Qualifier.IsMatch(qualifier) ? qualifier : throw new InvalidOperationException($"'{qualifier}' is not a version or an alias - $LATEST, 3 or live."));
                }
            }

            var asEvent = Values.Get(p, "event") is { } flag ? bool.TryParse(flag, out var on) ? on : throw new InvalidOperationException($"'{flag}' is not true or false (event).") : false;
            byte[]? payload = null;
            if (operation == InvokeOperation)
            {
                var given = Values.Get(p, "payload") ?? "{}";
                try
                {
                    using var parsed = JsonDocument.Parse(given);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The payload is not JSON - {\"image\": \"photo.jpg\"}.");
                }

                payload = Encoding.UTF8.GetBytes(given);
                var largest = asEvent ? 256 * 1024 : 6 * 1024 * 1024;
                if (payload.Length > largest)
                {
                    throw new InvalidOperationException($"The payload is larger than {(asEvent ? "256 KB, an event's most" : "6 MB")}.");
                }
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            var path = function is null ? null : "/2015-03-31/functions/" + Uri.EscapeDataString(function);
            switch (operation)
            {
                case InvokeOperation:
                    {
                        var (_, answer, headers) = await session.SendAsync(HttpMethod.Post, path + "/invocations" + query, payload, new Dictionary<string, string> { ["X-Amz-Invocation-Type"] = asEvent ? "Event" : "RequestResponse" }, cancellationToken);
                        if (headers.TryGetValue("X-Amz-Function-Error", out var failure))
                        {
                            var said = answer is JsonObject error ? Text(error["errorMessage"]) : null;
                            throw new InvalidOperationException($"The function failed ({failure}){(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                        }

                        if (asEvent)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["function"] = function, ["queued"] = true });
                            yield break;
                        }

                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                        if (answer is JsonObject result)
                        {
                            foreach (var (name, value) in result)
                            {
                                fields[name] = Plain(value);
                            }
                        }
                        else
                        {
                            fields["result"] = Plain(answer);
                        }

                        yield return new ConnectorRecord(fields);
                        yield break;
                    }

                case FunctionsOperation:
                    {
                        var read = 0;
                        string? marker = null;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var (_, page, _) = await session.SendAsync(HttpMethod.Get, $"/2015-03-31/functions/?MaxItems={Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)}{(marker is null ? "" : "&Marker=" + AwsSigV4.Encode(marker))}", null, null, cancellationToken);
                            foreach (var configuration in (page?["Functions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Configuration(configuration);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            marker = Text(page?["NextMarker"]) is { Length: > 0 } next ? next : null;
                        }
                        while (marker is not null);

                        yield break;
                    }

                default:
                    {
                        var (_, answer, _) = await session.SendAsync(HttpMethod.Get, path + query, null, null, cancellationToken);
                        yield return Configuration(answer?["Configuration"] as JsonObject ?? new JsonObject());
                        yield break;
                    }
            }
        }

        // A function's configuration: its name, ARN, runtime, handler, memory, timeout, size, state and when it last changed.
        private static ConnectorRecord Configuration(JsonObject configuration) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = Text(configuration["FunctionName"]),
            ["arn"] = Text(configuration["FunctionArn"]),
            ["runtime"] = Text(configuration["Runtime"]),
            ["handler"] = Text(configuration["Handler"]),
            ["description"] = Text(configuration["Description"]),
            ["memory_mb"] = Whole(configuration["MemorySize"]),
            ["timeout_seconds"] = Whole(configuration["Timeout"]),
            ["code_size"] = Whole(configuration["CodeSize"]),
            ["version"] = Text(configuration["Version"]),
            ["state"] = Text(configuration["State"]),
            ["package_type"] = Text(configuration["PackageType"]),
            ["last_modified"] = Utc(Text(configuration["LastModified"])),
        });

        // Lambda's time - 2026-10-01T12:00:00.000+0000 - in UTC.
        private static string? Utc(string? given) =>
            given is not null && DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;

        // A value of a function's answer: a string, a whole number, a number, a flag or null as itself; an object or a list as its JSON.
        private static object? Plain(JsonNode? value) => value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            _ => value.ToJsonString(Wire),
        };

        private static long? Whole(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Lambda's address in the region, every request signed; a throttled call waiting as AWS says; a
        // failure thrown with its status, AWS's error type and its message.
        private sealed class Session
        {
            private readonly string api;
            private readonly string accessKey;
            private readonly string secretKey;
            private readonly TimeProvider time;

            private Session(string api, string region, string accessKey, string secretKey, TimeProvider time, HttpClient client)
            {
                this.api = api;
                Region = region;
                this.accessKey = accessKey;
                this.secretKey = secretKey;
                this.time = time;
                Client = client;
            }

            public string Region { get; }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
            {
                var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the AWS region - eu-north-1.");
                if (!AwsLambdaConnector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not an AWS region - eu-north-1.");
                }

                var api = Values.BaseUrl(endpoint, $"https://lambda.{region}.amazonaws.com");
                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The AWS Lambda connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The AWS Lambda connection has no secret key.");
                return new Session(api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            public async Task<(HttpStatusCode Status, JsonNode? Answer, IReadOnlyDictionary<string, string> Headers)> SendAsync(HttpMethod method, string path, byte[]? body, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
            {
                var payload = body ?? Array.Empty<byte>();
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    if (body is not null)
                    {
                        request.Content = new ByteArrayContent(payload);
                        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    }

                    foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    AwsSigV4.Sign(request, payload, Region, "lambda", accessKey, secretKey, null, time.GetUtcNow());
                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    json = JsonValue.Create(answer); // a function may answer with what is not JSON - kept as its text
                }

                if (!response.IsSuccessStatusCode)
                {
                    var type = response.Headers.TryGetValues("x-amzn-ErrorType", out var types) ? types.First().Split(':')[0] : Text(json?["Type"]);
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["Message"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers)
                {
                    seen[header.Key] = string.Join(",", header.Value);
                }

                return (response.StatusCode, json, seen);
            }
        }
    }
}
