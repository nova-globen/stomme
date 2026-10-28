#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace Stomme.Modules.Connectors.Redis
{
    /// <summary>
    /// Redis (BL-229, n8n's Redis node; Valkey and the other servers that speak its protocol too) over StackExchange.Redis:
    /// the endpoint is ONE server, <c>cache.example.com:6379</c> with the client's options (<c>ssl=true</c>), and never a
    /// password - the Basic scheme's <c>username</c> (an ACL user; empty, the default user) and <c>password</c> come from the
    /// per-site secret store; the database by the connection's setting. A test pings and names the server's version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>info</c>; <c>get</c> - a key's value by its type, a hash's fields, a list's or a set's members as JSON; <c>set</c>
    /// - a string, a hash's fields (merged), or a list or a set (replaced whole), in one transaction with its time to live;
    /// <c>delete</c>; <c>incr</c> - by one or what is given, its time to live renewed; <c>keys</c> - by a pattern, through SCAN
    /// (never KEYS, which stops the server), with their values if asked; <c>llen</c>, <c>push</c> and <c>pop</c> at either end;
    /// <c>publish</c>. Every value held to its shape before anything is dialled.
    /// </para>
    /// <para>
    /// <b>The client dials only the address the host pinned.</b> The one server's host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> and the permitted address replaces it (its name kept for TLS); and
    /// because a Redis server can name OTHER servers - a cluster's MOVED and ASK, a sentinel's primary, a replica's - every
    /// connection the client opens passes a tunnel that refuses any address but the pinned one. A list of servers, a
    /// sentinel's service, a Unix socket, an HTTP tunnel and a password in the address are refused; admin commands stay off.
    /// </para>
    /// </remarks>
    public sealed class RedisConnector : IConnector, IConnectorExtractor
    {
        public const string InfoOperation = "info";

        public const string GetOperation = "get";

        public const string SetOperation = "set";

        public const string DeleteOperation = "delete";

        public const string IncrementOperation = "incr";

        public const string KeysOperation = "keys";

        public const string LengthOperation = "llen";

        public const string PushOperation = "push";

        public const string PopOperation = "pop";

        public const string PublishOperation = "publish";

        public const int DefaultMax = 1000;

        private static readonly string[] Known = { InfoOperation, GetOperation, SetOperation, DeleteOperation, IncrementOperation, KeysOperation, LengthOperation, PushOperation, PopOperation, PublishOperation };

        private static readonly string[] Types = { "automatic", "string", "hash", "list", "set" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "redis";

        public string DisplayName => "Redis";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "cache.example.com:6379,ssl=true";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "User (an ACL user - default when the server has none)",
            ["password"] = "Password",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? WebsiteUrl => new("https://redis.io");

        public Uri? ApiDocumentationUrl => new("https://redis.io/docs/latest/commands/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Integer, Required: false, Description: "The database's number - 0 when empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField KeyField = Field("key", "Key", "The key - kassa:1042.", required: true);

        private static readonly ConnectorConfigField ListField = Field("list", "List", "The list's key - kassa:queue.", required: true);

        private static readonly ConnectorConfigField EndField = Field("end", "End", "head (the default, n8n's) or tail.") with { Default = "head" };

        private static readonly ConnectorConfigField TtlField = Field("ttl", "Time to live", "Seconds until the key expires. Empty: as it is.", type: ConnectorConfigFieldType.Integer);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(InfoOperation, "Server information", "The server's INFO, one field each - n8n's Info."),
            new ConnectorOperation(GetOperation, "Get a key", "A key's value: a string, a hash's fields, a list's or a set's members - n8n's Get.")
            {
                Parameters = Schema(KeyField, Field("type", "Type", "automatic (the default, the key's own), string, hash, list or set.") with { Default = "automatic" }),
            },
            new ConnectorOperation(SetOperation, "Set a key", "A string, a hash's fields (merged into it), or a list or a set (replacing it), with a time to live - n8n's Set.")
            {
                Parameters = Schema(KeyField, Field("value", "Value", "The value: text, or JSON - an object for a hash, an array for a list or a set.", required: true),
                    Field("type", "Type", "automatic (the default: a JSON object a hash, an array a list, anything else a string), string, hash, list or set.") with { Default = "automatic" }, TtlField),
            },
            new ConnectorOperation(DeleteOperation, "Delete a key", "A key deleted - n8n's Delete.") { Parameters = Schema(KeyField) },
            new ConnectorOperation(IncrementOperation, "Increment a key", "A number's key increased, its time to live renewed - n8n's Increment.")
            {
                Parameters = Schema(KeyField, Field("by", "By", "How much - 1 when empty.", type: ConnectorConfigFieldType.Integer) with { Default = "1" }, TtlField),
            },
            new ConnectorOperation(KeysOperation, "Find keys", "The keys a pattern matches, through SCAN, with their values if asked - n8n's Keys.")
            {
                Parameters = Schema(Field("pattern", "Pattern", "Which keys - kassa:*.", required: true), Field("values", "With values", "true to read each key's value too (n8n's default).") with { Default = "true" },
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(LengthOperation, "A list's length", "How many members a list has - n8n's List Length.") { Parameters = Schema(ListField) },
            new ConnectorOperation(PushOperation, "Push onto a list", "A value pushed at the list's head or tail - n8n's Push.")
            {
                Parameters = Schema(ListField, Field("value", "Value", "What to push.", required: true), EndField),
            },
            new ConnectorOperation(PopOperation, "Pop from a list", "The value at the list's head or tail, taken off it - n8n's Pop.") { Parameters = Schema(ListField, EndField) },
            new ConnectorOperation(PublishOperation, "Publish a message", "A message on a channel - n8n's Publish.")
            {
                Parameters = Schema(Field("channel", "Channel", "The channel - kassa:events.", required: true), Field("message", "Message", "What to publish.", required: true)),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ConfigurationOptions options;
            try
            {
                options = Build(context.Endpoint, context.Credentials, context.Timeout);
            }
            catch (ArgumentException ex)
            {
                return ConnectionTestResult.Fail($"Invalid Redis address: {ex.Message}");
            }

            var database = Database(context.Settings);
            await PinAsync(options, context.Egress, cancellationToken);
            try
            {
                await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
                var db = redis.GetDatabase(database);
                await db.PingAsync();
                var info = Info((string?)await db.ExecuteAsync("INFO", "server"));
                var version = info.TryGetValue("valkey_version", out var valkey) ? "Valkey " + valkey : info.TryGetValue("redis_version", out var redisVersion) ? "Redis " + redisVersion : "Redis";
                return ConnectionTestResult.Ok($"Connected to {version}.");
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or SocketException)
            {
                return ConnectionTestResult.Fail(ex.Message); // the server's or the network's failure; the message stays inside the runner (D110)
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? InfoOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Redis has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is dialled.
            string Required(string name, string what) => Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what} ({name}).");
            string Raw(string name, string what) => p.TryGetValue(name, out var given) && !string.IsNullOrEmpty(given) ? given : throw new InvalidOperationException($"Give {what} ({name})."); // as given, not trimmed
            var key = operation is GetOperation or SetOperation or DeleteOperation or IncrementOperation ? Required("key", "the key") : null;
            var list = operation is LengthOperation or PushOperation or PopOperation ? Required("list", "the list's key") : null;
            var type = Values.GetOrDefault(p, "type", "automatic").ToLowerInvariant();
            if (!Types.Contains(type))
            {
                throw new InvalidOperationException($"'{type}' is not automatic, string, hash, list or set (type).");
            }

            var ttl = Values.Get(p, "ttl") is { } seconds
                ? int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : throw new InvalidOperationException($"'{seconds}' is not a number of seconds above 0 (ttl).")
                : (TimeSpan?)null;
            var end = Values.GetOrDefault(p, "end", "head").ToLowerInvariant();
            if (end is not ("head" or "tail"))
            {
                throw new InvalidOperationException($"'{end}' is not head or tail (end).");
            }

            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var by = Values.Get(p, "by") is { } step ? long.TryParse(step, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b) ? b : throw new InvalidOperationException($"'{step}' is not a whole number (by).") : 1L;
            var withValues = Values.Get(p, "values") is not { } flag || (bool.TryParse(flag, out var yes) ? yes : throw new InvalidOperationException($"'{flag}' is not true or false (values)."));
            var pattern = operation == KeysOperation ? Required("pattern", "the pattern - kassa:*") : null;
            var value = operation is SetOperation or PushOperation ? Raw("value", "the value") : null;
            var channel = operation == PublishOperation ? Required("channel", "the channel") : null;
            var message = operation == PublishOperation ? Raw("message", "the message") : null;
            var write = operation == SetOperation ? Shape(value!, type) : default;

            var options = Build(context.Endpoint, context.Credentials, context.Timeout);
            var database = Database(context.Settings);
            await PinAsync(options, context.Egress, cancellationToken);
            await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
            var db = redis.GetDatabase(database);
            switch (operation)
            {
                case InfoOperation:
                    yield return new ConnectorRecord(Info((string?)await db.ExecuteAsync("INFO")).ToDictionary(pair => pair.Key, pair => (object?)Parsed(pair.Value), StringComparer.Ordinal));
                    break;
                case GetOperation:
                    {
                        var (actual, found) = await ValueOfAsync(db, key!, type);
                        yield return Fields(("key", key), ("type", actual), ("value", found));
                        break;
                    }

                case SetOperation:
                    {
                        // One transaction, its commands queued in order: a list's or a set's write replaces it whole, the key
                        // deleted first.
                        var transaction = db.CreateTransaction();
                        var members = write.Members?.Select(member => (RedisValue)member.Value).ToArray() ?? Array.Empty<RedisValue>();
                        switch (write.Type)
                        {
                            case "string" when ttl is { } lives:
                                _ = transaction.StringSetAsync(key, write.Text, lives);
                                break;
                            case "string":
                                _ = transaction.StringSetAsync(key, write.Text);
                                break;
                            case "hash":
                                _ = transaction.HashSetAsync(key, write.Members!.Select(member => new HashEntry(member.Name, member.Value)).ToArray());
                                break;
                            case "list":
                                _ = transaction.KeyDeleteAsync(key);
                                _ = transaction.ListRightPushAsync(key, members);
                                break;
                            default:
                                _ = transaction.KeyDeleteAsync(key);
                                _ = transaction.SetAddAsync(key, members);
                                break;
                        }

                        if (ttl is { } expiry && write.Type != "string")
                        {
                            _ = transaction.KeyExpireAsync(key, expiry);
                        }

                        yield return Fields(("key", key), ("type", write.Type), ("set", await transaction.ExecuteAsync()));
                        break;
                    }

                case DeleteOperation:
                    yield return Fields(("key", key), ("deleted", await db.KeyDeleteAsync(key)));
                    break;
                case IncrementOperation:
                    {
                        var now = await db.StringIncrementAsync(key, by);
                        if (ttl is { } expiry)
                        {
                            await db.KeyExpireAsync(key, expiry);
                        }

                        yield return Fields(("key", key), ("value", now));
                        break;
                    }

                case KeysOperation:
                    {
                        var server = redis.GetServers().Single();
                        var read = 0;
                        await foreach (var found in server.KeysAsync(database, pattern, pageSize: 250).WithCancellation(cancellationToken))
                        {
                            if (read++ == max)
                            {
                                yield break;
                            }

                            if (!withValues)
                            {
                                yield return Fields(("key", (string?)found));
                                continue;
                            }

                            var (actual, its) = await ValueOfAsync(db, found!, "automatic");
                            yield return Fields(("key", (string?)found), ("type", actual), ("value", its));
                        }

                        break;
                    }

                case LengthOperation:
                    yield return Fields(("list", list), ("length", await db.ListLengthAsync(list)));
                    break;
                case PushOperation:
                    yield return Fields(("list", list), ("length", end == "tail" ? await db.ListRightPushAsync(list, value) : await db.ListLeftPushAsync(list, value)));
                    break;
                case PopOperation:
                    yield return Fields(("list", list), ("value", (string?)(end == "tail" ? await db.ListRightPopAsync(list) : await db.ListLeftPopAsync(list))));
                    break;
                default:
                    yield return Fields(("channel", channel), ("receivers", await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(channel!), message)));
                    break;
            }
        }

        /// <summary>A set's value as the type it is written as: text for a string; a JSON object's members for a hash; a JSON array's for a list or a set.</summary>
        internal static (string Type, string Text, List<(string Name, string Value)>? Members) Shape(string value, string type)
        {
            JsonNode? json = null;
            if (type != "string")
            {
                try
                {
                    json = JsonNode.Parse(value);
                }
                catch (JsonException) when (type == "automatic")
                {
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException($"A {type}'s value is JSON - {(type == "hash" ? "an object, {\"kund\": \"Ada\"}" : "an array, [\"a\", \"b\"]")} (value).");
                }
            }

            static string Text(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : node?.ToJsonString(Wire) ?? "";
            var shaped = type == "automatic" ? json switch { JsonObject => "hash", JsonArray => "list", _ => "string" } : type;
            return shaped switch
            {
                "string" => ("string", value, null),
                "hash" when json is JsonObject fields && fields.Count > 0 => ("hash", value, fields.Select(field => (field.Key, Text(field.Value))).ToList()),
                "hash" => throw new InvalidOperationException("A hash's value is a JSON object with a field at least - {\"kund\": \"Ada\"} (value)."),
                _ when json is JsonArray members && members.Count > 0 => (shaped, value, members.Select(member => ("", Text(member))).ToList()),
                _ => throw new InvalidOperationException($"A {shaped}'s value is a JSON array with a member at least - [\"a\", \"b\"] (value)."),
            };
        }

        /// <summary>The address held to one server and no secret; the scheme's credentials applied; admin commands off; the timeout clamped.</summary>
        internal static ConfigurationOptions Build(string endpoint, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("it names no server - cache.example.com:6379.");
            }

            var options = ConfigurationOptions.Parse(endpoint);
            if (!string.IsNullOrEmpty(options.Password) || !string.IsNullOrEmpty(options.User))
            {
                throw new ArgumentException("a password or a user is never part of the address - give them as the connection's credentials.");
            }

            if (options.EndPoints.Count == 0)
            {
                throw new ArgumentException("it names no server - cache.example.com:6379.");
            }

            if (options.EndPoints.Count > 1)
            {
                throw new ConnectorEgressDeniedException("A Redis connection names ONE server; a list of servers cannot be pinned to a checked address.");
            }

            if (!string.IsNullOrEmpty(options.ServiceName))
            {
                throw new ConnectorEgressDeniedException("A Redis connection does not follow a sentinel's service - it would dial servers this site's egress policy never checked.");
            }

            if (options.Tunnel is not null)
            {
                throw new ConnectorEgressDeniedException("A Redis connection is dialled by the host, not through a tunnel the address names.");
            }

            if (options.EndPoints[0] is not (DnsEndPoint or IPEndPoint))
            {
                throw new ConnectorEgressDeniedException("A Redis connection dials a host over TCP; a Unix socket is a local endpoint this site's egress policy cannot check.");
            }

            options.SetDefaultPorts();
            if (credentials.TryGetValue("username", out var user) && !string.IsNullOrWhiteSpace(user))
            {
                options.User = user.Trim();
            }

            if (credentials.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password))
            {
                options.Password = password;
            }

            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1000, 120_000);
            options.ConnectTimeout = milliseconds;
            options.SyncTimeout = milliseconds;
            options.AsyncTimeout = milliseconds;
            options.ConnectRetry = 1;
            options.AbortOnConnectFail = true;
            options.AllowAdmin = false;
            options.ClientName ??= "Stomme";
            return options;
        }

        private static int Database(IReadOnlyDictionary<string, string> settings) =>
            Values.Get(settings, "database") is not { } given ? 0
            : int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < 10_000 ? number
            : throw new InvalidOperationException($"'{given}' is not a database's number (database).");

        /// <summary>Resolves, checks and pins the one server; every connection the client opens then passes a tunnel that dials that address alone.</summary>
        internal static async Task PinAsync(ConfigurationOptions options, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var (host, port) = options.EndPoints[0] switch
            {
                DnsEndPoint named => (named.Host, named.Port),
                IPEndPoint address => (address.Address.ToString(), address.Port),
                _ => throw new ConnectorEgressDeniedException("A Redis connection dials a host over TCP."),
            };
            var pinned = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The Redis host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var only = new IPEndPoint(pinned, port);
            options.EndPoints.Clear();
            options.EndPoints.Add(only);
            if (options.Ssl && string.IsNullOrEmpty(options.SslHost) && !IPAddress.TryParse(host, out _))
            {
                options.SslHost = host; // the certificate names the host, the socket dials the pinned address
            }

            options.Tunnel = new PinnedTunnel(only);
        }

        /// <summary>Every connection the client opens: the pinned address passes, any other - a server's MOVED, ASK, primary or replica - is refused.</summary>
        internal sealed class PinnedTunnel(IPEndPoint pinned) : Tunnel
        {
            public override ValueTask<EndPoint?> GetSocketConnectEndpointAsync(EndPoint endpoint, CancellationToken cancellationToken) =>
                endpoint is IPEndPoint address && address.Port == pinned.Port && address.Address.MapToIPv6().Equals(pinned.Address.MapToIPv6())
                    ? new ValueTask<EndPoint?>(endpoint)
                    : throw new ConnectorEgressDeniedException($"The Redis server named another server ({endpoint}) - only the address this site's egress policy checked is dialled.");
        }

        private static async Task<(string Type, string? Value)> ValueOfAsync(IDatabase db, RedisKey key, string type)
        {
            var actual = type != "automatic" ? type : await db.KeyTypeAsync(key) switch
            {
                RedisType.String => "string",
                RedisType.Hash => "hash",
                RedisType.List => "list",
                RedisType.Set => "set",
                RedisType.SortedSet => "zset",
                RedisType.None => "none",
                var other => other.ToString().ToLowerInvariant(),
            };
            return actual switch
            {
                "string" => ("string", (string?)await db.StringGetAsync(key)),
                "hash" => ("hash", new JsonObject((await db.HashGetAllAsync(key)).Select(entry => KeyValuePair.Create((string)entry.Name!, (JsonNode?)JsonValue.Create((string?)entry.Value)))).ToJsonString(Wire)),
                "list" => ("list", new JsonArray((await db.ListRangeAsync(key)).Select(member => (JsonNode?)JsonValue.Create((string?)member)).ToArray()).ToJsonString(Wire)),
                "set" => ("set", new JsonArray((await db.SetMembersAsync(key)).Select(member => (string?)member).OrderBy(member => member, StringComparer.Ordinal).Select(member => (JsonNode?)JsonValue.Create(member)).ToArray()).ToJsonString(Wire)),
                "zset" => ("zset", new JsonObject((await db.SortedSetRangeByRankWithScoresAsync(key)).Select(entry => KeyValuePair.Create((string)entry.Element!, (JsonNode?)JsonValue.Create(entry.Score)))).ToJsonString(Wire)),
                _ => (actual, null),
            };
        }

        /// <summary>INFO's lines as fields: its section headers and blank lines left out, each 'name:value' a field.</summary>
        internal static Dictionary<string, string> Info(string? raw)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (raw ?? "").Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0 && !line.StartsWith('#')))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    fields[line[..colon]] = line[(colon + 1)..].Trim();
                }
            }

            return fields;
        }

        // A field of INFO: a number as one; 'a=1,b=2' as a JSON object of its pairs (n8n's); anything else as text.
        private static object Parsed(string value)
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            {
                return whole;
            }

            if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact) && value.Contains('.'))
            {
                return exact;
            }

            if (value.Contains('=') && value.Split(',').All(pair => pair.Contains('=')))
            {
                // A name can repeat (a listener's bind=*,bind=-::*): its values kept together, space-separated.
                var pairs = new JsonObject();
                foreach (var pair in value.Split(',').Select(pair => pair.Split('=', 2)))
                {
                    pairs[pair[0]] = pairs[pair[0]] is JsonValue before ? before.GetValue<string>() + " " + pair[1] : pair[1];
                }

                return pairs.ToJsonString(Wire);
            }

            return value;
        }

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
