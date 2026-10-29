#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsSns
{
    /// <summary>
    /// Amazon SNS (n8n's AWS SNS node: a topic made and deleted, a message published): an account's topics over SNS's
    /// query API (<c>https://sns.{region}.amazonaws.com</c>, a form posted, XML answered - read with no DTD), every call signed
    /// with AWS Signature Version 4 by an IAM user's access key and secret, over the host's egress. A test names how many
    /// topics the key sees.
    /// </summary>
    /// <remarks>
    /// <c>publish</c> (a topic's message with a subject and string and number attributes, a FIFO topic's group and deduplication
    /// ids - or an SMS to a phone number); <c>topics</c> (page by page), <c>topic_attributes</c>, <c>create_topic</c> (a display
    /// name, FIFO) and <c>delete_topic</c>; <c>subscriptions</c> (a topic's, page by page), <c>subscribe</c> (a protocol and its
    /// endpoint) and <c>unsubscribe</c>. A topic is its ARN - or its name, found among the topics. Every value held to its shape
    /// before anything is signed or sent. A failure is thrown with its status, AWS's error code and its message.
    /// </remarks>
    public sealed class AwsSnsConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string PublishOperation = "publish";

        public const string TopicsOperation = "topics";

        public const string TopicAttributesOperation = "topic_attributes";

        public const string CreateTopicOperation = "create_topic";

        public const string DeleteTopicOperation = "delete_topic";

        public const string SubscriptionsOperation = "subscriptions";

        public const string SubscribeOperation = "subscribe";

        public const string UnsubscribeOperation = "unsubscribe";

        public const int DefaultMax = 1000;

        private static readonly Regex Region = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex TopicName = new("^[A-Za-z0-9_-]{1,256}(\\.fifo)?$", RegexOptions.CultureInvariant);

        private static readonly Regex TopicArn = new("^arn:aws(-[a-z]+)*:sns:[a-z0-9-]+:[0-9]{12}:[A-Za-z0-9_-]{1,256}(\\.fifo)?$", RegexOptions.CultureInvariant);

        private static readonly Regex SubscriptionArn = new("^arn:aws(-[a-z]+)*:sns:[a-z0-9-]+:[0-9]{12}:[A-Za-z0-9_.-]+:[0-9a-f-]{36}$", RegexOptions.CultureInvariant);

        private static readonly Regex Phone = new("^\\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant);

        private static readonly Regex AttributeName = new("^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant);

        private static readonly string[] Protocols = { "http", "https", "email", "email-json", "sms", "sqs", "application", "lambda", "firehose" };

        private static readonly string[] Known = { PublishOperation, TopicsOperation, TopicAttributesOperation, CreateTopicOperation, DeleteTopicOperation, SubscriptionsOperation, SubscribeOperation, UnsubscribeOperation };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awssns";

        public string DisplayName => "Amazon SNS";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://sns.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/sns/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/sns/latest/api/welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region of the topics - eu-north-1. The address is SNS's in it when left empty."),
        });

        private static readonly ConnectorConfigField TopicField = new("topic", "Topic", ConnectorConfigFieldType.Text, Required: true, Description: "Its ARN - arn:aws:sns:eu-north-1:123456789012:orders - or its name.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PublishOperation, "Publish a message", "A message to a topic's subscribers, or an SMS to a phone number - n8n's AWS SNS Publish.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("topic", "Topic", ConnectorConfigFieldType.Text, Required: false, Description: "Its ARN or its name - or give a phone number instead."),
                    new ConnectorConfigField("phone", "Phone number", ConnectorConfigFieldType.Text, Required: false, Description: "An SMS's number, +46701740605 - instead of a topic."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "An e-mail subscriber's subject - one line, up to 100 characters."),
                    new ConnectorConfigField("message", "Message", ConnectorConfigFieldType.Multiline, Required: true, Description: "Up to 256 KB."),
                    new ConnectorConfigField("attributes", "Attributes", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of names and values - a number a Number, the rest a String: {\"source\": \"shop\", \"items\": 3}."),
                    new ConnectorConfigField("group", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "A FIFO topic's message group id."),
                    new ConnectorConfigField("deduplication", "Deduplication id", ConnectorConfigFieldType.Text, Required: false, Description: "A FIFO topic's deduplication id.")),
            },
            new ConnectorOperation(TopicsOperation, "Get topics", "The topics the key sees.") { Parameters = Schema(Max()) },
            new ConnectorOperation(TopicAttributesOperation, "Get a topic's attributes", "A topic's attributes - its name, its owner, its subscriptions' counts.") { Parameters = Schema(TopicField) },
            new ConnectorOperation(CreateTopicOperation, "Create a topic", "Makes a topic - n8n's AWS SNS Create.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "Letters, digits, - and _; a FIFO topic's ends .fifo."),
                    new ConnectorConfigField("display_name", "Display name", ConnectorConfigFieldType.Text, Required: false, Description: "An SMS's sender name - up to 100 characters."),
                    new ConnectorConfigField("fifo", "FIFO", ConnectorConfigFieldType.Boolean, Required: false, Description: "A FIFO topic - its name then ends .fifo.")),
            },
            new ConnectorOperation(DeleteTopicOperation, "Delete a topic", "Deletes a topic and its subscriptions - n8n's AWS SNS Delete.") { Parameters = Schema(TopicField) },
            new ConnectorOperation(SubscriptionsOperation, "Get subscriptions", "A topic's subscriptions.") { Parameters = Schema(TopicField, Max()) },
            new ConnectorOperation(SubscribeOperation, "Subscribe", "Subscribes an endpoint to a topic.")
            {
                Parameters = Schema(
                    TopicField,
                    new ConnectorConfigField("protocol", "Protocol", ConnectorConfigFieldType.Text, Required: true, Description: "https, http, email, email-json, sms, sqs, application, lambda or firehose."),
                    new ConnectorConfigField("endpoint", "Endpoint", ConnectorConfigFieldType.Text, Required: true, Description: "Its address - a URL, an e-mail address, a phone number or an ARN.")),
            },
            new ConnectorOperation(UnsubscribeOperation, "Unsubscribe", "Deletes a subscription.")
            {
                Parameters = Schema(new ConnectorConfigField("subscription", "Subscription", ConnectorConfigFieldType.Text, Required: true, Description: "Its ARN - from Get subscriptions or Subscribe.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var found = await session.CallAsync("ListTopics", new List<(string, string)>(), cancellationToken);
                var count = Members(found, "Topics").Count();
                var more = Value(found, "NextToken") is { Length: > 0 };
                return ConnectionTestResult.Ok($"SNS in {session.Region}: {(more ? $"{count} or more topics" : count == 1 ? "one topic" : $"{count} topics")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PublishOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Amazon SNS has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var phone = operation == PublishOperation ? Values.Get(p, "phone") : null;
            var topic = operation is TopicsOperation or CreateTopicOperation or UnsubscribeOperation || (operation == PublishOperation && phone is not null && Values.Get(p, "topic") is null) ? null : TopicOf(Values.Get(p, "topic"), operation == PublishOperation ? " - or a phone number" : "");
            var form = new List<(string Name, string Value)>();
            switch (operation)
            {
                case PublishOperation:
                    {
                        if (phone is not null)
                        {
                            if (topic is not null)
                            {
                                throw new InvalidOperationException("Give a topic or a phone number - not both.");
                            }

                            form.Add(("PhoneNumber", Phone.IsMatch(phone) ? phone : throw new InvalidOperationException($"'{phone}' is not a phone number - +46701740605.")));
                        }

                        var message = p.TryGetValue("message", out var given) && !string.IsNullOrWhiteSpace(given) ? given : throw new InvalidOperationException("Give the message.");
                        if (Encoding.UTF8.GetByteCount(message) > 256 * 1024)
                        {
                            throw new InvalidOperationException("The message is larger than 256 KB.");
                        }

                        form.Add(("Message", message));
                        if (Values.Get(p, "subject") is { } subject)
                        {
                            form.Add(("Subject", subject.Length <= 100 && !subject.Any(char.IsControl) ? subject : throw new InvalidOperationException("The subject is one line of up to 100 characters.")));
                        }

                        if (Values.Get(p, "attributes") is { } attributes)
                        {
                            var parsed = ObjectOf(attributes, "attributes are", "{\"source\": \"shop\"}");
                            if (parsed.Count > 10)
                            {
                                throw new InvalidOperationException($"A message carries ten attributes at most - {parsed.Count} given.");
                            }

                            var entry = 0;
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

                                var number = value is JsonValue numeric && (numeric.TryGetValue<long>(out _) || numeric.TryGetValue<double>(out _));
                                entry++;
                                form.Add(($"MessageAttributes.entry.{entry}.Name", name));
                                form.Add(($"MessageAttributes.entry.{entry}.Value.DataType", number ? "Number" : "String"));
                                form.Add(($"MessageAttributes.entry.{entry}.Value.StringValue", number ? value.ToJsonString() : value is JsonValue text && text.TryGetValue<string>(out var s) ? s : value.ToJsonString()));
                            }
                        }

                        foreach (var (name, key) in new[] { ("group", "MessageGroupId"), ("deduplication", "MessageDeduplicationId") })
                        {
                            if (Values.Get(p, name) is { } id)
                            {
                                form.Add((key, id.Length <= 128 && id.All(c => c is >= '!' and <= '~') ? id : throw new InvalidOperationException($"'{id}' is not a {(name == "group" ? "group" : "deduplication")} id - up to 128 letters, digits and punctuation.")));
                            }
                        }

                        if (topic is not null && topic.EndsWith(".fifo", StringComparison.Ordinal) && !form.Any(field => field.Name == "MessageGroupId"))
                        {
                            throw new InvalidOperationException("A FIFO topic's message needs its group.");
                        }

                        break;
                    }

                case CreateTopicOperation:
                    {
                        var name = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the topic's name.");
                        var fifo = Values.Get(p, "fifo") is { } flag ? bool.TryParse(flag, out var on) ? on : throw new InvalidOperationException($"'{flag}' is not true or false (fifo).") : false;
                        if (fifo && !name.EndsWith(".fifo", StringComparison.Ordinal))
                        {
                            name += ".fifo";
                        }

                        form.Add(("Name", TopicName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a topic's name - letters, digits, - and _, up to 256.")));
                        var attribute = 0;
                        if (Values.Get(p, "display_name") is { } display)
                        {
                            attribute++;
                            form.Add(($"Attributes.entry.{attribute}.key", "DisplayName"));
                            form.Add(($"Attributes.entry.{attribute}.value", display.Length <= 100 && !display.Any(char.IsControl) ? display : throw new InvalidOperationException("The display name is one line of up to 100 characters.")));
                        }

                        if (name.EndsWith(".fifo", StringComparison.Ordinal))
                        {
                            attribute++;
                            form.Add(($"Attributes.entry.{attribute}.key", "FifoTopic"));
                            form.Add(($"Attributes.entry.{attribute}.value", "true"));
                        }

                        break;
                    }

                case SubscribeOperation:
                    {
                        var protocol = Values.Get(p, "protocol")?.ToLowerInvariant() is { } named && Protocols.Contains(named) ? named : throw new InvalidOperationException($"Give the protocol - {string.Join(", ", Protocols[..^1])} or {Protocols[^1]}.");
                        var endpoint = Values.Get(p, "endpoint") ?? throw new InvalidOperationException("Give the endpoint - its address.");
                        var fits = protocol switch
                        {
                            "http" or "https" => Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint, out var url) && url.Scheme == protocol,
                            "email" or "email-json" => endpoint.IndexOf('@') > 0 && endpoint.IndexOf('@') == endpoint.LastIndexOf('@') && endpoint.IndexOf('@') < endpoint.Length - 1 && !endpoint.Any(char.IsWhiteSpace),
                            "sms" => Phone.IsMatch(endpoint),
                            _ => endpoint.StartsWith("arn:", StringComparison.Ordinal) && !endpoint.Any(char.IsWhiteSpace),
                        };
                        if (!fits)
                        {
                            throw new InvalidOperationException($"'{endpoint}' is not an endpoint for {protocol}.");
                        }

                        form.Add(("Protocol", protocol));
                        form.Add(("Endpoint", endpoint));
                        form.Add(("ReturnSubscriptionArn", "true"));
                        break;
                    }

                case UnsubscribeOperation:
                    form.Add(("SubscriptionArn", Values.Get(p, "subscription") is { } subscription && SubscriptionArn.IsMatch(subscription) ? subscription : throw new InvalidOperationException("Give the subscription - its ARN, from Get subscriptions or Subscribe.")));
                    break;
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            string? arn = null;
            if (topic is not null)
            {
                arn = TopicArn.IsMatch(topic) ? topic : await FindAsync(session, topic, cancellationToken) ?? throw new InvalidOperationException($"No topic is named {topic}.");
                form.Insert(0, ("TopicArn", arn));
            }

            switch (operation)
            {
                case PublishOperation:
                    {
                        var published = await session.CallAsync("Publish", form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["message_id"] = Value(published, "MessageId"), ["sequence_number"] = Value(published, "SequenceNumber"), ["topic"] = arn, ["phone"] = phone });
                        yield break;
                    }

                case TopicsOperation:
                case SubscriptionsOperation:
                    {
                        var read = 0;
                        string? next = null;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var page = await session.CallAsync(operation == TopicsOperation ? "ListTopics" : "ListSubscriptionsByTopic", next is null ? form : form.Append(("NextToken", next)).ToList(), cancellationToken);
                            foreach (var member in Members(page, operation == TopicsOperation ? "Topics" : "Subscriptions"))
                            {
                                yield return operation == TopicsOperation ? TopicRecord(Value(member, "TopicArn") ?? "") : Record(member);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Value(page, "NextToken") is { Length: > 0 } more ? more : null;
                        }
                        while (next is not null);

                        yield break;
                    }

                case TopicAttributesOperation:
                    {
                        var answer = await session.CallAsync("GetTopicAttributes", form, cancellationToken);
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["topic"] = arn };
                        foreach (var entry in answer.Descendants().Where(element => element.Name.LocalName == "entry"))
                        {
                            var key = Value(entry, "key");
                            var value = Value(entry, "value");
                            if (key is null || key is "Policy" or "EffectiveDeliveryPolicy" or "DeliveryPolicy")
                            {
                                continue; // the policies are JSON documents - not a record's values
                            }

                            fields[Snake(key)] = value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && !key.EndsWith("Arn", StringComparison.Ordinal) ? number : value;
                        }

                        yield return new ConnectorRecord(fields);
                        yield break;
                    }

                case CreateTopicOperation:
                    {
                        var made = Value(await session.CallAsync("CreateTopic", form, cancellationToken), "TopicArn") ?? "";
                        yield return TopicRecord(made);
                        yield break;
                    }

                case SubscribeOperation:
                    {
                        var subscription = Value(await session.CallAsync("Subscribe", form, cancellationToken), "SubscriptionArn");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["subscription_arn"] = subscription, ["topic"] = arn, ["pending"] = subscription == "pending confirmation" });
                        yield break;
                    }

                default:
                    await session.CallAsync(operation == DeleteTopicOperation ? "DeleteTopic" : "Unsubscribe", form, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [operation == DeleteTopicOperation ? "topic" : "subscription"] = arn ?? form[0].Value, ["done"] = true });
                    yield break;
            }
        }

        // A topic's name among the topics the key sees - its ARN, or none.
        private static async Task<string?> FindAsync(Session session, string name, CancellationToken cancellationToken)
        {
            string? next = null;
            do
            {
                var page = await session.CallAsync("ListTopics", next is null ? new List<(string, string)>() : new List<(string, string)> { ("NextToken", next) }, cancellationToken);
                var found = Members(page, "Topics").Select(member => Value(member, "TopicArn")).FirstOrDefault(arn => arn is not null && arn[(arn.LastIndexOf(':') + 1)..] == name);
                if (found is not null)
                {
                    return found;
                }

                next = Value(page, "NextToken") is { Length: > 0 } more ? more : null;
            }
            while (next is not null);

            return null;
        }

        private static ConnectorRecord TopicRecord(string arn) => new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = arn[(arn.LastIndexOf(':') + 1)..], ["arn"] = arn });

        private static ConnectorRecord Record(XElement member) =>
            new(member.Elements().ToDictionary(element => Snake(element.Name.LocalName), element => (object?)element.Value, StringComparer.Ordinal));

        private static IEnumerable<XElement> Members(XElement answer, string list) =>
            answer.Descendants().FirstOrDefault(element => element.Name.LocalName == list)?.Elements().Where(element => element.Name.LocalName == "member") ?? Enumerable.Empty<XElement>();

        private static string? Value(XElement element, string name) => element.Descendants().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

        // A name in snake case: a word's capital after a small letter or a digit, or the last of a run of capitals before a word
        // (AWSTraceHeader aws_trace_header), starts a word.
        private static string Snake(string name) =>
            string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))) ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

        // A topic's ARN as given, or its name - found after signing starts.
        private static string TopicOf(string? given, string orElse)
        {
            var topic = given ?? throw new InvalidOperationException($"Give the topic - its ARN or its name{orElse}.");
            return TopicArn.IsMatch(topic) || TopicName.IsMatch(topic) ? topic : throw new InvalidOperationException($"'{topic}' is not a topic - its ARN, arn:aws:sns:eu-north-1:123456789012:orders, or its name.");
        }

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

        // One connection's calls: SNS's address in the region, its query API's action and version as a form, every request
        // signed; the XML answer read with no DTD; a failure thrown with its status, AWS's error code and its message.
        private sealed class Session
        {
            private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

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
                if (!AwsSnsConnector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not an AWS region - eu-north-1.");
                }

                var api = Values.BaseUrl(endpoint, $"https://sns.{region}.amazonaws.com");
                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Amazon SNS connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Amazon SNS connection has no secret key.");
                return new Session(api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            public async Task<XElement> CallAsync(string action, IReadOnlyList<(string Name, string Value)> form, CancellationToken cancellationToken)
            {
                var body = string.Join("&", new[] { ("Action", action), ("Version", "2010-03-31") }.Concat(form).Select(field => AwsSigV4.Encode(field.Item1) + "=" + AwsSigV4.Encode(field.Item2)));
                var payload = Encoding.UTF8.GetBytes(body);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, api + "/") { Content = new ByteArrayContent(payload) };
                    request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded; charset=utf-8");
                    AwsSigV4.Sign(request, payload, Region, "sns", accessKey, secretKey, null, time.GetUtcNow());
                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                XElement? xml = null;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(answer), Safe);
                    xml = XDocument.Load(reader).Root;
                }
                catch (XmlException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = xml is null ? null : Value(xml, "Code");
                    var said = xml is null ? null : Value(xml, "Message");
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return xml ?? throw new InvalidOperationException($"Amazon SNS answered {action} with no XML.");
            }
        }
    }
}
