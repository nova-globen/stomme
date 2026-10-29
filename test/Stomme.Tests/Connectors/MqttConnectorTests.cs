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
using Stomme.Modules.Connectors.Mqtt;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The MQTT connector's live tests' broker: MQTT_TEST_HOST / PORT / PASSWORD where set, otherwise this machine's
    /// throwaway Mosquitto that <c>scripts/mqtt-test-server.ps1</c> makes on 127.0.0.1:11883, its user <c>kafe</c> and its
    /// password read from the file that script writes under the user's temp folder. No password is a FAILURE that says how to
    /// get one, never a skip.
    /// </summary>
    internal static class MqttTestEndpoint
    {
        public const string User = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("MQTT_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("MQTT_TEST_PORT"), out var port) ? port : 11883;

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("MQTT_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-mqtt-test", "password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No MQTT test broker: run 'pwsh scripts/mqtt-test-server.ps1' (a throwaway Mosquitto on 127.0.0.1:11883, its password in {file}) or set MQTT_TEST_HOST, MQTT_TEST_PORT and MQTT_TEST_PASSWORD.");
            }
        }
    }

    /// <summary>
    /// The MQTT connector without a broker: an address that is not MQTT's, or names a path, invalid; a user or password
    /// in it, a WebSocket address and a list of brokers refused as the egress's denial - each before any pin; the host pinned on
    /// its port and MQTT's own 1883 and 8883, a refused host the guard's own denial; every parameter held to its shape - a
    /// wildcard topic to publish to among them - before anything is dialled.
    /// </summary>
    public sealed class MqttConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe", ["password"] = "pw-1" };

        [Fact]
        public async Task An_address_that_is_not_mqtts_is_invalid_and_what_reaches_past_the_egress_is_denied()
        {
            var egress = new StubEgress();
            foreach (var endpoint in new[] { "", "http://broker.internal", "mqtt://broker.internal:1883/kassa", "mqtt://broker.internal?clean=1" })
            {
                var result = await new MqttConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials));
                Assert.False(result.Success);
                Assert.StartsWith("Invalid MQTT address: ", result.Message, StringComparison.Ordinal);
            }

            foreach (var (endpoint, refusal) in new[] { ("mqtt://ada:pw@broker.internal", "no user or password"), ("wss://broker.internal/mqtt", "WebSocket"), ("a.internal:1883,b.internal:1883", "names ONE broker") })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MqttConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials)));
                Assert.Contains(refusal, denied.Message, StringComparison.Ordinal);
                Assert.True(OutboundConnectGuard.IsDenial(denied)); // the runner says DeniedByPolicy
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_through_the_egress()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MqttConnector().TestConnectionAsync(Contexts.Test(refused, "broker.internal", credentials: Credentials)));
            Assert.Contains("broker.internal", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("broker.internal", 1883), refused.Pins.Single()); // MQTT's own port

            var tls = new StubEgress { Pinned = IPAddress.Loopback };
            var closed = await new MqttConnector().TestConnectionAsync(Contexts.Test(tls, "mqtts://broker.internal", credentials: Credentials, timeoutSeconds: 2));
            Assert.False(closed.Success); // nothing listens at the pinned address: the client's failure, said
            Assert.Equal(("broker.internal", 8883), tls.Pins.Single());
            Assert.Equal(new MqttConnector.Broker("broker.internal", 1884, false), MqttConnector.Broker.Of("tcp://broker.internal:1884"));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var egress = new StubEgress();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MqttConnector(), Contexts.Extract(egress, "mqtt://broker.internal", operation, parameters, settings, Credentials)));
                Assert.Empty(egress.Pins);
                return refusal.Message;
            }

            Assert.Equal("MQTT has no operation 'subscribe' - publish or receive.", await Refused("subscribe", new()));
            Assert.Equal("Give the topic (topic).", await Refused("publish", new() { ["message"] = "x" }));
            Assert.Equal("'kassa/#' holds a wildcard - a message is published to one topic (topic).", await Refused("publish", new() { ["topic"] = "kassa/#", ["message"] = "x" }));
            Assert.Equal("'kassa/+/paid' holds a wildcard - a message is published to one topic (topic).", await Refused("publish", new() { ["topic"] = "kassa/+/paid", ["message"] = "x" }));
            Assert.Equal("'kassa\tx' is not a topic - one line (topic).", await Refused("publish", new() { ["topic"] = "kassa\tx" }));
            Assert.Equal("'3' is not 0, 1 or 2 (qos).", await Refused("publish", new() { ["topic"] = "kassa", ["qos"] = "3" }));
            Assert.Equal("'maybe' is not true or false (retain).", await Refused("publish", new() { ["topic"] = "kassa", ["retain"] = "maybe" }));
            Assert.Equal("'0' is not a number from 1 to 10000 (max).", await Refused("receive", new() { ["topic"] = "kassa/#", ["max"] = "0" }));
            Assert.Equal("'301' is not a number of seconds from 1 to 300 (wait).", await Refused("receive", new() { ["topic"] = "kassa/#", ["wait"] = "301" }));
        }
    }

    /// <summary>
    /// The MQTT connector against a real Mosquitto 2.1 (<c>scripts/mqtt-test-server.ps1</c>'s throwaway broker on
    /// loopback) through the HOST's egress: loopback refused until allowlisted; a test that connects, a wrong password and no
    /// user refused; the client dialling the pinned address and never the name; messages published at QoS 0, 1 and 2 - one over
    /// several lines from the step's Query box - received on a topic filter with their topics, payloads and QoS; a retained
    /// message received at once by a later subscriber, and cleared; no more than asked, and no longer than the wait. Fails, never
    /// skips, when the broker is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MqttConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"mqtt://{MqttTestEndpoint.Host}:{MqttTestEndpoint.Port}";

        private static Dictionary<string, string> Credentials => new() { ["username"] = MqttTestEndpoint.User, ["password"] = MqttTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { MqttTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new MqttConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, credentials: Credentials, timeoutSeconds: 15));

        [Fact]
        public async Task A_test_connects_and_a_wrong_password_and_no_user_are_refused()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MqttConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15)));
            var right = await new MqttConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.False((await new MqttConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = MqttTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 15))).Success);
            Assert.False((await new MqttConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string>(), timeoutSeconds: 15))).Success); // anonymous refused

            // A name that resolves nowhere, pinned to the broker: the client dials the address, never the name.
            var pinned = new StubEgress { Pinned = IPAddress.Parse(MqttTestEndpoint.Host) };
            var byPin = await new MqttConnector().TestConnectionAsync(Contexts.Test(pinned, $"mqtt://kassa-broker.invalid:{MqttTestEndpoint.Port}", credentials: Credentials, timeoutSeconds: 15));
            Assert.True(byPin.Success, byPin.Message);
            Assert.Equal(("kassa-broker.invalid", MqttTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task Messages_published_at_each_qos_are_received_on_a_topic_filter()
        {
            var root = "kassa/" + Guid.NewGuid().ToString("N")[..8];
            // The receive listens while the publishes go: it starts first and waits up to six seconds or three messages.
            var listening = Run("receive", new() { ["topic"] = root + "/#", ["wait"] = "6", ["max"] = "3", ["qos"] = "1" });
            await Task.Delay(1000);
            var sent = new List<ConnectorRecord>();
            sent.AddRange(await Run("publish", new() { ["topic"] = root + "/orders", ["message"] = "{\"nummer\": 1042, \"kund\": \"Kafé Ada\"}" }));
            sent.AddRange(await Run("publish", new() { ["topic"] = root + "/orders", ["query"] = "rad 1\nrad 2", ["qos"] = "1" }));
            sent.AddRange(await Run("publish", new() { ["topic"] = root + "/paid", ["message"] = "1042", ["qos"] = "2" }));
            Assert.Equal(new[] { (0, false), (1, true), (2, true) }, sent.Select(record => ((int)record.Fields["qos"]!, (bool)record.Fields["acknowledged"]!)));

            var received = await listening;
            Assert.Equal(new[] { ($"{root}/orders", "{\"nummer\": 1042, \"kund\": \"Kafé Ada\"}"), ($"{root}/orders", "rad 1\nrad 2"), ($"{root}/paid", "1042") },
                received.Select(message => ((string)message.Fields["topic"]!, (string)message.Fields["payload"]!)));
            Assert.Equal(new[] { 0, 1, 1 }, received.Select(message => (int)message.Fields["qos"]!)); // the lower of the publish's and the subscription's
        }

        [Fact]
        public async Task A_retained_message_reaches_a_later_subscriber_at_once_and_the_wait_ends_a_quiet_receive()
        {
            var topic = "kassa/" + Guid.NewGuid().ToString("N")[..8] + "/status";
            Assert.True((bool)Assert.Single(await Run("publish", new() { ["topic"] = topic, ["message"] = "öppen", ["retain"] = "true", ["qos"] = "1" })).Fields["retain"]!);
            var started = DateTime.UtcNow;
            var retained = Assert.Single(await Run("receive", new() { ["topic"] = topic, ["wait"] = "10", ["max"] = "1" })).Fields;
            Assert.Equal(("öppen", true), ((string?)retained["payload"], (bool?)retained["retain"]));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8)); // max reached: no waiting out the window

            await Run("publish", new() { ["topic"] = topic, ["message"] = "", ["retain"] = "true" }); // an empty retained message clears it
            started = DateTime.UtcNow;
            Assert.Empty(await Run("receive", new() { ["topic"] = topic, ["wait"] = "2" }));
            Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10)); // the wait, then nothing
        }
    }
}
