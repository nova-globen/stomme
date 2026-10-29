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

namespace Stomme.Modules.Connectors.AwsSqs
{
    /// <summary>
    /// Amazon SQS (n8n's AWS SQS node: a message sent): an account's queues over SQS's JSON protocol
    /// (<c>https://sqs.{region}.amazonaws.com</c>, <c>X-Amz-Target: AmazonSQS.*</c>), every call signed with AWS Signature Version
    /// 4 by an IAM user's access key and secret, over the host's egress. A test names how many queues the key sees.
    /// </summary>
    /// <remarks>
    /// <c>send_message</c> (a body, a delay, string and number attributes, a FIFO queue's group and deduplication ids),
    /// <c>receive_messages</c> (up to ten a call, a long poll, a visibility timeout - each its body, attributes and receipt
    /// handle - up to <c>max</c>), <c>delete_message</c> (by its receipt handle), <c>queues</c> (by a name prefix, page by page),
    /// <c>queue_attributes</c> and <c>purge_queue</c>. A queue is its URL - or its name, resolved through GetQueueUrl. Every
    /// value held to its shape before anything is signed or sent. A failure is thrown with its status, AWS's error type and its
    /// message.
    /// </remarks>
    public sealed class AwsSqsConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string SendMessageOperation = "send_message";

        public const string ReceiveMessagesOperation = "receive_messages";

        public const string DeleteMessageOperation = "delete_message";

        public const string QueuesOperation = "queues";

        public const string QueueAttributesOperation = "queue_attributes";

        public const string PurgeQueueOperation = "purge_queue";

        public const int DefaultMax = 10;

        private static readonly Regex Region = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex QueueName = new("^[A-Za-z0-9_-]{1,80}(\\.fifo)?$", RegexOptions.CultureInvariant);

