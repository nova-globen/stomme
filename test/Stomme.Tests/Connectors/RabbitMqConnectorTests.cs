#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The RabbitMQ connector's live tests' broker (BL-229): RABBITMQ_TEST_HOST / PORT / PASSWORD where set, otherwise this
    /// machine's throwaway RabbitMQ that <c>scripts/rabbitmq-test-server.ps1</c> makes on 127.0.0.1:15673, its user <c>kafe</c>
    /// and its password read from the file that script writes under the user's temp folder. No password is a FAILURE that
    /// says how to get one, never a skip (D46).
    /// </summary>
    internal static class RabbitMqTestEndpoint
    {
        public const string User = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("RABBITMQ_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_TEST_PORT"), out var port) ? port : 15673;

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("RABBITMQ_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-rabbitmq-test", "password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No RabbitMQ test broker: run 'pwsh scripts/rabbitmq-test-server.ps1' (a throwaway RabbitMQ on 127.0.0.1:15673, its password in {file}) or set RABBITMQ_TEST_HOST, RABBITMQ_TEST_PORT and RABBITMQ_TEST_PASSWORD.");
            }
        }
    }

    /// <summary>
    /// BL-229 - the RabbitMQ connector without a broker: an address that is not AMQP's, or carries options, invalid; a user or
    /// password in it and a list of brokers refused as the egress's denial - each before any pin; the host pinned on its port
    /// and AMQP's own 5672 and 5671, a refused host the guard's own denial; the virtual host from the path; TLS naming the
    /// broker's host; the scheme's credentials applied and the client's own recovery off; every parameter held to its shape
    /// before anything is dialled.
    /// </summary>
    public sealed class RabbitMqConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe", ["password"] = "pw-1" };

        [Fact]
        public async Task An_address_that_is_not_amqps_is_invalid_and_what_reaches_past_the_egress_is_denied()
        {
            var egress = new StubEgress();
            foreach (var endpoint in new[] { "", "http://rabbit.internal", "amqp://rabbit.internal:5672/kassa?heartbeat=10" })
            {
                var result = await new RabbitMqConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials));
                Assert.False(result.Success);
                Assert.StartsWith("Invalid RabbitMQ address: ", result.Message, StringComparison.Ordinal);
            }

            foreach (var (endpoint, refusal) in new[] { ("amqp://guest:guest@rabbit.internal/", "no user or password"), ("amqp://a.internal,b.internal/", "names ONE broker") })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RabbitMqConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials)));
                Assert.Contains(refusal, denied.Message, StringComparison.Ordinal);
                Assert.True(OutboundConnectGuard.IsDenial(denied)); // the runner says DeniedByPolicy
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_through_the_egress_and_the_broker_built_from_the_address()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RabbitMqConnector().TestConnectionAsync(Contexts.Test(refused, "rabbit.internal", credentials: Credentials)));
            Assert.Contains("rabbit.internal", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("rabbit.internal", 5672), refused.Pins.Single()); // AMQP's own port

            var tls = new StubEgress { Pinned = IPAddress.Loopback };
            var closed = await new RabbitMqConnector().TestConnectionAsync(Contexts.Test(tls, "amqps://rabbit.internal/", credentials: Credentials, timeoutSeconds: 2));
            Assert.False(closed.Success); // nothing listens at the pinned address: the client's failure, said
            Assert.Equal(("rabbit.internal", 5671), tls.Pins.Single());

            var factory = RabbitMqConnector.Build("amqps://rabbit.internal:5999/kassa%2Fprod", Credentials, TimeSpan.FromSeconds(7), out var host, out var port);
            Assert.Equal(("rabbit.internal", 5999, "kassa/prod", "kafe", "pw-1"), (host, port, factory.VirtualHost, factory.UserName, factory.Password));
            Assert.Equal((true, "rabbit.internal"), (factory.Ssl.Enabled, factory.Ssl.ServerName)); // the certificate checked against the broker's name
            Assert.Equal((false, false, TimeSpan.FromSeconds(7)), (factory.AutomaticRecoveryEnabled, factory.TopologyRecoveryEnabled, factory.RequestedConnectionTimeout));
            Assert.Equal("/", RabbitMqConnector.Build("rabbit.internal", Credentials, TimeSpan.FromSeconds(7), out _, out _).VirtualHost); // the default virtual host
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var egress = new StubEgress();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RabbitMqConnector(), Contexts.Extract(egress, "amqp://rabbit.internal/", operation, parameters, credentials: Credentials)));
                Assert.Empty(egress.Pins);
                return refusal.Message;
            }

            Assert.Equal("RabbitMQ has no operation 'purge' - publish, get or count.", await Refused("purge", new()));
            Assert.Equal("Give the queue or the exchange to publish to.", await Refused("publish", new() { ["message"] = "x" }));
            Assert.Equal("Give the queue or the exchange, not both.", await Refused("publish", new() { ["queue"] = "q", ["exchange"] = "e", ["message"] = "x" }));
            Assert.Equal("Give the message (message) - or write it in the step's Query box.", await Refused("publish", new() { ["queue"] = "q" }));
            Assert.Equal("'q\nx' is not a queue's name - one line of up to 255 bytes (queue).", await Refused("publish", new() { ["queue"] = "q\nx", ["message"] = "x" }));
            Assert.Equal("'shuffle' is not fanout, direct, topic or headers (exchange_type).", await Refused("publish", new() { ["exchange"] = "e", ["exchange_type"] = "shuffle", ["message"] = "x" }));
            Assert.Equal("'256' is not a number from 0 to 255 (priority).", await Refused("publish", new() { ["queue"] = "q", ["message"] = "x", ["priority"] = "256" }));
            Assert.Equal("'-5' is not a number of milliseconds (expiration).", await Refused("publish", new() { ["queue"] = "q", ["message"] = "x", ["expiration"] = "-5" }));
            Assert.Equal("The headers are not JSON - {\"source\": \"kassa\"}.", await Refused("publish", new() { ["queue"] = "q", ["message"] = "x", ["headers"] = "source=kassa" }));
            Assert.Equal("The header 'tags' is not text, a number or true/false.", await Refused("publish", new() { ["queue"] = "q", ["message"] = "x", ["headers"] = "{\"tags\": [1]}" }));
            Assert.Equal("'maybe' is not true or false (persistent).", await Refused("publish", new() { ["queue"] = "q", ["message"] = "x", ["persistent"] = "maybe" }));
            Assert.Equal("Give the queue - its name.", await Refused("get", new()));
            Assert.Equal("'0' is not a number from 1 to 10000 (max).", await Refused("get", new() { ["queue"] = "q", ["max"] = "0" }));
            Assert.Equal("Give the queue - its name.", await Refused("count", new()));
        }
    }

    /// <summary>
    /// BL-229 - the RabbitMQ connector against a real RabbitMQ 4.1 (<c>scripts/rabbitmq-test-server.ps1</c>'s throwaway broker on
    /// loopback) through the HOST's egress: loopback refused until allowlisted; a test naming the broker, a wrong password
    /// failing; the client dialling the pinned address and never the name; a message published to a queue - declared first -
    /// with its type, headers and ids, confirmed, counted, got with them all and acknowledged; one put back and got again,
    /// redelivered; a message through an exchange to the queue bound to it by its routing key; no further than asked; a queue
    /// that is not there the broker's refusal. Fails, never skips, when the broker is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RabbitMqConnectorLiveTests(ITestOutputHelper output) : IAsyncLifetime
    {
        // What a test declared on the broker, deleted after it, so the throwaway broker does not gather a run's queues.
        private readonly List<string> _queues = new();

        private readonly List<string> _exchanges = new();

        private static string Endpoint => $"amqp://{RabbitMqTestEndpoint.Host}:{RabbitMqTestEndpoint.Port}/";

        private static Dictionary<string, string> Credentials => new() { ["username"] = RabbitMqTestEndpoint.User, ["password"] = RabbitMqTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { RabbitMqTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new RabbitMqConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, credentials: Credentials, timeoutSeconds: 15));

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_queues.Count + _exchanges.Count == 0)
            {
                return;
            }

            var factory = RabbitMqConnector.Build(Endpoint, Credentials, TimeSpan.FromSeconds(15), out _, out _);
            factory.HostName = RabbitMqTestEndpoint.Host;
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in _queues)
            {
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }

            foreach (var exchange in _exchanges)
            {
                await channel.ExchangeDeleteAsync(exchange);
            }
        }

        [Fact]
        public async Task A_test_names_the_broker_and_a_wrong_password_fails_and_loopback_waits_for_the_allowlist()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RabbitMqConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15)));
            var right = await new RabbitMqConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.StartsWith("Connected to RabbitMQ 4.", right.Message, StringComparison.Ordinal);
            var wrong = await new RabbitMqConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = RabbitMqTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 15));
            Assert.False(wrong.Success);

            // A name that resolves nowhere, pinned to the broker: the client dials the address, never the name.
            var pinned = new StubEgress { Pinned = IPAddress.Parse(RabbitMqTestEndpoint.Host) };
            var byPin = await new RabbitMqConnector().TestConnectionAsync(Contexts.Test(pinned, $"amqp://kassa-broker.invalid:{RabbitMqTestEndpoint.Port}/", credentials: Credentials, timeoutSeconds: 15));
            Assert.True(byPin.Success, byPin.Message);
            Assert.Equal(("kassa-broker.invalid", RabbitMqTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task A_message_is_published_counted_got_and_acknowledged_or_put_back()
        {
            var queue = "kassa.orders." + Guid.NewGuid().ToString("N")[..8];
            _queues.Add(queue);
            const string Body = "{\"nummer\": 1042, \"kund\": \"Kafé Ada\"}";
            var published = Assert.Single(await Run("publish", new()
            {
                ["queue"] = queue, ["message"] = Body, ["content_type"] = "application/json", ["headers"] = "{\"source\": \"kassa\", \"try\": 2, \"urgent\": true}",
                ["message_id"] = "m-1042", ["correlation_id"] = "c-7", ["priority"] = "5", ["durable"] = "false",
            })).Fields;
            Assert.Equal((queue, Body.Length + 1, true), ((string?)published["queue"], (int?)published["bytes"], (bool?)published["confirmed"])); // é takes two bytes
            Assert.Equal(1L, Assert.Single(await Run("count", new() { ["queue"] = queue })).Fields["messages"]);

            Assert.Single(await Run("publish", new() { ["queue"] = queue, ["message"] = "1043", ["durable"] = "false" }));
            var looked = await Run("get", new() { ["queue"] = queue, ["ack"] = "false" });
            Assert.Equal(new[] { Body, "1043" }, looked.Select(message => (string?)message.Fields["body"])); // each once: none put back until the pass ends
            Assert.Equal(2L, Assert.Single(await Run("count", new() { ["queue"] = queue })).Fields["messages"]); // then both back
            var kept = looked[0].Fields;
            Assert.Equal((Body, false), ((string?)kept["body"], (bool?)kept["redelivered"]));
            var again = (await Run("get", new() { ["queue"] = queue, ["max"] = "1" }))[0].Fields; // put back, then taken
            Assert.Equal((Body, true, "application/json", "m-1042", "c-7", "", queue), ((string?)again["body"], (bool?)again["redelivered"], (string?)again["content_type"], (string?)again["message_id"], (string?)again["correlation_id"], (string?)again["exchange"], (string?)again["routing_key"]));
            Assert.Equal("{\"source\":\"kassa\",\"try\":2,\"urgent\":true}", again["headers"]);
            Assert.Equal("1043", (string?)Assert.Single(await Run("get", new() { ["queue"] = queue })).Fields["body"]); // the first acknowledged: gone
            Assert.Empty(await Run("get", new() { ["queue"] = queue }));
            Assert.Equal(0L, Assert.Single(await Run("count", new() { ["queue"] = queue })).Fields["messages"]);
        }

        [Fact]
        public async Task A_message_over_several_lines_comes_from_the_steps_query_box_as_it_is()
        {
            var queue = "kassa.notes." + Guid.NewGuid().ToString("N")[..8];
            _queues.Add(queue);
            const string Lines = " Kassan stängd\r\nÅter 08:00 \n";
            Assert.Single(await Run("publish", new() { ["queue"] = queue, ["query"] = Lines, ["durable"] = "false", ["persistent"] = "false" }));
            Assert.Equal(Lines, (string?)Assert.Single(await Run("get", new() { ["queue"] = queue })).Fields["body"]); // not trimmed, every line
        }

        [Fact]
        public async Task An_exchange_routes_by_its_key_and_no_more_than_asked_is_taken()
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var exchange = "kassa.events." + tag;
            var queue = "kassa.paid." + tag;
            _queues.Add(queue);
            _exchanges.Add(exchange);
            // The queue, bound to the exchange by the routing key orders.paid - the broker's own declare and bind, through the client.
            var factory = RabbitMqConnector.Build(Endpoint, Credentials, TimeSpan.FromSeconds(15), out _, out _);
            factory.HostName = RabbitMqTestEndpoint.Host;
            await using (var connection = await factory.CreateConnectionAsync())
            await using (var channel = await connection.CreateChannelAsync())
            {
                await channel.ExchangeDeclareAsync(exchange, "direct", durable: false, autoDelete: false);
                await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);
                await channel.QueueBindAsync(queue, exchange, "orders.paid");
            }

            foreach (var (key, message) in new[] { ("orders.paid", "1042"), ("orders.void", "1043"), ("orders.paid", "1044"), ("orders.paid", "1045") })
            {
                var sent = Assert.Single(await Run("publish", new() { ["exchange"] = exchange, ["exchange_type"] = "direct", ["routing_key"] = key, ["message"] = message, ["durable"] = "false" })).Fields;
                Assert.Equal((exchange, key), ((string?)sent["exchange"], (string?)sent["routing_key"]));
            }

            var two = await Run("get", new() { ["queue"] = queue, ["max"] = "2" });
            Assert.Equal(new[] { "1042", "1044" }, two.Select(message => (string?)message.Fields["body"])); // routed by its key; no more than asked
            Assert.Equal((exchange, "orders.paid", 2L), ((string?)two[0].Fields["exchange"], (string?)two[0].Fields["routing_key"], (long?)two[0].Fields["remaining"])); // the two behind it
            Assert.Equal("1045", (string?)Assert.Single(await Run("get", new() { ["queue"] = queue })).Fields["body"]);

            // Declared as it is on the broker - not durable - a publish to the queue goes; declared otherwise, the broker refuses it.
            Assert.Single(await Run("publish", new() { ["queue"] = queue, ["message"] = "1046", ["durable"] = "false" }));
            var inequivalent = await Assert.ThrowsAnyAsync<Exception>(() => Run("publish", new() { ["queue"] = queue, ["message"] = "1047" }));
            Assert.Contains("PRECONDITION_FAILED", inequivalent.Message, StringComparison.Ordinal);

            // A message the broker does not take fails the step - its confirmation awaited - rather than vanish.
            var lost = await Assert.ThrowsAnyAsync<Exception>(() => Run("publish", new() { ["exchange"] = "kassa.none." + tag, ["routing_key"] = "x", ["message"] = "1048", ["declare"] = "false" }));
            output.WriteLine($"[live] {lost.GetType().Name}: {lost.Message}");

            var missing = await Assert.ThrowsAnyAsync<Exception>(() => Run("count", new() { ["queue"] = "kassa.none." + tag }));
            output.WriteLine($"[live] {missing.GetType().Name}: {missing.Message}");
            Assert.Contains("NOT_FOUND", missing.Message, StringComparison.Ordinal);
        }
    }
}
