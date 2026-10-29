#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AwsSqs;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Amazon SQS (n8n's AWS SQS node: a message sent): every call SQS's JSON protocol - its target and content type -
    /// signed with AWS Signature Version 4 for SQS in the connection's region; a message sent with a delay and attributes, a
    /// FIFO one with its group and deduplication ids, to a queue by its URL or its name (resolved first); messages received ten
    /// a call up to the most asked for, an empty answer the end; a message deleted, a queue purged, its attributes read, the
    /// queues listed by a prefix page by page; what could go wrong refused before anything is signed or sent; AWS's error type
    /// and message; a test naming the queues seen.
    /// </summary>
    public sealed class AwsSqsConnectorTests
    {
        private const string Api = "https://sqs.eu-north-1.example.test";

        private const string Queue = "https://sqs.eu-north-1.amazonaws.com/123456789012/orders";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDEXAMPLE", ["secret-key"] = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" };

        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-north-1" };

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static AwsSqsConnector Connector() => new(new FixedClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero)));

        private sealed class Sqs : HttpMessageHandler
        {
            public List<(string Target, string? Authorization, string? ContentType, string Body, string Url, string? Date)> Requests { get; } = new();

            public Func<string, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var target = request.Headers.TryGetValues("X-Amz-Target", out var targets) ? targets.Single() : "";
                Requests.Add((target, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, request.Content?.Headers.ContentType?.ToString(), body, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("x-amz-date", out var date) ? date.Single() : null));
                var (status, answer) = Answer(target, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/x-amz-json-1.0") };
            }
        }

        private static ConnectorExtractContext Context(Sqs sqs, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(sqs), Api, operation, parameters, settings: Settings, credentials: Credentials);

        [Fact]
        public async Task A_message_is_sent_signed_by_the_json_protocol_to_a_queue_by_url_or_name()
        {
            var sqs = new Sqs { Answer = (target, _) => (HttpStatusCode.OK, target == "AmazonSQS.GetQueueUrl" ? "{\"QueueUrl\":\"" + Queue + ".fifo\"}" : """{"MessageId":"219f8380","MD5OfMessageBody":"fafb00f5","SequenceNumber":"18849"}""") };
            var sent = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sqs, AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "{\"order\": 1042, \"note\": \"Café\"}", ["delay"] = "45", ["attributes"] = "{\"source\": \"shop\", \"items\": 3, \"meta\": {\"a\": 1}}" })));

            var call = Assert.Single(sqs.Requests);
            Assert.Equal(("AmazonSQS.SendMessage", "application/x-amz-json-1.0", $"{Api}/", "20261012T093000Z"), (call.Target, call.ContentType, call.Url, call.Date));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-north-1/sqs/aws4_request, SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature=", call.Authorization, StringComparison.Ordinal);
            Assert.Equal("{\"MessageBody\":\"{\\\"order\\\": 1042, \\\"note\\\": \\\"Café\\\"}\",\"DelaySeconds\":45,\"MessageAttributes\":{\"source\":{\"DataType\":\"String\",\"StringValue\":\"shop\"},\"items\":{\"DataType\":\"Number\",\"StringValue\":\"3\"},\"meta\":{\"DataType\":\"String\",\"StringValue\":\"{\\\"a\\\":1}\"}},\"QueueUrl\":\"" + Queue + "\"}", call.Body); // the body as written; a number a Number
            Assert.Equal(("219f8380", "fafb00f5", "18849", Queue), ((string?)sent.Fields["message_id"], (string?)sent.Fields["md5"], (string?)sent.Fields["sequence_number"], (string?)sent.Fields["queue"]));

            await Contexts.DrainAsync(Connector(), Context(sqs, AwsSqsConnector.SendMessageOperation, new() { ["queue"] = "orders.fifo", ["body"] = "hi", ["group"] = "shop-1", ["deduplication"] = "o-1042" }));
            Assert.Equal(new[] { ("AmazonSQS.GetQueueUrl", "{\"QueueName\":\"orders.fifo\"}"), ("AmazonSQS.SendMessage", "{\"MessageBody\":\"hi\",\"MessageGroupId\":\"shop-1\",\"MessageDeduplicationId\":\"o-1042\",\"QueueUrl\":\"" + Queue + ".fifo\"}") },
                sqs.Requests.Skip(1).Select(request => (request.Target, request.Body))); // a name resolved to its URL first
            await Contexts.DrainAsync(Connector(), Context(sqs, AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = "http://localhost:9324/000000000000/orders" }));
            Assert.Equal("{\"QueueUrl\":\"http://localhost:9324/000000000000/orders\"}", sqs.Requests[^1].Body); // a local queue's http address taken as it is

            var unknown = new Sqs();
            Assert.Equal("No queue is named orders.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(unknown, AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = "orders" })))).Message);
            Assert.Equal("AmazonSQS.GetQueueUrl", Assert.Single(unknown.Requests).Target); // nothing purged
        }

        [Fact]
        public async Task Messages_are_received_deleted_purged_and_queues_listed()
        {
            var batches = 0;
            var sqs = new Sqs
            {
                Answer = (target, body) => target switch
                {
                    "AmazonSQS.ReceiveMessage" => (HttpStatusCode.OK, ++batches > 2 ? "{}" : "{\"Messages\":[" + string.Join(",", Enumerable.Range(1, body.Contains("\"MaxNumberOfMessages\":10", StringComparison.Ordinal) ? 10 : 3).Select(n =>
                        "{\"MessageId\":\"m" + batches + "-" + n + "\",\"ReceiptHandle\":\"r" + n + "\",\"MD5OfBody\":\"x\",\"Body\":\"Café " + n + "\",\"Attributes\":{\"ApproximateReceiveCount\":\"2\",\"SenderId\":\"AIDA\",\"AWSTraceHeader\":\"Root=1-5759e988\"},\"MessageAttributes\":{\"items\":{\"DataType\":\"Number\",\"StringValue\":\"3\"},\"source\":{\"DataType\":\"String\",\"StringValue\":\"shop\"}}}")) + "]}"),
                    "AmazonSQS.ListQueues" => (HttpStatusCode.OK, body.Contains("NextToken", StringComparison.Ordinal) ? "{\"QueueUrls\":[\"" + Queue + "-c\"]}" : "{\"QueueUrls\":[\"" + Queue + "-a\",\"" + Queue + "-b\"],\"NextToken\":\"t+1\"}"),
                    "AmazonSQS.GetQueueAttributes" => (HttpStatusCode.OK, """{"Attributes":{"ApproximateNumberOfMessages":"12","QueueArn":"arn:aws:sqs:eu-north-1:123456789012:orders","VisibilityTimeout":"30"}}"""),
                    _ => (HttpStatusCode.OK, "{}"),
                },
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(sqs, operation, parameters));

            var messages = await Run(AwsSqsConnector.ReceiveMessagesOperation, new() { ["queue"] = Queue, ["wait"] = "20", ["visibility"] = "60", ["max"] = "13" });
            Assert.Equal(13, messages.Count);
            Assert.Equal(new[]
            {
                "{\"WaitTimeSeconds\":20,\"VisibilityTimeout\":60,\"MessageAttributeNames\":[\"All\"],\"MessageSystemAttributeNames\":[\"All\"],\"QueueUrl\":\"" + Queue + "\",\"MaxNumberOfMessages\":10}",
                "{\"WaitTimeSeconds\":20,\"VisibilityTimeout\":60,\"MessageAttributeNames\":[\"All\"],\"MessageSystemAttributeNames\":[\"All\"],\"QueueUrl\":\"" + Queue + "\",\"MaxNumberOfMessages\":3}",
            }, sqs.Requests.Select(request => request.Body)); // ten a call, then no more than what is left
            var first = messages[0].Fields;
            Assert.Equal(("m1-1", "r1", "Café 1", 2L, "AIDA", 3.0, "shop"), ((string?)first["message_id"], (string?)first["receipt"], (string?)first["body"], (long?)first["approximate_receive_count"], (string?)first["sender_id"], (double?)first["attribute.items"], (string?)first["attribute.source"]));
            Assert.Equal("Root=1-5759e988", (string?)first["aws_trace_header"]); // capitals in a row one word
            batches = 2;
            Assert.Empty(await Run(AwsSqsConnector.ReceiveMessagesOperation, new() { ["queue"] = Queue }));
            Assert.Equal("{\"MessageAttributeNames\":[\"All\"],\"MessageSystemAttributeNames\":[\"All\"],\"QueueUrl\":\"" + Queue + "\",\"MaxNumberOfMessages\":10}", sqs.Requests[^1].Body); // an empty answer the end

            var gone = Assert.Single(await Run(AwsSqsConnector.DeleteMessageOperation, new() { ["queue"] = Queue, ["receipt"] = "AQEB+r/1=" }));
            Assert.Equal(("AmazonSQS.DeleteMessage", "{\"ReceiptHandle\":\"AQEB+r/1=\",\"QueueUrl\":\"" + Queue + "\"}"), (sqs.Requests[^1].Target, sqs.Requests[^1].Body));
            Assert.Equal((Queue, true), ((string?)gone.Fields["queue"], (bool?)gone.Fields["done"]));
            await Run(AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = Queue + "/" });
            Assert.Equal(("AmazonSQS.PurgeQueue", "{\"QueueUrl\":\"" + Queue + "\"}"), (sqs.Requests[^1].Target, sqs.Requests[^1].Body));
            var attributes = Assert.Single(await Run(AwsSqsConnector.QueueAttributesOperation, new() { ["queue"] = Queue })).Fields;
            Assert.Equal(("AmazonSQS.GetQueueAttributes", "{\"AttributeNames\":[\"All\"],\"QueueUrl\":\"" + Queue + "\"}"), (sqs.Requests[^1].Target, sqs.Requests[^1].Body)); // every attribute asked for
            Assert.Equal((12L, "arn:aws:sqs:eu-north-1:123456789012:orders", 30L), ((long?)attributes["approximate_number_of_messages"], (string?)attributes["queue_arn"], (long?)attributes["visibility_timeout"]));

            var queues = await Run(AwsSqsConnector.QueuesOperation, new() { ["prefix"] = "orders" });
            Assert.Equal(new[] { "orders-a", "orders-b", "orders-c" }, queues.Select(queue => (string?)queue.Fields["name"]));
            Assert.Equal(new[] { "{\"QueueNamePrefix\":\"orders\",\"MaxResults\":1000}", "{\"QueueNamePrefix\":\"orders\",\"MaxResults\":998,\"NextToken\":\"t+1\"}" }, sqs.Requests.TakeLast(2).Select(request => request.Body));
            var before = sqs.Requests.Count;
            Assert.Equal(2, (await Run(AwsSqsConnector.QueuesOperation, new() { ["max"] = "2" })).Count);
            Assert.Equal(before + 1, sqs.Requests.Count); // no page asked for past the most
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed_or_sent_and_awss_errors_are_its_words()
        {
            var sqs = new Sqs();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(sqs, operation, parameters)))).Message;

            Assert.Equal("Give the queue - its URL or its name.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["body"] = "x" }));
            Assert.Equal("'my queue' is not a queue - its URL or its name, letters, digits, - and _.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = "my queue", ["body"] = "x" }));
            Assert.Equal("'https://sqs.eu-north-1.amazonaws.com/orders' is not a queue's URL - https://sqs.eu-north-1.amazonaws.com/123456789012/orders.", await Refused(AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = "https://sqs.eu-north-1.amazonaws.com/orders" }));
            Assert.Equal("Give the message's body.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue }));
            Assert.Equal("Give the message's body.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "" }));
            Assert.Equal("'ftp://sqs.example.com/123456789012/orders' is not a queue - its URL or its name, letters, digits, - and _.", await Refused(AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = "ftp://sqs.example.com/123456789012/orders" }));
            Assert.Equal("'https://sqs.eu-north-1.amazonaws.com/123456789012/my queue' is not a queue's URL - https://sqs.eu-north-1.amazonaws.com/123456789012/orders.", await Refused(AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = "https://sqs.eu-north-1.amazonaws.com/123456789012/my queue" }));
            Assert.Equal("The body is larger than 1 MiB.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = new string('b', 1024 * 1024 + 1) }));
            Assert.Equal("'901' is not 0 to 900 seconds (delay).", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["delay"] = "901" }));
            Assert.Equal("The attributes are not a JSON object - {\"source\": \"shop\"}.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "source=shop" }));
            Assert.Equal("'my attr' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{\"my attr\": \"x\"}" }));
            Assert.Equal("'Amazon.trace' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{\"Amazon.trace\": \"x\"}" }));
            Assert.Equal("The attribute source has no value.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{\"source\": null}" }));
            Assert.Equal("'AWS.trace' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{\"AWS.trace\": \"x\"}" }));
            Assert.Equal("The attribute source has no value.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{\"source\": \"\"}" }));
            Assert.Equal("A message carries ten attributes at most - 11 given.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue, ["body"] = "x", ["attributes"] = "{" + string.Join(",", Enumerable.Range(1, 11).Select(i => $"\"a{i}\": 1")) + "}" }));
            Assert.Equal("'a b' is not a group id - up to 128 letters, digits and punctuation.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue + ".fifo", ["body"] = "x", ["group"] = "a b" }));
            Assert.Equal("'" + new string('d', 129) + "' is not a deduplication id - up to 128 letters, digits and punctuation.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue + ".fifo", ["body"] = "x", ["group"] = "g", ["deduplication"] = new string('d', 129) }));
            Assert.Equal("A FIFO queue's message needs its group.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue + ".fifo", ["body"] = "x" }));
            Assert.Equal("A FIFO queue takes no delay per message.", await Refused(AwsSqsConnector.SendMessageOperation, new() { ["queue"] = Queue + ".fifo", ["body"] = "x", ["group"] = "g", ["delay"] = "5" }));
            Assert.Equal("'21' is not 0 to 20 seconds (wait).", await Refused(AwsSqsConnector.ReceiveMessagesOperation, new() { ["queue"] = Queue, ["wait"] = "21" }));
            Assert.Equal("'43201' is not 0 to 43200 seconds (hidden for).", await Refused(AwsSqsConnector.ReceiveMessagesOperation, new() { ["queue"] = Queue, ["visibility"] = "43201" }));
            Assert.Equal("Give the message's receipt handle - from Receive messages.", await Refused(AwsSqsConnector.DeleteMessageOperation, new() { ["queue"] = Queue, ["receipt"] = "AQEB r1" }));
            Assert.Equal("Give the message's receipt handle - from Receive messages.", await Refused(AwsSqsConnector.DeleteMessageOperation, new() { ["queue"] = Queue }));
            Assert.Equal("'or ders' is not the start of a queue's name - letters, digits, - and _.", await Refused(AwsSqsConnector.QueuesOperation, new() { ["prefix"] = "or ders" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(AwsSqsConnector.ReceiveMessagesOperation, new() { ["queue"] = Queue, ["max"] = "0" }));
            Assert.StartsWith("Amazon SQS has no operation 'create_queue' - send_message, receive_messages,", await Refused("create_queue", new()));
            Assert.Empty(sqs.Requests); // nothing signed or sent

            var noRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(sqs), Api, AwsSqsConnector.QueuesOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string> { ["region"] = "eu-north" }, credentials: Credentials)));
            Assert.Equal("'eu-north' is not an AWS region - eu-north-1.", noRegion.Message);
            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(sqs), Api, AwsSqsConnector.QueuesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["access-key"] = "AKIDEXAMPLE" })));
            Assert.Equal("The Amazon SQS connection has no secret key.", keyless.Message);
            var nameless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(sqs), Api, AwsSqsConnector.QueuesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["secret-key"] = "s" })));
            Assert.Equal("The Amazon SQS connection has no access key.", nameless.Message);
            Assert.Empty(sqs.Requests);

            var missing = new Sqs { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"__type":"com.amazonaws.sqs#QueueDoesNotExist","message":"The specified queue does not exist."}""") };
            Assert.Equal("HTTP 400 QueueDoesNotExist: The specified queue does not exist.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, AwsSqsConnector.PurgeQueueOperation, new() { ["queue"] = Queue })))).Message);
            var silent = new Sqs { Answer = (_, _) => (HttpStatusCode.Forbidden, "") };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, AwsSqsConnector.QueuesOperation, new())))).Message);
            var wordy = new Sqs { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"__type\":\"com.amazonaws.sqs#InvalidParameterValue\",\"Message\":\"" + new string('x', 400) + "\"}") };
            Assert.Equal("HTTP 400 InvalidParameterValue: " + new string('x', 300), (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(wordy, AwsSqsConnector.QueuesOperation, new())))).Message); // a long message cut
        }

        [Fact]
        public async Task A_test_names_the_queues_seen()
        {
            var sqs = new Sqs { Answer = (_, _) => (HttpStatusCode.OK, "{\"QueueUrls\":[\"" + Queue + "\",\"" + Queue + "-b\"]}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(sqs), "", settings: Settings, credentials: Credentials));
            Assert.Equal((true, "SQS in eu-north-1: 2 queues seen."), (tested.Success, tested.Message));
            Assert.Equal(("AmazonSQS.ListQueues", "https://sqs.eu-north-1.amazonaws.com/", "{\"MaxResults\":1000}"), (sqs.Requests[0].Target, sqs.Requests[0].Url, sqs.Requests[0].Body)); // the region's own address when none is given
            var one = new Sqs { Answer = (_, _) => (HttpStatusCode.OK, "{\"QueueUrls\":[\"" + Queue + "\"]}") };
            Assert.Equal("SQS in eu-north-1: one queue seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", settings: Settings, credentials: Credentials))).Message);
            var many = new Sqs { Answer = (_, _) => (HttpStatusCode.OK, "{\"QueueUrls\":[\"" + Queue + "\"],\"NextToken\":\"t\"}") };
            Assert.Equal("SQS in eu-north-1: 1 or more queues seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(many), "", settings: Settings, credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "awssqs"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(6, Connector().Operations.Count);
        }
    }
}