        private static readonly Regex AttributeName = new("^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SendMessageOperation, ReceiveMessagesOperation, DeleteMessageOperation, QueuesOperation, QueueAttributesOperation, PurgeQueueOperation };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awssqs";

        public string DisplayName => "Amazon SQS";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://sqs.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/sqs/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region of the queues - eu-north-1. The address is SQS's in it when left empty."),
        });

        private static readonly ConnectorConfigField QueueField = new("queue", "Queue", ConnectorConfigFieldType.Text, Required: true, Description: "Its URL - https://sqs.eu-north-1.amazonaws.com/123456789012/orders - or its name.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendMessageOperation, "Send a message", "A message to a queue - n8n's AWS SQS Send Message.")
            {
                Parameters = Schema(
                    QueueField,
                    new ConnectorConfigField("body", "Body", ConnectorConfigFieldType.Multiline, Required: true, Description: "Text, JSON or XML - up to 1 MiB."),
                    new ConnectorConfigField("delay", "Delay", ConnectorConfigFieldType.Integer, Required: false, Description: "Seconds before it can be received - 0 to 900; not on a FIFO queue."),
                    new ConnectorConfigField("attributes", "Attributes", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of names and values - a number a Number, the rest a String: {\"source\": \"shop\", \"items\": 3}."),
                    new ConnectorConfigField("group", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "A FIFO queue's message group id."),
                    new ConnectorConfigField("deduplication", "Deduplication id", ConnectorConfigFieldType.Text, Required: false, Description: "A FIFO queue's deduplication id.")),
            },
            new ConnectorOperation(ReceiveMessagesOperation, "Receive messages", "Messages from a queue - each its body, attributes and receipt handle.")
            {
                Parameters = Schema(
                    QueueField,
                    new ConnectorConfigField("wait", "Wait", ConnectorConfigFieldType.Integer, Required: false, Description: "Seconds to wait for a message - 0 to 20."),
                    new ConnectorConfigField("visibility", "Hidden for", ConnectorConfigFieldType.Integer, Required: false, Description: "Seconds the messages stay hidden from others - 0 to 43200."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most - ten a call.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(DeleteMessageOperation, "Delete a message", "Deletes a received message by its receipt handle.")
            {
                Parameters = Schema(QueueField, new ConnectorConfigField("receipt", "Receipt handle", ConnectorConfigFieldType.Text, Required: true, Description: "From Receive messages.")),
            },
            new ConnectorOperation(QueuesOperation, "Get queues", "The queues the key sees, by a name prefix.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("prefix", "Name prefix", ConnectorConfigFieldType.Text, Required: false, Description: "Only queues whose names start so."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = "1000" }),
            },
            new ConnectorOperation(QueueAttributesOperation, "Get a queue's attributes", "A queue's attributes - its counts, its timeouts, its ARN.") { Parameters = Schema(QueueField) },
            new ConnectorOperation(PurgeQueueOperation, "Purge a queue", "Deletes every message in a queue.") { Parameters = Schema(QueueField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var found = await session.CallAsync("ListQueues", new JsonObject { ["MaxResults"] = 1000 }, cancellationToken) as JsonObject ?? new JsonObject();
                var count = (found["QueueUrls"] as JsonArray ?? new JsonArray()).Count;
                var more = Text(found["NextToken"]) is not null;
                return ConnectionTestResult.Ok($"SQS in {session.Region}: {(more ? $"{count} or more queues" : count == 1 ? "one queue" : $"{count} queues")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendMessageOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Amazon SQS has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : operation == QueuesOperation ? 1000 : DefaultMax;
            var queue = operation == QueuesOperation ? null : QueueOf(Values.Get(p, "queue"));
            var request = new JsonObject();
            switch (operation)
            {
                case SendMessageOperation:
                    {
                        var body = p.TryGetValue("body", out var given) && given.Length > 0 ? given : throw new InvalidOperationException("Give the message's body.");
                        if (Encoding.UTF8.GetByteCount(body) > 1024 * 1024)
                        {
                            throw new InvalidOperationException("The body is larger than 1 MiB.");
                        }

                        request["MessageBody"] = body;
                        if (Values.Get(p, "delay") is { } delay)
                        {
                            request["DelaySeconds"] = Bounded(delay, "delay", 0, 900);
                        }

                        if (Values.Get(p, "attributes") is { } attributes)
                        {
                            var parsed = ObjectOf(attributes, "attributes are", "{\"source\": \"shop\"}");
                            if (parsed.Count > 10)
                            {
                                throw new InvalidOperationException($"A message carries ten attributes at most - {parsed.Count} given.");
                            }

                            var sent = new JsonObject();
                            foreach (var (name, value) in parsed)
                            {
                                if (!AttributeName.IsMatch(name) || name.StartsWith("AWS.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Amazon.", StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException($"'{name}' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.");
                                }

                                if (value is null || (value is JsonValue blank && blank.TryGetValue<string>(out var empty) && empty.Length == 0))
                                {
                                    throw new InvalidOperationException($"The attribute {name} has no value.");
                                }

                                sent[name] = value is JsonValue number && (number.TryGetValue<long>(out _) || number.TryGetValue<double>(out _))
                                    ? new JsonObject { ["DataType"] = "Number", ["StringValue"] = number.ToJsonString() }
                                    : new JsonObject { ["DataType"] = "String", ["StringValue"] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : value.ToJsonString(Wire) };
                            }

                            request["MessageAttributes"] = sent;
                        }

                        foreach (var (name, key) in new[] { ("group", "MessageGroupId"), ("deduplication", "MessageDeduplicationId") })
                        {
                            if (Values.Get(p, name) is { } id)
                            {
                                request[key] = id.Length <= 128 && id.All(c => c is >= '!' and <= '~') ? id : throw new InvalidOperationException($"'{id}' is not a {(name == "group" ? "group" : "deduplication")} id - up to 128 letters, digits and punctuation.");
                            }
                        }

                        if (queue!.EndsWith(".fifo", StringComparison.Ordinal))
                        {
                            if (request["MessageGroupId"] is null)
                            {
                                throw new InvalidOperationException("A FIFO queue's message needs its group.");
                            }

                            if (request["DelaySeconds"] is not null)
                            {
                                throw new InvalidOperationException("A FIFO queue takes no delay per message.");
                            }
                        }

                        break;
                    }

                case ReceiveMessagesOperation:
                    if (Values.Get(p, "wait") is { } wait)
                    {
                        request["WaitTimeSeconds"] = Bounded(wait, "wait", 0, 20);
                    }

                    if (Values.Get(p, "visibility") is { } visibility)
                    {
                        request["VisibilityTimeout"] = Bounded(visibility, "hidden for", 0, 43200);
                    }

                    request["MessageAttributeNames"] = new JsonArray("All");
                    request["MessageSystemAttributeNames"] = new JsonArray("All");
                    break;
                case DeleteMessageOperation:
                    request["ReceiptHandle"] = Values.Get(p, "receipt") is { } receipt && receipt.Length <= 1024 && !receipt.Any(char.IsWhiteSpace) ? receipt : throw new InvalidOperationException("Give the message's receipt handle - from Receive messages.");
                    break;
                case QueuesOperation:
                    if (Values.Get(p, "prefix") is { } prefix)
                    {
                        request["QueueNamePrefix"] = QueueName.IsMatch(prefix) ? prefix : throw new InvalidOperationException($"'{prefix}' is not the start of a queue's name - letters, digits, - and _.");
                    }

                    break;
                case QueueAttributesOperation:
                    request["AttributeNames"] = new JsonArray("All");
                    break;
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            if (queue is not null)
            {
                request["QueueUrl"] = queue.StartsWith("https://", StringComparison.Ordinal) || queue.StartsWith("http://", StringComparison.Ordinal)
                    ? queue
                    : Text((await session.CallAsync("GetQueueUrl", new JsonObject { ["QueueName"] = queue }, cancellationToken))?["QueueUrl"]) ?? throw new InvalidOperationException($"No queue is named {queue}.");
            }

            switch (operation)
            {
                case SendMessageOperation:
                    {
                        var sent = await session.CallAsync("SendMessage", request, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["message_id"] = Text(sent["MessageId"]),
                            ["md5"] = Text(sent["MD5OfMessageBody"]),
                            ["sequence_number"] = Text(sent["SequenceNumber"]),
                            ["queue"] = Text(request["QueueUrl"]),
                        });
                        yield break;
                    }

                case ReceiveMessagesOperation:
                    {
                        var read = 0;
                        while (read < max)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            request["MaxNumberOfMessages"] = Math.Min(10, max - read);
                            var messages = ((await session.CallAsync("ReceiveMessage", request, cancellationToken))?["Messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var message in messages)
                            {
                                yield return MessageRecord(message);
                                read++;
                            }

                            if (messages.Count == 0)
                            {
                                yield break;
                            }
                        }

                        yield break;
                    }

                case QueuesOperation:
                    {
                        var read = 0;
                        string? next = null;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            request["MaxResults"] = Math.Min(1000, max - read);
                            if (next is not null)
                            {
                                request["NextToken"] = next;
                            }

                            var page = await session.CallAsync("ListQueues", request, cancellationToken);
                            foreach (var url in (page?["QueueUrls"] as JsonArray ?? new JsonArray()).Select(Text).Where(url => url is not null))
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = url![(url.LastIndexOf('/') + 1)..], ["url"] = url });
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["NextToken"]) is { Length: > 0 } more ? more : null;
                        }
                        while (next is not null);

                        yield break;
                    }

                case QueueAttributesOperation:
                    {
                        var attributes = (await session.CallAsync("GetQueueAttributes", request, cancellationToken))?["Attributes"] as JsonObject ?? new JsonObject();
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["queue"] = Text(request["QueueUrl"]) };
                        foreach (var (name, value) in attributes)
                        {
                            var text = Text(value);
                            fields[Snake(name)] = text is not null && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : text;
                        }

                        yield return new ConnectorRecord(fields);
                        yield break;
                    }

                default:
                    await session.CallAsync(operation == DeleteMessageOperation ? "DeleteMessage" : "PurgeQueue", request, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["queue"] = Text(request["QueueUrl"]), ["done"] = true });
                    yield break;
            }
        }

        private static ConnectorRecord MessageRecord(JsonObject message)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message_id"] = Text(message["MessageId"]),
                ["receipt"] = Text(message["ReceiptHandle"]),
                ["body"] = Text(message["Body"]),
                ["md5"] = Text(message["MD5OfBody"]),
            };
            foreach (var (name, value) in message["Attributes"] as JsonObject ?? new JsonObject())
            {
                fields[Snake(name)] = Text(value) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : Text(value);
            }

            foreach (var (name, value) in message["MessageAttributes"] as JsonObject ?? new JsonObject())
            {
                var text = Text(value?["StringValue"]);
                fields["attribute." + name] = Text(value?["DataType"])?.StartsWith("Number", StringComparison.Ordinal) == true && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : text;
            }

            return new ConnectorRecord(fields);
        }

        // A name in snake case: a word's capital after a small letter or a digit, or the last of a run of capitals before a word
        // (AWSTraceHeader aws_trace_header), starts a word.
        private static string Snake(string name) =>
            string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))) ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

        // A queue's URL as given, or its name - resolved after signing starts.
        private static string QueueOf(string? given)
        {
            var queue = given ?? throw new InvalidOperationException("Give the queue - its URL or its name.");
            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(queue, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            {
                var path = url.AbsolutePath.TrimEnd('/');
                return path.Count(c => c == '/') == 2 && QueueName.IsMatch(path[(path.LastIndexOf('/') + 1)..]) ? queue.TrimEnd('/') : throw new InvalidOperationException($"'{queue}' is not a queue's URL - https://sqs.eu-north-1.amazonaws.com/123456789012/orders.");
            }

            return QueueName.IsMatch(queue) ? queue : throw new InvalidOperationException($"'{queue}' is not a queue - its URL or its name, letters, digits, - and _.");
        }

        private static int Bounded(string given, string what, int least, int most) =>
            int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most ? n : throw new InvalidOperationException($"'{given}' is not {least} to {most} seconds ({what}).");

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} not a JSON object - {example}.");
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: SQS's address in the region, its JSON protocol's target and content type, every request signed;
        // a failure thrown with its status, AWS's error type and its message.
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
                if (!AwsSqsConnector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not an AWS region - eu-north-1.");
                }

                var api = Values.BaseUrl(endpoint, $"https://sqs.{region}.amazonaws.com");
                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Amazon SQS connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Amazon SQS connection has no secret key.");
                return new Session(api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> CallAsync(string action, JsonObject body, CancellationToken cancellationToken)
            {
                var payload = Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, api + "/") { Content = new ByteArrayContent(payload) };
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.0");
                    request.Headers.TryAddWithoutValidation("X-Amz-Target", "AmazonSQS." + action);
                    AwsSigV4.Sign(request, payload, Region, "sqs", accessKey, secretKey, null, time.GetUtcNow());
                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Amazon SQS answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var type = Text(json?["__type"]) is { } named ? named[(named.LastIndexOf('#') + 1)..] : null;
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["Message"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
