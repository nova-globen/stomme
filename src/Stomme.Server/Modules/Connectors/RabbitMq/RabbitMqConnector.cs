#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
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
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Stomme.Modules.Connectors.RabbitMq
{
    /// <summary>
    /// RabbitMQ (n8n's RabbitMQ node) over RabbitMQ.Client, AMQP 0-9-1: the endpoint is ONE broker,
    /// <c>amqp://rabbit.example.com:5672/kassa</c> (<c>amqps://</c> for TLS, the virtual host its path), and never a user or a
    /// password - the Basic scheme's <c>username</c> and <c>password</c> come from the per-site secret store. A test opens a
    /// connection and names the broker's version.
    /// </summary>
    /// <remarks>
    /// <para><c>publish</c> - a message to a queue or to an exchange with its routing key, its content type, headers,
    /// persistence, priority, expiry and ids; the queue or exchange declared first as n8n's node does (durable unless asked),
    /// and the broker's confirmation awaited, so a step that finishes is a message the broker took. <c>get</c> - up to
    /// <c>max</c> messages taken from a queue, each acknowledged - or, asked, put back - its body, properties and headers a
    /// record. <c>count</c> - a queue's messages and consumers. Every value held to its shape before anything is dialled.</para>
    /// <para><b>The client dials only the address the host pinned</b>: the broker's host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> and the permitted address is what the client connects to, its name
    /// kept for TLS; automatic recovery is off, so a lost connection is never re-dialled by name. A user or password in the
    /// address, a query of client options and more than one broker are refused.</para>
    /// </remarks>
    public sealed class RabbitMqConnector : IConnector, IConnectorExtractor
    {
        public const string PublishOperation = "publish";

        public const string GetOperation = "get";

        public const string CountOperation = "count";

        public const int DefaultMax = 100;

        /// <summary>The most taken from a queue in one step.</summary>
        public const int MaxMessages = 10_000;

        private static readonly string[] Known = { PublishOperation, GetOperation, CountOperation };

        private static readonly string[] ExchangeTypes = { "direct", "fanout", "topic", "headers" };

        // What RabbitMQ.Client 7 stamps on a message it publishes with its confirmation tracked.
        private const string PublishSequenceHeader = "x-dotnet-pub-seq-no";

        // AMQP's names: up to 255 bytes, no control characters (the broker names a server-named queue amq.gen-...).
        private static readonly Regex Name = new("^[^\\x00-\\x1F\\x7F]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "rabbitmq";

        public string DisplayName => "RabbitMQ";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "amqp://rabbit.example.com:5672/kassa";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "User",
            ["password"] = "Password",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging, ConnectorCategory.Development };

        public Uri? WebsiteUrl => new("https://www.rabbitmq.com");

        public Uri? ApiDocumentationUrl => new("https://www.rabbitmq.com/client-libraries/dotnet-api-guide");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField QueueField = Field("queue", "Queue", "The queue's name - kassa.orders.", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PublishOperation, "Publish a message", "A message to a queue, or to an exchange with a routing key - n8n's RabbitMQ Send a Message.")
            {
                Parameters = Schema(
                    Field("queue", "Queue", "The queue to send to - kassa.orders. Or give an exchange."),
                    Field("exchange", "Exchange", "The exchange to send to - kassa.events."),
                    Field("exchange_type", "Exchange type", "fanout (n8n's default), direct, topic or headers - how the exchange is declared.") with { Default = "fanout" },
                    Field("routing_key", "Routing key", "The routing key - orders.paid. Empty for a fanout exchange."),
                    Field("message", "Message", "The message - text, or JSON as text; one over several lines in the step's Query box.", required: false, ConnectorConfigFieldType.Multiline),
                    Field("content_type", "Content type", "Its media type - application/json. Empty: text/plain."),
                    Field("headers", "Headers", "A JSON object of header names and values - {\"source\": \"kassa\"}."),
                    Field("persistent", "Persistent", "Kept on disk by the broker (the default).", type: ConnectorConfigFieldType.Boolean) with { Default = "true" },
                    Field("priority", "Priority", "0 to 255.", type: ConnectorConfigFieldType.Integer),
                    Field("expiration", "Expires after", "Milliseconds until the broker drops it unread.", type: ConnectorConfigFieldType.Integer),
                    Field("message_id", "Message id", "Its id."),
                    Field("correlation_id", "Correlation id", "The id of what it answers."),
                    Field("declare", "Declare", "Declare the queue or exchange first, as n8n's node does (the default).", type: ConnectorConfigFieldType.Boolean) with { Default = "true" },
                    Field("durable", "Durable", "A declared queue or exchange survives a broker restart (the default).", type: ConnectorConfigFieldType.Boolean) with { Default = "true" }),
            },
            new ConnectorOperation(GetOperation, "Get messages", "Up to max messages taken from a queue, each acknowledged - or put back.")
            {
                Parameters = Schema(
                    QueueField,
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    Field("ack", "Acknowledge", "true (the default) takes them off the queue; false puts them back.", type: ConnectorConfigFieldType.Boolean) with { Default = "true" }),
            },
            new ConnectorOperation(CountOperation, "Count a queue", "How many messages a queue holds, and how many consumers read it.") { Parameters = Schema(QueueField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ConnectionFactory factory;
            try
            {
                factory = Build(context.Endpoint, context.Credentials, context.Timeout, out var host, out var port);
                await PinAsync(factory, host, port, context.Egress, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return ConnectionTestResult.Fail($"Invalid RabbitMQ address: {ex.Message}");
            }

            try
            {
                await using var connection = await factory.CreateConnectionAsync(cancellationToken);
                var version = connection.ServerProperties is { } properties && properties.TryGetValue("version", out var said) && said is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
                return ConnectionTestResult.Ok($"Connected to RabbitMQ{(version is null ? "" : " " + version)}.");
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or AuthenticationFailureException or SocketException or TimeoutException)
            {
                return ConnectionTestResult.Fail(ex.Message); // the broker's or the network's failure; the message stays inside the runner
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PublishOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"RabbitMQ has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is dialled.
            string? NameOf(string parameter, string what) =>
                Values.Get(p, parameter) is { } given ? Name.IsMatch(given) && Encoding.UTF8.GetByteCount(given) <= 255 ? given : throw new InvalidOperationException($"'{given}' is not {what} - one line of up to 255 bytes ({parameter}).") : null;
            bool Flag(string parameter, bool fallback) =>
                Values.Get(p, parameter) is not { } given ? fallback : bool.TryParse(given, out var yes) ? yes : throw new InvalidOperationException($"'{given}' is not true or false ({parameter}).");

            var queue = NameOf("queue", "a queue's name");
            var exchange = NameOf("exchange", "an exchange's name");
            if (operation is GetOperation or CountOperation && queue is null)
            {
                throw new InvalidOperationException("Give the queue - its name.");
            }

            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= MaxMessages ? n : throw new InvalidOperationException($"'{most}' is not a number from 1 to {MaxMessages} (max).") : DefaultMax;
            var ack = Flag("ack", true);
            byte[] body = Array.Empty<byte>();
            BasicProperties? properties = null;
            var exchangeType = "fanout";
            var routingKey = "";
            var declare = Flag("declare", true);
            var durable = Flag("durable", true);
            if (operation == PublishOperation)
            {
                if ((queue is null) == (exchange is null))
                {
                    throw new InvalidOperationException(queue is null ? "Give the queue or the exchange to publish to." : "Give the queue or the exchange, not both.");
                }

                // As given, not trimmed: one line as 'message', or as many as it takes in the step's Query box.
                var message = p.TryGetValue("message", out var raw) && !string.IsNullOrEmpty(raw) ? raw : p.TryGetValue("query", out var query) && !string.IsNullOrEmpty(query) ? query : throw new InvalidOperationException("Give the message (message) - or write it in the step's Query box.");
                body = Encoding.UTF8.GetBytes(message);
                exchangeType = Values.GetOrDefault(p, "exchange_type", "fanout").ToLowerInvariant();
                if (!ExchangeTypes.Contains(exchangeType))
                {
                    throw new InvalidOperationException($"'{exchangeType}' is not fanout, direct, topic or headers (exchange_type).");
                }

                routingKey = Values.Get(p, "routing_key") ?? "";
                if (Encoding.UTF8.GetByteCount(routingKey) > 255 || routingKey.Any(char.IsControl))
                {
                    throw new InvalidOperationException($"'{routingKey}' is not a routing key - one line of up to 255 bytes (routing_key).");
                }

                properties = new BasicProperties
                {
                    ContentType = Values.Get(p, "content_type") ?? "text/plain",
                    DeliveryMode = Flag("persistent", true) ? DeliveryModes.Persistent : DeliveryModes.Transient,
                    MessageId = Values.Get(p, "message_id"),
                    CorrelationId = Values.Get(p, "correlation_id"),
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                };
                if (properties.ContentType.Any(char.IsControl) || properties.ContentType.Length > 255)
                {
                    throw new InvalidOperationException($"'{properties.ContentType}' is not a media type (content_type).");
                }

                if (Values.Get(p, "priority") is { } priority)
                {
                    properties.Priority = byte.TryParse(priority, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : throw new InvalidOperationException($"'{priority}' is not a number from 0 to 255 (priority).");
                }

                if (Values.Get(p, "expiration") is { } expiration)
                {
                    properties.Expiration = long.TryParse(expiration, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms >= 0 ? ms.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{expiration}' is not a number of milliseconds (expiration).");
                }

                if (Values.Get(p, "headers") is { } headers)
                {
                    properties.Headers = Headers(headers);
                }
            }

            var factory = Build(context.Endpoint, context.Credentials, context.Timeout, out var host, out var port);
            await PinAsync(factory, host, port, context.Egress, cancellationToken);
            await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: operation == PublishOperation, publisherConfirmationTrackingEnabled: operation == PublishOperation), cancellationToken);
            switch (operation)
            {
                case PublishOperation:
                    if (declare && queue is not null)
                    {
                        await channel.QueueDeclareAsync(queue, durable: durable, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);
                    }
                    else if (declare)
                    {
                        await channel.ExchangeDeclareAsync(exchange!, exchangeType, durable: durable, autoDelete: false, arguments: null, cancellationToken: cancellationToken);
                    }

                    // To a queue through the default exchange, by its name; to an exchange by the routing key. The broker's confirmation awaited.
                    await channel.BasicPublishAsync(exchange ?? "", queue ?? routingKey, mandatory: false, basicProperties: properties!, body: body, cancellationToken: cancellationToken);
                    yield return Fields(("queue", queue), ("exchange", exchange), ("routing_key", queue is null ? routingKey : null), ("bytes", body.Length), ("confirmed", true));
                    break;

                case CountOperation:
                    {
                        var counted = await channel.QueueDeclarePassiveAsync(queue!, cancellationToken);
                        yield return Fields(("queue", counted.QueueName), ("messages", (long)counted.MessageCount), ("consumers", (long)counted.ConsumerCount));
                        break;
                    }

                case GetOperation:
                    {
                        // Each message taken unacknowledged and made a record, then acknowledged one at a time - or, asked, left
                        // unacknowledged: the broker puts every one back when the step's channel closes, none of them taken twice in
                        // the pass (one put back at once would be the next one taken).
                        for (var read = 0; read < max; read++)
                        {
                            var got = await channel.BasicGetAsync(queue!, autoAck: false, cancellationToken);
                            if (got is null)
                            {
                                yield break;
                            }

                            var record = Message(got);
                            if (ack)
                            {
                                await channel.BasicAckAsync(got.DeliveryTag, multiple: false, cancellationToken);
                            }

                            yield return record;
                        }

                        break;
                    }
            }
        }

        /// <summary>The broker the endpoint names - one, its host and port, its virtual host, TLS for amqps - the client's own recovery off.</summary>
        internal static ConnectionFactory Build(string endpoint, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, out string host, out int port)
        {
            var given = (endpoint ?? "").Trim();
            if (given.Length == 0)
            {
                throw new ArgumentException("give the broker's address - amqp://rabbit.example.com:5672/kassa.");
            }

            if (!given.Contains("://", StringComparison.Ordinal))
            {
                given = "amqp://" + given;
            }

            if (given.Contains(',', StringComparison.Ordinal))
            {
                throw new ConnectorEgressDeniedException("A RabbitMQ connection names ONE broker; a list of brokers cannot be pinned to a checked address.");
            }
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var uri) || uri.Scheme is not ("amqp" or "amqps") || uri.Host.Length == 0)
            {
                throw new ArgumentException($"'{endpoint}' is not an AMQP address - amqp://rabbit.example.com:5672/kassa, or amqps:// for TLS.");
            }

            if (uri.UserInfo.Length > 0)
            {
                throw new ConnectorEgressDeniedException("A RabbitMQ address carries no user or password - they are the connection's credentials.");
            }

            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new ArgumentException("the address names the broker and its virtual host alone - no options after a ?.");
            }


            var tls = uri.Scheme == "amqps";
            host = uri.Host;
            port = uri.IsDefaultPort || uri.Port < 0 ? (tls ? 5671 : 5672) : uri.Port;
            var path = uri.AbsolutePath.TrimStart('/');
            var virtualHost = path.Length == 0 ? "/" : Uri.UnescapeDataString(path);
            if (virtualHost.Any(char.IsControl) || virtualHost.Length > 255)
            {
                throw new ArgumentException($"'{virtualHost}' is not a virtual host.");
            }

            var factory = new ConnectionFactory
            {
                Port = port,
                VirtualHost = virtualHost,
                UserName = Values.Get(credentials, "username") ?? ConnectionFactory.DefaultUser,
                Password = credentials.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password) ? password : ConnectionFactory.DefaultPass,
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                RequestedConnectionTimeout = timeout,
                ContinuationTimeout = timeout,
                HandshakeContinuationTimeout = timeout,
                ClientProvidedName = "Stomme",
            };
            if (tls)
            {
                factory.Ssl = new SslOption { Enabled = true, ServerName = host };
            }

            return factory;
        }

        // The broker's host resolved and checked by the egress, its address what the client dials; the name kept for TLS.
        private static async Task PinAsync(ConnectionFactory factory, string host, int port, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var pinned = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The RabbitMQ host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            factory.HostName = pinned.ToString();
        }

        // A header object's values: text, a whole number, a decimal, true or false - AMQP's own.
        private static Dictionary<string, object?> Headers(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The headers are not JSON - {\"source\": \"kassa\"}.");
            }

            if (parsed is not JsonObject headers || headers.Count > 100)
            {
                throw new InvalidOperationException("The headers are not a JSON object of up to 100 names and values - {\"source\": \"kassa\"}.");
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in headers)
            {
                result[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => Encoding.UTF8.GetBytes(s),
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => throw new InvalidOperationException($"The header '{name}' is not text, a number or true/false."),
                };
            }

            return result;
        }

        // A message taken from a queue: its body as text, where it came from, its properties and its headers as JSON.
        private static ConnectorRecord Message(BasicGetResult got)
        {
            var properties = got.BasicProperties;
            // The client's own trace of a confirmed publish (x-dotnet-pub-seq-no, RabbitMQ.Client 7's) is not the publisher's header.
            var named = (properties.Headers ?? new Dictionary<string, object?>()).Where(pair => pair.Key != PublishSequenceHeader).ToList();
            var headers = named.Count > 0 ? new JsonObject(named.Select(pair => KeyValuePair.Create(pair.Key, Header(pair.Value)))).ToJsonString(Wire) : null;
            return Fields(
                ("body", Encoding.UTF8.GetString(got.Body.Span)),
                ("exchange", got.Exchange),
                ("routing_key", got.RoutingKey),
                ("redelivered", got.Redelivered),
                ("content_type", properties.ContentType),
                ("message_id", properties.MessageId),
                ("correlation_id", properties.CorrelationId),
                ("timestamp", properties.IsTimestampPresent() ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : null),
                ("headers", headers),
                ("remaining", (long)got.MessageCount));
        }

        // A header's value as JSON: AMQP's text arrives as bytes.
        private static JsonNode? Header(object? value) => value switch
        {
            null => null,
            byte[] bytes => JsonValue.Create(Encoding.UTF8.GetString(bytes)),
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            long whole => JsonValue.Create(whole),
            int small => JsonValue.Create(small),
            double real => JsonValue.Create(real),
            decimal exact => JsonValue.Create(exact),
            _ => JsonValue.Create(value.ToString()),
        };

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
