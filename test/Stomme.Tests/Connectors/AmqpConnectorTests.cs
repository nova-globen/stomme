#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Amqp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - AMQP 1.0 (n8n's AMQP Sender and AMQP Trigger) against an in-process AMQP 1.0 broker (AMQPNetLite's own listener
    /// on loopback): every address, header, property and number held to its shape before anything is dialled; the endpoint ONE
    /// broker with no user or queue in it; the address the host pinned what is dialled, SASL PLAIN with the connection's user;
    /// a message sent as n8n's AMQP string or as bytes, its properties and application properties as given, and the broker's
    /// outcome AWAITED - accepted a record, rejected and released each a failure in the broker's words (n8n's sender says
    /// "sent" for both); messages received up to max, accepted or released back, each a record; a test naming the broker.
    /// </summary>
    public sealed class AmqpConnectorTests : IDisposable
    {
        private const string User = "kafe";
        private const string Password = "pw-amqp-1";

        private readonly int port;
        private readonly ContainerHost host;
        private readonly ConcurrentQueue<(string To, Message Message)> taken = new();
        private readonly ConcurrentQueue<Message> waiting = new();
        private readonly ConcurrentQueue<string> settled = new();
        private readonly CancellationTokenSource closing = new();

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = User, ["password"] = Password };

        // A broker that keeps what reaches 'orders', rejects what goes to 'missing', releases what goes to 'nowhere', and
        // gives 'inbox' what the test put in it - each disposition noted.
        public AmqpConnectorTests()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }

            host = new ContainerHost(new Uri($"amqp://127.0.0.1:{port}"));
            host.Listeners[0].SASL.EnablePlainMechanism(User, Password);
            host.RegisterMessageProcessor("orders", new Processor(context => { taken.Enqueue(("orders", context.Message)); context.Complete(); }));
            host.RegisterMessageProcessor("missing", new Processor(context => context.Complete(new Error(ErrorCode.NotFound) { Description = "no queue 'missing' in vhost '/'" })));
            host.RegisterMessageProcessor("nowhere", new Processor(context => context.Link.DisposeMessage(context.Message, new Released(), true)));
            host.RegisterMessageSource("inbox", new Source(waiting, settled, closing.Token));
            host.Open();
        }

        public void Dispose()
        {
            closing.Cancel(); // the source's wait ends, or the broker never closes
            host.Close();
            closing.Dispose();
        }

        private sealed class Processor(Action<MessageContext> process) : IMessageProcessor
        {
            public int Credit => 10;

            public void Process(MessageContext messageContext) => process(messageContext);
        }

        private sealed class Source(ConcurrentQueue<Message> waiting, ConcurrentQueue<string> settled, CancellationToken closing) : IMessageSource
        {
            public async Task<ReceiveContext> GetMessageAsync(ListenerLink link)
            {
                while (true)
                {
                    if (waiting.TryDequeue(out var message))
                    {
                        return new ReceiveContext(link, message);
                    }

                    await Task.Delay(20, closing);
                }
            }

            public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
            {
                settled.Enqueue(dispositionContext.DeliveryState?.GetType().Name ?? "none");
                dispositionContext.Complete();
            }
        }

        private string Endpoint => $"amqp://broker.kafe.example:{port}";

        private Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters, string? endpoint = null, Dictionary<string, string>? credentials = null, StubEgress? egress = null) =>
            Contexts.DrainAsync(new AmqpConnector(), Contexts.Extract(egress ?? new StubEgress(new StubHandler(HttpStatusCode.OK, "{}")), endpoint ?? Endpoint, operation, parameters, new Dictionary<string, string>(), credentials ?? Credentials, timeoutSeconds: 10));

        [Fact]
        public void It_declares_itself_as_n8ns_amqp_nodes()
        {
            IConnector connector = new AmqpConnector();
            Assert.Equal(("amqp", "AMQP 1.0", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "send", "receive" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string? endpoint = null)
            {
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(operation, parameters, endpoint));
                Assert.Empty(taken);
                return refusal.Message;
            }

            Assert.Equal("AMQP has no operation 'publish' - send or receive.", await Refused("publish", new() { ["to"] = "orders", ["message"] = "x" }));
            Assert.Equal("Give the queue or topic - its address as the broker names it, orders (to).", await Refused("send", new() { ["message"] = "x" }));
            Assert.Equal("Give the queue or topic - its address as the broker names it, orders (from).", await Refused("receive", new() { ["from"] = "in\nbox" }));
            Assert.Equal("Give the message (message).", await Refused("send", new() { ["to"] = "orders" }));
            Assert.Equal("'bytes' is not value or data (body).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["body"] = "bytes" }));
            Assert.Equal("The headers are not JSON - {\"source\": \"kassa\"} (headers).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["headers"] = "source=kassa" }));
            Assert.Equal("The header 'tags' is not text, a number or true or false - AMQP's application properties hold only those (headers).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["headers"] = "{\"tags\": [\"a\"]}" }));
            Assert.Equal("'json' is not a media type - application/json (content_type).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["content_type"] = "json" }));
            Assert.Equal("'300' is not a number from 0 to 255 (priority).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["priority"] = "300" }));
            Assert.Equal("'0' is not a number from 1 to 2147483647 (ttl).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["ttl"] = "0" }));
            Assert.Equal("'kanske' is not true or false (durable).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["durable"] = "kanske" }));
            Assert.Equal("A subject is one line (subject).", await Refused("send", new() { ["to"] = "orders", ["message"] = "x", ["subject"] = "a\nb" }));
            Assert.Equal("'0' is not a number from 1 to 1000 (max).", await Refused("receive", new() { ["from"] = "inbox", ["max"] = "0" }));
            Assert.Equal("'90' is not a number from 0 to 60 (wait).", await Refused("receive", new() { ["from"] = "inbox", ["wait"] = "90" }));
            Assert.Equal("Invalid AMQP address: 'https://broker.kafe.example' is not an AMQP address - amqp://broker.kafe.example:5672, or amqps:// for TLS.", await Refused("send", new() { ["to"] = "orders", ["message"] = "x" }, "https://broker.kafe.example"));
            Assert.Equal("Invalid AMQP address: 'amqp://kafe:pw@broker.kafe.example/orders' names more than a broker - its user and password are the connection's credentials, its queue the step's.", await Refused("send", new() { ["to"] = "orders", ["message"] = "x" }, "amqp://kafe:pw@broker.kafe.example/orders"));
        }

        [Fact]
        public async Task A_message_goes_to_the_pinned_address_with_its_properties_and_its_acceptance_awaited()
        {
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK, "{}"));
            var sent = Assert.Single(await Run("send", new() { ["to"] = "orders", ["query"] = "{\"order\":1}", ["content_type"] = "application/json", ["headers"] = "{\"source\": \"kassa\", \"lines\": 3, \"paid\": true}", ["subject"] = "order.paid", ["message_id"] = "o-1", ["correlation_id"] = "c-1", ["priority"] = "7", ["ttl"] = "60000" }, egress: egress)).Fields;
            Assert.Equal(("orders", true, "o-1"), ((string?)sent["to"], (bool?)sent["accepted"], (string?)sent["message_id"]));
            Assert.Equal(("broker.kafe.example", port), Assert.Single(egress.Pins)); // the name pinned, the loopback address it answered dialled
            var (to, message) = Assert.Single(taken);
            Assert.Equal(("orders", "{\"order\":1}", "application/json", "order.paid", "o-1", "c-1"), (to, (string)message.Body, message.Properties.ContentType.ToString(), message.Properties.Subject, message.Properties.MessageId, message.Properties.CorrelationId));
            Assert.Equal((true, (byte)7, 60000u), (message.Header.Durable, message.Header.Priority, message.Header.Ttl));
            Assert.Equal(("kassa", 3L, true), ((string)message.ApplicationProperties["source"], (long)message.ApplicationProperties["lines"], (bool)message.ApplicationProperties["paid"]));

            await Run("send", new() { ["to"] = "orders", ["message"] = "Hej", ["body"] = "data", ["durable"] = "false" });
            var bytes = taken.Last().Message;
            Assert.Equal(("Hej", false), (System.Text.Encoding.UTF8.GetString(((Data)bytes.BodySection).Binary), bytes.Header.Durable));
        }

        [Fact]
        public async Task A_message_the_broker_rejects_or_releases_is_a_failure_in_its_words()
        {
            Assert.Equal("The AMQP broker rejected the message: no queue 'missing' in vhost '/' (amqp:not-found).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("send", new() { ["to"] = "missing", ["message"] = "x" }))).Message); // n8n's sender: "sent"
            Assert.Equal("The AMQP broker released the message - nothing took it: an exchange's routing key bound to no queue, or a full queue (nowhere).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("send", new() { ["to"] = "nowhere", ["message"] = "x" }))).Message);
            Assert.StartsWith("The AMQP broker refused the sign-in - check the user and password", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("send", new() { ["to"] = "orders", ["message"] = "x" }, credentials: new() { ["username"] = User, ["password"] = "wrong" }))).Message);
        }

        [Fact]
        public async Task Messages_are_received_up_to_max_and_accepted_or_released_back()
        {
            foreach (var order in new[] { "{\"order\":1}", "{\"order\":2}", "{\"order\":3}" })
            {
                waiting.Enqueue(new Message(order) { Properties = new Properties { ContentType = "application/json", MessageId = "m" + order[^2] }, ApplicationProperties = new ApplicationProperties { ["source"] = "kassa" } });
            }

            var got = await Run("receive", new() { ["from"] = "inbox", ["max"] = "2", ["wait"] = "2" });
            Assert.Equal(new[] { "{\"order\":1}", "{\"order\":2}" }, got.Select(record => (string?)record.Fields["body"]));
            Assert.Equal(("application/json", "m1", "{\"source\":\"kassa\"}"), ((string?)got[0].Fields["content_type"], (string?)got[0].Fields["message_id"], (string?)got[0].Fields["properties"]));
            var released = await Run("receive", new() { ["from"] = "inbox", ["wait"] = "2", ["accept"] = "false" });
            Assert.Equal("{\"order\":3}", (string?)Assert.Single(released).Fields["body"]);
            await WaitFor(() => settled.Count == 3);
            Assert.Equal(new[] { "Accepted", "Accepted", "Released" }, settled.ToArray());
            Assert.Empty(await Run("receive", new() { ["from"] = "inbox", ["wait"] = "0" })); // nothing waiting: no records, no failure
        }

        [Fact]
        public async Task A_test_names_the_broker_and_says_why_it_cannot_connect()
        {
            var connector = new AmqpConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.OK, "{}")), Endpoint, credentials: Credentials, timeoutSeconds: 10));
            Assert.True(ok.Success, ok.Message);
            Assert.StartsWith("Connected to ", ok.Message, StringComparison.Ordinal);
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.OK, "{}")), Endpoint, credentials: new Dictionary<string, string> { ["username"] = User, ["password"] = "wrong" }, timeoutSeconds: 10));
            Assert.False(refused.Success);
            Assert.StartsWith("The AMQP broker refused the sign-in - check the user and password", refused.Message, StringComparison.Ordinal);
            var unpinned = new StubEgress(new StubHandler(HttpStatusCode.OK, "{}")) { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => connector.TestConnectionAsync(Contexts.Test(unpinned, Endpoint, credentials: Credentials, timeoutSeconds: 10))); // the host's refusal is its own
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (var i = 0; i < 100 && !condition(); i++)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>
    /// BL-229 - AMQP 1.0 LIVE, against this machine's throwaway RabbitMQ 4.1 (scripts/rabbitmq-test-server.ps1, its AMQP 1.0 on
    /// the same port as its AMQP 0-9-1) - fail-not-skip without it (D46): a test naming RabbitMQ; a message to a queue by its
    /// bare name - as n8n's sender writes one - made and taken back with its properties by its v2 address; a queue RabbitMQ has
    /// not got rejected, an exchange's routing key bound to nothing released, n8n's own topic:// refused - each the step's
    /// failure where n8n's sender said "sent"; a message released back and there again. Its queue deleted afterwards.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AmqpConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"amqp://{RabbitMqTestEndpoint.Host}:{RabbitMqTestEndpoint.Port}";

        private static Dictionary<string, string> Credentials => new() { ["username"] = RabbitMqTestEndpoint.User, ["password"] = RabbitMqTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { RabbitMqTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AmqpConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, new Dictionary<string, string>(), Credentials, timeoutSeconds: 30));

        [Fact]
        public async Task A_message_is_sent_and_taken_back_and_what_rabbitmq_refuses_is_a_failure()
        {
            var tested = await new AmqpConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 30));
            Assert.True(tested.Success, tested.Message);
            Assert.StartsWith("Connected to RabbitMQ", tested.Message, StringComparison.Ordinal);

            var queue = "stomme-amqp-" + Guid.NewGuid().ToString("N")[..8];
            output.WriteLine($"queue {queue}");
            try
            {
                Assert.Equal(true, (bool?)Assert.Single(await All("send", new() { ["to"] = queue, ["message"] = "{\"order\":1}", ["content_type"] = "application/json", ["headers"] = "{\"source\": \"kassa\"}", ["message_id"] = "o-1" })).Fields["accepted"]);
                await All("send", new() { ["to"] = "/queues/" + queue, ["message"] = "{\"order\":2}" });
                var got = await All("receive", new() { ["from"] = "/queues/" + queue, ["max"] = "1", ["wait"] = "10" });
                Assert.Equal(("{\"order\":1}", "application/json", "o-1", "{\"source\":\"kassa\"}"), ((string?)Assert.Single(got).Fields["body"], (string?)got[0].Fields["content_type"], (string?)got[0].Fields["message_id"], (string?)got[0].Fields["properties"]));

                Assert.Equal("{\"order\":2}", (string?)Assert.Single(await All("receive", new() { ["from"] = "/queues/" + queue, ["wait"] = "10", ["accept"] = "false" })).Fields["body"]);
                Assert.Equal("{\"order\":2}", (string?)Assert.Single(await All("receive", new() { ["from"] = "/queues/" + queue, ["wait"] = "10" })).Fields["body"]); // released: there again
                Assert.Empty(await All("receive", new() { ["from"] = "/queues/" + queue, ["wait"] = "1" }));

                Assert.Equal($"The AMQP broker rejected the message: no queue 'missing-{queue}' in vhost '/' (amqp:not-found).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("send", new() { ["to"] = $"/queues/missing-{queue}", ["message"] = "x" }))).Message);
                Assert.StartsWith("The AMQP broker released the message - nothing took it", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("send", new() { ["to"] = $"/exchanges/amq.topic/{queue}.nobody", ["message"] = "x" }))).Message, StringComparison.Ordinal);
                Assert.StartsWith("The AMQP broker", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("send", new() { ["to"] = "topic://kassa.order", ["message"] = "x" }))).Message, StringComparison.Ordinal); // n8n's own placeholder is ActiveMQ's
            }
            finally
            {
                var factory = new RabbitMQ.Client.ConnectionFactory { HostName = RabbitMqTestEndpoint.Host, Port = RabbitMqTestEndpoint.Port, UserName = RabbitMqTestEndpoint.User, Password = RabbitMqTestEndpoint.Password };
                await using var connection = await factory.CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }
        }
    }
}
