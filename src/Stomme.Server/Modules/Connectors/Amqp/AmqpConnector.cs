#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amqp;
using Amqp.Framing;
using Amqp.Handler;
using Amqp.Sasl;
using AmqpOpen = Amqp.Framing.Open;
using Amqp.Types;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Amqp
{
    /// <summary>
    /// AMQP 1.0 (BL-229, n8n's AMQP Sender and AMQP Trigger nodes) over AMQPNetLite: the endpoint is ONE broker,
    /// <c>amqp://broker.kafe.example:5672</c> (<c>amqps://</c> for TLS), never a user or a password - the Basic scheme's
    /// <c>username</c> and <c>password</c> come from the per-site secret store, as SASL PLAIN (none: SASL ANONYMOUS). A test
    /// opens a connection and names the broker as it names itself.
    /// </summary>
    /// <remarks>
    /// <para><c>send</c> - a message to a queue, a topic or an exchange address, its headers as application properties, its
    /// content type, subject, ids, durability, priority and time to live - and the broker's outcome AWAITED: n8n's sender sends
    /// and returns without it, which measured against RabbitMQ 4.1 says "sent" for a message to a queue the broker has not got
    /// (it rejects it, amqp:not-found) and for one an exchange routes nowhere (it releases it), so here a message the broker did
    /// not accept is the step's failure, in the broker's words. <c>receive</c> - up to <c>max</c> messages from a queue,
    /// waiting at most <c>wait</c> seconds for the first, each accepted - or, asked, released back - its body, properties and
    /// application properties a record, as n8n's trigger takes them.</para>
    /// <para><b>The client dials only the address the host pinned</b>: the broker's host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> and the permitted address is what the client connects to; for TLS the
    /// handshake checks the broker's certificate against its NAME, never the address.</para>
    /// </remarks>
    public sealed class AmqpConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1; // n8n's trigger: a message an execution

        private const int MostMax = 1000;

        private const int MostWait = 60;

        private const int MostBody = 1048576;

        public string ConnectorType => "amqp";

        public string DisplayName => "AMQP 1.0";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "amqp://broker.kafe.example:5672";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "User",
            ["password"] = "Password",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging, ConnectorCategory.Development };

        public Uri? WebsiteUrl => new("https://www.amqp.org");

        public Uri? ApiDocumentationUrl => new("https://docs.oasis-open.org/amqp/core/v1.0/amqp-core-overview-v1.0.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("send", "Send a message", "n8n's AMQP Sender: a message to a queue, topic or exchange address - the broker's acceptance awaited.")
            {
                Parameters = new(new[]
                {
                    Field("to", "Queue / topic", "Its address as the broker names it - orders, /queues/orders, /exchanges/amq.topic/kassa.paid (RabbitMQ), queue://orders (ActiveMQ).", required: true),
                    Field("message", "Message", "The message - text, or JSON as text; one over several lines in the step's Query box.", type: ConnectorConfigFieldType.Multiline),
                    Field("headers", "Headers", "A JSON object of names and values, sent as its application properties - {\"source\": \"kassa\"}."),
                    Field("content_type", "Content type", "Its media type - application/json."),
                    Field("body", "Body", "value (n8n's - an AMQP string) or data (its bytes, as most other clients read them)."),
                    Field("subject", "Subject", "Its subject."),
                    Field("message_id", "Message id", "Its id."),
                    Field("correlation_id", "Correlation id", "The id of what it answers."),
                    Field("durable", "Durable", "Kept by the broker through a restart (the default).", type: ConnectorConfigFieldType.Boolean) with { Default = "true" },
                    Field("priority", "Priority", "0 to 255.", type: ConnectorConfigFieldType.Integer),
                    Field("ttl", "Expires after", "Milliseconds until the broker drops it unread.", type: ConnectorConfigFieldType.Integer),
                }),
            },
            new ConnectorOperation("receive", "Receive messages", "n8n's AMQP Trigger, as a step: up to max messages from a queue, each accepted - or released back.")
            {
                Parameters = new(new[]
                {
                    Field("from", "Queue / topic", "Its address as the broker names it - orders, /queues/orders.", required: true),
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    Field("wait", "Wait", "Seconds to wait for the first - 5 by default, at most 60.", type: ConnectorConfigFieldType.Integer) with { Default = "5" },
                    Field("accept", "Accept", "true (the default) takes them; false releases them back to the broker.", type: ConnectorConfigFieldType.Boolean) with { Default = "true" },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            Broker broker;
            try
            {
                broker = Broker.Of(context.Endpoint, context.Credentials);
            }
            catch (ArgumentException ex)
            {
                return ConnectionTestResult.Fail($"Invalid AMQP address: {ex.Message}");
            }

            try
            {
                await using var connection = await broker.OpenAsync(context.Egress, context.Timeout, cancellationToken);
                return ConnectionTestResult.Ok($"Connected to {connection.Name}.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = (context.OperationName ?? "receive").Trim().ToLowerInvariant();
            var p = context.Parameters;
            Broker broker;
            try
            {
                broker = Broker.Of(context.Endpoint, context.Credentials);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid AMQP address: {ex.Message}");
            }

            switch (operation)
            {
                case "send":
                    {
                        var to = Target(p, "to");
                        var message = Build(p);
                        await using var connection = await broker.OpenAsync(context.Egress, context.Timeout, cancellationToken);
                        yield return await connection.SendAsync(to, message, cancellationToken);
                        break;
                    }

                case "receive":
                    {
                        var from = Target(p, "from");
                        var max = Number(p, "max", DefaultMax, 1, MostMax);
                        var wait = Number(p, "wait", 5, 0, MostWait);
                        var accept = Flag(p, "accept") ?? true;
                        await using var connection = await broker.OpenAsync(context.Egress, context.Timeout, cancellationToken);
                        foreach (var record in await connection.ReceiveAsync(from, max, TimeSpan.FromSeconds(wait), accept, cancellationToken))
                        {
                            yield return record;
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException($"AMQP has no operation '{context.OperationName}' - send or receive.");
            }
        }

        // The message as given: its body as n8n's AMQP string or as bytes, its properties and application properties checked.
        private static Message Build(IReadOnlyDictionary<string, string> p)
        {
            var text = Values.Get(p, "message") ?? Values.Get(p, "query") ?? throw new InvalidOperationException("Give the message (message).");
            if (Encoding.UTF8.GetByteCount(text) > MostBody)
            {
                throw new InvalidOperationException("A message is at most a megabyte (message).");
            }

            var message = (Values.Get(p, "body")?.Trim().ToLowerInvariant() ?? "value") switch
            {
                "value" => new Message(text),
                "data" => new Message { BodySection = new Data { Binary = Encoding.UTF8.GetBytes(text) } },
                var other => throw new InvalidOperationException($"'{other}' is not value or data (body)."),
            };
            message.Properties = new Properties();
            if (Values.Get(p, "content_type") is { } contentType)
            {
                message.Properties.ContentType = MediaType(contentType);
            }

            foreach (var (name, set) in new (string, Action<string>)[] { ("subject", value => message.Properties.Subject = value), ("message_id", value => message.Properties.MessageId = value), ("correlation_id", value => message.Properties.CorrelationId = value) })
            {
                if (Values.Get(p, name) is { } given)
                {
                    set(OneLine(given, name));
                }
            }

            message.Header = new Header { Durable = Flag(p, "durable") ?? true };
            if (Values.Get(p, "priority") is not null)
            {
                message.Header.Priority = (byte)Number(p, "priority", 0, 0, 255);
            }

            if (Values.Get(p, "ttl") is not null)
            {
                message.Header.Ttl = (uint)Number(p, "ttl", 0, 1, int.MaxValue);
            }

            if (Values.Get(p, "headers") is { } headers)
            {
                message.ApplicationProperties = new ApplicationProperties();
                foreach (var (name, value) in Headers(headers))
                {
                    message.ApplicationProperties[name] = value;
                }
            }

            return message;
        }

        // Application properties: AMQP allows only simple values there - text, a whole number, a decimal, true or false.
        private static IEnumerable<(string Name, object Value)> Headers(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The headers are not JSON - {\"source\": \"kassa\"} (headers).");
            }

            foreach (var (name, node) in parsed as JsonObject ?? throw new InvalidOperationException("The headers are a JSON object - {\"source\": \"kassa\"} (headers)."))
            {
                if (name.Length is 0 or > 200)
                {
                    throw new InvalidOperationException("A header's name is 1 to 200 characters (headers).");
                }

                yield return (name, node switch
                {
                    JsonValue value when value.TryGetValue<string>(out var text) => text,
                    JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                    JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                    JsonValue value when value.TryGetValue<double>(out var number) => number,
                    _ => throw new InvalidOperationException($"The header '{name}' is not text, a number or true or false - AMQP's application properties hold only those (headers)."),
                });
            }
        }

        private static string Target(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given && given.Trim() is { Length: > 0 and <= 1024 } address && !address.Any(char.IsControl)
                ? address
                : throw new InvalidOperationException($"Give the queue or topic - its address as the broker names it, orders ({name}).");

        private static string OneLine(string value, string name) =>
            value.Length <= 1024 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"A {name.Replace('_', ' ')} is one line ({name}).");

        private static string MediaType(string value) =>
            System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value.Trim(), out var parsed) ? parsed.ToString() : throw new InvalidOperationException($"'{value}' is not a media type - application/json (content_type).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static int Number(IReadOnlyDictionary<string, string> p, string name, int fallback, int least, int most) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most ? n : throw new InvalidOperationException($"'{given}' is not a number from {least} to {most} ({name}).")
                : fallback;

        /// <summary>The broker the endpoint names - one, its host and port, TLS for amqps - and the connection's credentials.</summary>
        private sealed record Broker(string Host, int Port, bool Tls, string? User, string? Password)
        {
            public static Broker Of(string endpoint, IReadOnlyDictionary<string, string> credentials)
            {
                var given = (endpoint ?? string.Empty).Trim();
                if (!Uri.TryCreate(given, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps") || uri.Host.Length == 0)
                {
                    throw new ArgumentException($"'{endpoint}' is not an AMQP address - amqp://broker.kafe.example:5672, or amqps:// for TLS.");
                }

                if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath is not ("" or "/"))
                {
                    throw new ArgumentException($"'{endpoint}' names more than a broker - its user and password are the connection's credentials, its queue the step's.");
                }

                var tls = uri.Scheme == "amqps";
                return new Broker(uri.IdnHost, uri.IsDefaultPort || uri.Port <= 0 ? (tls ? 5671 : 5672) : uri.Port, tls, Values.Get(credentials, "username"), Values.Get(credentials, "password"));
            }

            public async Task<BrokerConnection> OpenAsync(IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var pinned = await egress.ResolveAndPinAsync(Host, Port, cancellationToken)
                    ?? throw new ConnectorEgressDeniedException($"The AMQP host '{Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
                await egress.ReachAsync(pinned, Port, Host, timeout, "the AMQP broker", cancellationToken);

                // The pinned address is what is dialled; SASL PLAIN with the connection's user, or ANONYMOUS without one.
                var address = User is null
                    ? new Address(pinned.ToString(), Port, null, null, "/", Tls ? "amqps" : "amqp")
                    : new Address(pinned.ToString(), Port, User, Password ?? string.Empty, "/", Tls ? "amqps" : "amqp");
                var factory = new ConnectionFactory();
                if (User is null)
                {
                    factory.SASL.Profile = SaslProfile.Anonymous;
                }

                var handler = new OpenHandler(Tls ? Host : null);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                Connection connection;
                try
                {
                    connection = await factory.CreateAsync(address, limit.Token, handler);
                }
                catch (AmqpException ex)
                {
                    throw new InvalidOperationException(ex.Error?.Condition == ErrorCode.UnauthorizedAccess || ex.Message.Contains("Sasl", StringComparison.OrdinalIgnoreCase)
                        ? $"The AMQP broker refused the sign-in - check the user and password ({ex.Error?.Description ?? ex.Message})."
                        : $"The AMQP broker refused the connection: {ex.Error?.Description ?? ex.Message}.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"The AMQP broker at {Host}:{Port} did not answer within {(int)timeout.TotalSeconds} s.");
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    throw new InvalidOperationException($"The AMQP broker at {Host}:{Port} could not be reached: {ex.Message}");
                }
                catch (AuthenticationException ex)
                {
                    throw new InvalidOperationException($"The AMQP broker at {Host}:{Port} failed the TLS handshake: {ex.Message}");
                }

                return new BrokerConnection(connection, timeout, handler.Remote);
            }
        }

        // The broker's open frame kept, to name it; and for TLS the broker's certificate checked against its NAME, though the
        // address dialled is the pinned one.
        private sealed class OpenHandler(string? tlsHost) : IHandler
        {
            public AmqpOpen? Remote { get; private set; }

            public bool CanHandle(EventId id) => id == EventId.ConnectionRemoteOpen || (id == EventId.SslAuthenticate && tlsHost is not null);

            public void Handle(Event protocolEvent)
            {
                if (protocolEvent.Id == EventId.ConnectionRemoteOpen)
                {
                    Remote = protocolEvent.Context as AmqpOpen;
                }
                else if (protocolEvent.Context is SslStream stream)
                {
                    stream.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = tlsHost, EnabledSslProtocols = SslProtocols.None, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online });
                }
            }
        }

        /// <summary>One connection to the broker, a session on it, and the links a step opens.</summary>
        private sealed class BrokerConnection : IAsyncDisposable
        {
            private readonly Connection connection;
            private readonly Session session;
            private readonly TimeSpan timeout;
            private readonly AmqpOpen? remote;

            public BrokerConnection(Connection connection, TimeSpan timeout, AmqpOpen? remote)
            {
                this.connection = connection;
                this.timeout = timeout;
                this.remote = remote;
                session = new Session(connection);
            }

            /// <summary>The broker as it names itself in its open frame - RabbitMQ 4.1.0 - else its container's id.</summary>
            public string Name
            {
                get
                {
                    var properties = remote?.Properties;
                    string? Said(string key) => properties is not null && properties.TryGetValue(new Symbol(key), out var value) ? value?.ToString() : null;
                    return Said("product") is { } product ? (product + (Said("version") is { } version ? " " + version : string.Empty)) : remote?.ContainerId ?? "the broker";
                }
            }

            public async Task<ConnectorRecord> SendAsync(string to, Message message, CancellationToken cancellationToken)
            {
                var sender = new SenderLink(session, "stomme-send-" + Guid.NewGuid().ToString("N")[..8], to);
                try
                {
                    var outcome = new TaskCompletionSource<Outcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    sender.Send(message, (_, _, settled, _) => outcome.TrySetResult(settled), null);
                    var done = await Task.WhenAny(outcome.Task, Task.Delay(timeout, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (done != outcome.Task)
                    {
                        throw new InvalidOperationException($"The AMQP broker settled nothing within {(int)timeout.TotalSeconds} s{Refusal(sender.Error)}.");
                    }

                    // n8n's sender returns without the outcome: here a message the broker did not accept is a failure, in its words.
                    return await outcome.Task switch
                    {
                        Accepted => new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = to, ["accepted"] = true, ["message_id"] = message.Properties?.MessageId }),
                        Rejected rejected => throw new InvalidOperationException($"The AMQP broker rejected the message{Refusal(rejected.Error)}."),
                        Released => throw new InvalidOperationException($"The AMQP broker released the message - nothing took it: an exchange's routing key bound to no queue, or a full queue ({to})."),
                        Modified => throw new InvalidOperationException("The AMQP broker did not deliver the message (modified) - it may try again."),
                        var other => throw new InvalidOperationException($"The AMQP broker answered {other?.GetType().Name ?? "nothing"}{Refusal(sender.Error)}."),
                    };
                }
                finally
                {
                    await CloseQuietlyAsync(sender);
                }
            }

            public async Task<List<ConnectorRecord>> ReceiveAsync(string from, int max, TimeSpan wait, bool accept, CancellationToken cancellationToken)
            {
                var receiver = new ReceiverLink(session, "stomme-receive-" + Guid.NewGuid().ToString("N")[..8], from);
                var records = new List<ConnectorRecord>();
                try
                {
                    receiver.SetCredit(max, autoRestore: false);
                    for (var i = 0; i < max; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Message? message;
                        try
                        {
                            // AMQPNetLite's ReceiveAsync with a zero wait waits forever (measured; its documentation says it returns at once).
                            message = await receiver.ReceiveAsync(i == 0 ? (wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50)) : TimeSpan.FromMilliseconds(200));
                        }
                        catch (AmqpException ex)
                        {
                            throw new InvalidOperationException($"The AMQP broker refused to give from {from}{Refusal(ex.Error)}.");
                        }

                        if (message is null)
                        {
                            if (receiver.Error is { } error)
                            {
                                throw new InvalidOperationException($"The AMQP broker refused to give from {from}{Refusal(error)}.");
                            }

                            break;
                        }

                        records.Add(Record(message));
                        if (accept)
                        {
                            receiver.Accept(message);
                        }
                        else
                        {
                            receiver.Release(message);
                        }
                    }

                    if (records.Count == 0 && receiver.Error is { } refused)
                    {
                        throw new InvalidOperationException($"The AMQP broker refused to give from {from}{Refusal(refused)}.");
                    }

                    return records;
                }
                finally
                {
                    await CloseQuietlyAsync(receiver);
                }
            }

            private static ConnectorRecord Record(Message message)
            {
                var body = message.BodySection switch
                {
                    Data data => Encoding.UTF8.GetString(data.Binary ?? Array.Empty<byte>()),
                    AmqpValue value => value.Value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), null => null, var other => Convert.ToString(other, CultureInfo.InvariantCulture) },
                    null => null,
                    var other => other.ToString(),
                };
                var properties = new JsonObject();
                foreach (var key in message.ApplicationProperties?.Map.Keys ?? Enumerable.Empty<object>())
                {
                    var value = message.ApplicationProperties!.Map[key];
                    properties[key.ToString()!] = value switch
                    {
                        string text => JsonValue.Create(text),
                        bool flag => JsonValue.Create(flag),
                        long or int or short or sbyte or ulong or uint or ushort or byte => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                        double or float or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                        null => null,
                        var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                    };
                }

                return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["body"] = body,
                    ["content_type"] = message.Properties?.ContentType is { } type && type.ToString() is { Length: > 0 } said ? said : null,
                    ["subject"] = message.Properties?.Subject,
                    ["message_id"] = message.Properties?.MessageId,
                    ["correlation_id"] = message.Properties?.CorrelationId,
                    ["properties"] = properties.ToJsonString(),
                    ["durable"] = message.Header?.Durable ?? false,
                    ["priority"] = (long)(message.Header?.Priority ?? 4),
                    ["delivery_count"] = (long)(message.Header?.DeliveryCount ?? 0),
                });
            }

            // A close the broker does not answer is not waited for longer than this.
            private static readonly TimeSpan Closing = TimeSpan.FromSeconds(5);

            private static string Refusal(Error? error) => error is null ? string.Empty : $": {error.Description ?? error.Condition.ToString()} ({error.Condition})";

            private static async Task CloseQuietlyAsync(global::Amqp.Link link)
            {
                try
                {
                    await link.CloseAsync(Closing, null);
                }
                catch (Exception ex) when (ex is AmqpException or TimeoutException)
                {
                    // a link the broker detached is closed already; one it does not answer is let go
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await session.CloseAsync(Closing, null);
                    await connection.CloseAsync(Closing, null);
                }
                catch (Exception ex) when (ex is AmqpException or TimeoutException)
                {
                    // a connection the broker ended is closed already; one it does not answer is let go
                }
            }
        }
    }
}
