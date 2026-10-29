#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace Stomme.Modules.Connectors.Mqtt
{
    /// <summary>
    /// MQTT (BL-229, n8n's MQTT node) over MQTTnet, MQTT 3.1.1: the endpoint is ONE broker, <c>mqtt://broker.example.com:1883</c>
    /// (<c>mqtts://</c> for TLS, 8883), and never a user or password - the Basic scheme's <c>username</c> and <c>password</c>,
    /// both optional as a broker may let anyone in, come from the per-site secret store. A test connects and disconnects.
    /// </summary>
    /// <remarks>
    /// <para><c>publish</c> - a message on a topic at QoS 0, 1 (n8n's default is 0) or 2, retained when asked; at QoS 1 or 2 the
    /// broker's acknowledgement awaited, so a step that finishes is a message the broker received (MQTT 3.1.1 acknowledges
    /// with no reason: a topic the broker refuses this user is dropped silently). <c>receive</c> - the messages on
    /// a topic filter (<c>kassa/#</c>) for up to <c>wait</c> seconds or <c>max</c> messages, whichever comes first - a retained
    /// message arrives at once - each its topic, payload as text, QoS and whether it was retained. Every value held to its shape
    /// before anything is dialled; a topic to publish to holds no wildcard.</para>
    /// <para><b>The client dials only the address the host pinned</b>: the broker's host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> and the permitted address is what the client connects to, its name kept
    /// for TLS. A user or password in the address, a WebSocket address (its own HTTP the egress does not check) and more than one
    /// broker are refused.</para>
    /// </remarks>
    public sealed class MqttConnector : IConnector, IConnectorExtractor
    {
        public const string PublishOperation = "publish";

        public const string ReceiveOperation = "receive";

        public const int DefaultMax = 100;

        /// <summary>The most a receive collects in one step.</summary>
        public const int MaxMessages = 10_000;

        public const int DefaultWaitSeconds = 5;

        /// <summary>The longest a receive waits.</summary>
        public const int MaxWaitSeconds = 300;

        private static readonly string[] Known = { PublishOperation, ReceiveOperation };

        public string ConnectorType => "mqtt";

        public string DisplayName => "MQTT";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "mqtt://broker.example.com:1883";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "User (empty when the broker lets anyone in)",
            ["password"] = "Password",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging, ConnectorCategory.Development };

        public Uri? WebsiteUrl => new("https://mqtt.org");

        public Uri? ApiDocumentationUrl => new("https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/mqtt-v3.1.1.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("client_id", "Client id", ConnectorConfigFieldType.Text, Required: false, Description: "The client id the broker sees - a fresh one each time when empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField QosField = Field("qos", "QoS", "0 (at most once, n8n's default), 1 (at least once) or 2 (exactly once).", type: ConnectorConfigFieldType.Integer) with { Default = "0" };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PublishOperation, "Publish a message", "A message on a topic - n8n's MQTT Send a Message.")
            {
                Parameters = Schema(
                    Field("topic", "Topic", "The topic - kassa/orders. No wildcard.", required: true),
                    Field("message", "Message", "The message - text, or JSON as text; one over several lines in the step's Query box.", type: ConnectorConfigFieldType.Multiline),
                    QosField,
                    Field("retain", "Retain", "The broker keeps it as the topic's last message, for whoever subscribes later.", type: ConnectorConfigFieldType.Boolean) with { Default = "false" }),
            },
            new ConnectorOperation(ReceiveOperation, "Receive messages", "The messages on a topic filter for a while - a retained one at once.")
            {
                Parameters = Schema(
                    Field("topic", "Topic filter", "The topic filter - kassa/# or kassa/+/paid.", required: true),
                    Field("wait", "Wait (seconds)", "How long to listen, at most - 1 to 300.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultWaitSeconds.ToString(CultureInfo.InvariantCulture) },
                    Field("max", "At most", "How many messages, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    QosField),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            Broker broker;
            try
            {
                broker = Broker.Of(context.Endpoint);
            }
            catch (ArgumentException ex)
            {
                return ConnectionTestResult.Fail($"Invalid MQTT address: {ex.Message}");
            }

            var options = await OptionsAsync(broker, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
            try
            {
                using var client = new MqttClientFactory().CreateMqttClient();
                await ConnectAsync(client, options, cancellationToken);
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
                return ConnectionTestResult.Ok("Connected to the MQTT broker.");
            }
            catch (Exception ex) when (ex is MqttCommunicationException or SocketException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                return ConnectionTestResult.Fail(ex.Message); // the broker's or the network's failure; the message stays inside the runner (D110)
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PublishOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"MQTT has no operation '{context.OperationName}' - {string.Join(" or ", Known)}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is dialled.
            var topic = Values.Get(p, "topic") ?? throw new InvalidOperationException("Give the topic (topic).");
            if (topic.Any(char.IsControl) || Encoding.UTF8.GetByteCount(topic) > 65535)
            {
                throw new InvalidOperationException($"'{topic}' is not a topic - one line (topic).");
            }

            if (operation == PublishOperation && (topic.Contains('#') || topic.Contains('+')))
            {
                throw new InvalidOperationException($"'{topic}' holds a wildcard - a message is published to one topic (topic).");
            }

            var qos = Values.GetOrDefault(p, "qos", "0") switch
            {
                "0" => MqttQualityOfServiceLevel.AtMostOnce,
                "1" => MqttQualityOfServiceLevel.AtLeastOnce,
                "2" => MqttQualityOfServiceLevel.ExactlyOnce,
                var other => throw new InvalidOperationException($"'{other}' is not 0, 1 or 2 (qos)."),
            };
            var retain = Values.Get(p, "retain") is not { } flag ? false : bool.TryParse(flag, out var yes) ? yes : throw new InvalidOperationException($"'{flag}' is not true or false (retain).");
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= MaxMessages ? n : throw new InvalidOperationException($"'{most}' is not a number from 1 to {MaxMessages} (max).") : DefaultMax;
            var wait = Values.Get(p, "wait") is { } seconds ? int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is > 0 and <= MaxWaitSeconds ? s : throw new InvalidOperationException($"'{seconds}' is not a number of seconds from 1 to {MaxWaitSeconds} (wait).") : DefaultWaitSeconds;
            byte[] payload = Array.Empty<byte>();
            if (operation == PublishOperation)
            {
                // As given, not trimmed: one line as 'message', or as many as it takes in the step's Query box; an empty message is MQTT's own.
                var message = p.TryGetValue("message", out var raw) && !string.IsNullOrEmpty(raw) ? raw : p.TryGetValue("query", out var query) && !string.IsNullOrEmpty(query) ? query : "";
                payload = Encoding.UTF8.GetBytes(message);
            }

            var broker = Broker.Of(context.Endpoint);
            var options = await OptionsAsync(broker, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
            using var client = new MqttClientFactory().CreateMqttClient();
            if (operation == PublishOperation)
            {
                await ConnectAsync(client, options, cancellationToken);
                var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel(qos).WithRetainFlag(retain).Build();
                // MQTT 3.1.1's acknowledgement carries no reason: it says the broker received the message, not that a reader may
                // have it - a broker that refuses a topic to this user drops what is published there without a word.
                await client.PublishAsync(message, cancellationToken);

                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
                yield return Fields(("topic", topic), ("bytes", payload.Length), ("qos", (int)qos), ("retain", retain), ("acknowledged", qos != MqttQualityOfServiceLevel.AtMostOnce));
                yield break;
            }

            // Received messages queue here as they arrive; the step reads them until the wait ends or max have come.
            var received = Channel.CreateBounded<MqttApplicationMessage>(new BoundedChannelOptions(max) { FullMode = BoundedChannelFullMode.DropWrite });
            client.ApplicationMessageReceivedAsync += e =>
            {
                received.Writer.TryWrite(e.ApplicationMessage);
                return Task.CompletedTask;
            };
            await ConnectAsync(client, options, cancellationToken);
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic(topic).WithQualityOfServiceLevel(qos)).Build(), cancellationToken);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(TimeSpan.FromSeconds(wait));
            var read = 0;
            while (read < max)
            {
                MqttApplicationMessage message;
                try
                {
                    message = await received.Reader.ReadAsync(window.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break; // the wait is over
                }

                read++;
                yield return Fields(
                    ("topic", message.Topic),
                    ("payload", Encoding.UTF8.GetString(message.Payload.ToArray())),
                    ("qos", (int)message.QualityOfServiceLevel),
                    ("retain", message.Retain));
            }

            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }

        /// <summary>The broker the endpoint names - one host and port, TLS for mqtts - checked before anything is dialled.</summary>
        internal sealed record Broker(string Host, int Port, bool Tls)
        {
            public static Broker Of(string endpoint)
            {
                var given = (endpoint ?? "").Trim();
                if (given.Length == 0)
                {
                    throw new ArgumentException("give the broker's address - mqtt://broker.example.com:1883.");
                }

                if (given.Contains(',', StringComparison.Ordinal))
                {
                    throw new ConnectorEgressDeniedException("An MQTT connection names ONE broker; a list of brokers cannot be pinned to a checked address.");
                }

                if (!given.Contains("://", StringComparison.Ordinal))
                {
                    given = "mqtt://" + given;
                }

                if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var uri) || uri.Host.Length == 0)
                {
                    throw new ArgumentException($"'{endpoint}' is not an MQTT address - mqtt://broker.example.com:1883, or mqtts:// for TLS.");
                }

                if (uri.Scheme is "ws" or "wss")
                {
                    throw new ConnectorEgressDeniedException("An MQTT connection is dialled over TCP; a WebSocket address goes through HTTP this site's egress does not check.");
                }

                if (uri.Scheme is not ("mqtt" or "mqtts" or "tcp" or "ssl"))
                {
                    throw new ArgumentException($"'{endpoint}' is not an MQTT address - mqtt://broker.example.com:1883, or mqtts:// for TLS.");
                }

                if (uri.UserInfo.Length > 0)
                {
                    throw new ConnectorEgressDeniedException("An MQTT address carries no user or password - they are the connection's credentials.");
                }

                if (uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                {
                    throw new ArgumentException("the address names the broker alone - no path or options.");
                }

                var tls = uri.Scheme is "mqtts" or "ssl";
                return new Broker(uri.Host, uri.IsDefaultPort || uri.Port < 0 ? (tls ? 8883 : 1883) : uri.Port, tls);
            }
        }

        // The client's options: the broker's host resolved and checked by the egress, its address what the client dials; the
        // name kept for TLS; the credentials when given; MQTT 3.1.1, a clean session, the connection's timeout.
        private static async Task<MqttClientOptions> OptionsAsync(Broker broker, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var clientId = Values.Get(settings, "client_id") ?? "stomme-" + Guid.NewGuid().ToString("N")[..12];
            if (clientId.Any(char.IsControl) || clientId.Length > 128)
            {
                throw new InvalidOperationException($"'{clientId}' is not a client id - one line of up to 128 characters (client_id).");
            }

            var pinned = await egress.ResolveAndPinAsync(broker.Host, broker.Port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The MQTT host '{broker.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(pinned.ToString(), broker.Port)
                .WithClientId(clientId)
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
                .WithCleanSession(true)
                .WithTimeout(timeout);
            if (Values.Get(credentials, "username") is { } user)
            {
                builder = builder.WithCredentials(user, credentials.TryGetValue("password", out var password) ? password ?? "" : "");
            }

            if (broker.Tls)
            {
                builder = builder.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(broker.Host));
            }

            return builder.Build();
        }

        // MQTTnet 5 answers a broker's refusal - a wrong password, no user where one is needed - as the CONNACK's code, not a throw.
        private static async Task ConnectAsync(IMqttClient client, MqttClientOptions options, CancellationToken cancellationToken)
        {
            var connected = await client.ConnectAsync(options, cancellationToken);
            if (connected.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new InvalidOperationException($"The MQTT broker refused the connection: {connected.ResultCode}.");
            }
        }

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
