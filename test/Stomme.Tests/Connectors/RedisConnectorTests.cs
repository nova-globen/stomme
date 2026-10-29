#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Redis connector without a server: an address that names no server, or a password, refused as invalid; a
    /// list of servers, a sentinel's service and a tunnel refused as the egress's denial - each before any pin; the host pinned
    /// on its port and Redis's own 6379, a refused host the guard's own denial; the pinned tunnel letting the pinned address
    /// alone through; the scheme's credentials applied and admin commands off; a set's value shaped by its type; every
    /// parameter held to its shape before anything is dialled.
    /// </summary>
    public sealed class RedisConnectorTests
    {
        private static readonly Dictionary<string, string> Password = new() { ["password"] = "pw-1" };

        [Fact]
        public async Task An_address_that_names_no_server_or_a_password_is_invalid_and_what_reaches_past_the_egress_is_denied()
        {
            var egress = new StubEgress();
            foreach (var (endpoint, said) in new[] { ("", "it names no server"), ("cache.internal:6379,password=pw", "a password or a user is never part of the address"), ("cache.internal:6379,user=ada", "a password or a user is never part of the address") })
            {
                var result = await new RedisConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Password));
                Assert.False(result.Success);
                Assert.StartsWith("Invalid Redis address: " + said, result.Message, StringComparison.Ordinal);
            }

            foreach (var (endpoint, refusal) in new[]
            {
                ("a.internal:6379,b.internal:6379", "names ONE server"),
                ("sentinel.internal:26379,serviceName=kassa", "does not follow a sentinel's service"),
                ("cache.internal:6379,tunnel=http:proxy.internal:8080", "not through a tunnel the address names"),
            })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RedisConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Password)));
                Assert.Contains(refusal, denied.Message, StringComparison.Ordinal);
                Assert.True(OutboundConnectGuard.IsDenial(denied)); // the runner says DeniedByPolicy
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_through_the_egress_and_a_refused_host_is_the_guards_denial()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RedisConnector().TestConnectionAsync(Contexts.Test(refused, "cache.internal:6380", credentials: Password)));
            Assert.Contains("cache.internal", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("cache.internal", 6380), refused.Pins.Single());

            var standard = new StubEgress { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RedisConnector().TestConnectionAsync(Contexts.Test(standard, "cache.internal", credentials: Password)));
            Assert.Equal(6379, standard.Pins.Single().Port); // Redis's own port when the address names none

            var closed = await new RedisConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "cache.internal:1", credentials: Password, timeoutSeconds: 2));
            Assert.False(closed.Success); // the pinned loopback's closed port: the client's failure, said, not thrown
        }

        [Fact]
        public async Task The_pinned_tunnel_lets_the_pinned_address_alone_through()
        {
            var tunnel = new RedisConnector.PinnedTunnel(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 6379));
            Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 6379), await tunnel.GetSocketConnectEndpointAsync(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 6379), CancellationToken.None));
            Assert.NotNull(await tunnel.GetSocketConnectEndpointAsync(new IPEndPoint(IPAddress.Parse("10.0.0.5").MapToIPv6(), 6379), CancellationToken.None)); // the same address, as IPv6
            foreach (var other in new EndPoint[] { new IPEndPoint(IPAddress.Parse("10.0.0.6"), 6379), new IPEndPoint(IPAddress.Parse("10.0.0.5"), 6380), new DnsEndPoint("replica.internal", 6379) })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(async () => await tunnel.GetSocketConnectEndpointAsync(other, CancellationToken.None));
                Assert.Contains("named another server", denied.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Pinning_writes_the_address_keeps_the_name_for_TLS_and_puts_every_connection_through_the_tunnel()
        {
            var named = RedisConnector.Build("cache.internal:6380,ssl=true", Password, TimeSpan.FromSeconds(5));
            await RedisConnector.PinAsync(named, new StubEgress { Pinned = IPAddress.Parse("10.0.0.5") }, CancellationToken.None);
            Assert.Equal(new EndPoint[] { new IPEndPoint(IPAddress.Parse("10.0.0.5"), 6380) }, named.EndPoints.ToArray());
            Assert.Equal("cache.internal", named.SslHost); // the certificate names the host, the socket dials the address
            var tunnel = Assert.IsType<RedisConnector.PinnedTunnel>(named.Tunnel);
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(async () => await tunnel.GetSocketConnectEndpointAsync(new IPEndPoint(IPAddress.Parse("10.0.0.9"), 6380), CancellationToken.None));

            var address = RedisConnector.Build("10.0.0.7:6379,ssl=true", Password, TimeSpan.FromSeconds(5));
            await RedisConnector.PinAsync(address, new StubEgress { Pinned = IPAddress.Parse("10.0.0.7") }, CancellationToken.None);
            Assert.True(string.IsNullOrEmpty(address.SslHost)); // an address names no host to check a certificate against
        }

        [Fact]
        public void The_schemes_credentials_are_applied_and_admin_commands_stay_off()
        {
            var options = RedisConnector.Build("cache.internal:6379,ssl=true,allowAdmin=true", new Dictionary<string, string> { ["username"] = " ada ", ["password"] = "pw-1" }, TimeSpan.FromSeconds(10));
            Assert.Equal(("ada", "pw-1", false, true, 10_000, 1), (options.User, options.Password, options.AllowAdmin, options.AbortOnConnectFail, options.ConnectTimeout, options.ConnectRetry));
            Assert.Equal(1_000, RedisConnector.Build("cache.internal", Password, TimeSpan.FromMilliseconds(10)).ConnectTimeout);
            Assert.Equal(120_000, RedisConnector.Build("cache.internal", Password, TimeSpan.FromHours(1)).SyncTimeout);
            Assert.Null(RedisConnector.Build("cache.internal", new Dictionary<string, string>(), TimeSpan.FromSeconds(5)).Password);
        }

        [Fact]
        public void A_sets_value_is_shaped_by_its_type()
        {
            Assert.Equal(("hash", "kund=Ada; summa=99.5; tags=[\"fika\"]"), Shaped("{\"kund\": \"Ada\", \"summa\": 99.5, \"tags\": [\"fika\"]}", "automatic"));
            Assert.Equal(("list", "=a; =2; ={\"x\":1}"), Shaped("[\"a\", 2, {\"x\": 1}]", "automatic"));
            Assert.Equal(("string", ""), Shaped("Kassan är öppen", "automatic"));
            Assert.Equal(("string", ""), Shaped("{\"kund\": \"Ada\"}", "string")); // text as given, JSON or not
            Assert.Equal(("set", "=a; =b"), Shaped("[\"a\", \"b\"]", "set"));
            foreach (var (value, type, refusal) in new[]
            {
                ("kund=Ada", "hash", "A hash's value is JSON"),
                ("[\"a\"]", "hash", "A hash's value is a JSON object with a field at least"),
                ("{}", "hash", "A hash's value is a JSON object with a field at least"),
                ("a, b", "list", "A list's value is JSON"),
                ("{\"a\": 1}", "set", "A set's value is a JSON array with a member at least"),
                ("[]", "list", "A list's value is a JSON array with a member at least"),
            })
            {
                Assert.StartsWith(refusal, Assert.Throws<InvalidOperationException>(() => RedisConnector.Shape(value, type)).Message, StringComparison.Ordinal);
            }

            static (string, string) Shaped(string value, string type) => RedisConnector.Shape(value, type) is var shaped ? (shaped.Type, string.Join("; ", (shaped.Members ?? new()).Select(member => member.Name + "=" + member.Value))) : default;
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            var egress = new StubEgress();
            async Task<string> Refused(string? operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RedisConnector(), Contexts.Extract(egress, "cache.internal:6379", operation!, parameters, credentials: Password)))).Message;

            Assert.StartsWith("Redis has no operation 'flushall' - info, get, set,", await Refused("flushall", new()), StringComparison.Ordinal);
            Assert.Equal("Give the key (key).", await Refused("get", new()));
            Assert.Equal("Give the list's key (list).", await Refused("push", new() { ["value"] = "x" }));
            Assert.Equal("Give the value (value).", await Refused("set", new() { ["key"] = "k" }));
            Assert.Equal("'json' is not automatic, string, hash, list or set (type).", await Refused("get", new() { ["key"] = "k", ["type"] = "json" }));
            Assert.Equal("'0' is not a number of seconds above 0 (ttl).", await Refused("set", new() { ["key"] = "k", ["value"] = "v", ["ttl"] = "0" }));
            Assert.Equal("'middle' is not head or tail (end).", await Refused("pop", new() { ["list"] = "q", ["end"] = "middle" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("keys", new() { ["pattern"] = "*", ["max"] = "0" }));
            Assert.Equal("'1.5' is not a whole number (by).", await Refused("incr", new() { ["key"] = "k", ["by"] = "1.5" }));
            Assert.Equal("'yes' is not true or false (values).", await Refused("keys", new() { ["pattern"] = "*", ["values"] = "yes" }));
            Assert.Equal("Give the pattern - kassa:* (pattern).", await Refused("keys", new()));
            Assert.Equal("Give the message (message).", await Refused("publish", new() { ["channel"] = "c" }));
            Assert.Equal("A hash's value is JSON - an object, {\"kund\": \"Ada\"} (value).", await Refused("set", new() { ["key"] = "k", ["value"] = "x", ["type"] = "hash" }));
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public void INFO_comes_apart_into_its_fields()
        {
            var info = RedisConnector.Info("# Server\r\nredis_version:7.2.4\r\nvalkey_version:8.1.4\r\n\r\n# Keyspace\r\ndb0:keys=3,expires=1,avg_ttl=0\r\nused_memory_human:1.02M\r\n");
            Assert.Equal(new[] { "redis_version", "valkey_version", "db0", "used_memory_human" }, info.Keys);
            Assert.Equal(("8.1.4", "keys=3,expires=1,avg_ttl=0"), (info["valkey_version"], info["db0"]));
        }
    }

    /// <summary>
    /// The Redis connector against a real server (this machine's throwaway Valkey on loopback, or a CI job's service
    /// container - <see cref="RedisTestEndpoint"/>) through the HOST's egress: loopback refused until allowlisted, a test naming
    /// the server, a wrong password failing; the client dialling the pinned address and never the name; a string with its time
    /// to live, a hash merged, a list and a set replaced, each read back by its type; a counter incremented; a queue pushed
    /// and popped at both ends; keys found by a pattern through SCAN with their values, no further than asked; a key deleted; a
    /// message published to a subscriber; INFO's fields with its keyspace as JSON; a read of the wrong type Redis's own error.
    /// Fails, never skips, without a server.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RedisConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"{RedisTestEndpoint.Host}:{RedisTestEndpoint.Port}";

        private static Dictionary<string, string> Credentials => new() { ["password"] = RedisTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { RedisTestEndpoint.Host });

        private static async Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters, string? database = null) =>
            await Contexts.DrainAsync(new RedisConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, database is null ? null : new Dictionary<string, string> { ["database"] = database }, Credentials, timeoutSeconds: 10));

        private static async Task<Dictionary<string, object?>> One(string operation, Dictionary<string, string> parameters, string? database = null) =>
            new(Assert.Single(await Run(operation, parameters, database)).Fields);

        [Fact]
        public async Task A_test_names_the_server_and_a_wrong_password_fails_and_the_client_dials_the_pinned_address()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new RedisConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 10))); // loopback, not allowlisted

            var result = await new RedisConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 10));
            output.WriteLine($"[live] {result.Success}: {result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.StartsWith("Connected to Valkey 8.", result.Message, StringComparison.Ordinal);

            var wrong = await new RedisConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["password"] = "not-the-password" }, timeoutSeconds: 10));
            Assert.False(wrong.Success);

            var pinned = new StubEgress { Pinned = IPAddress.Parse(RedisTestEndpoint.Host) };
            var named = await new RedisConnector().TestConnectionAsync(Contexts.Test(pinned, $"kassa-cache.invalid:{RedisTestEndpoint.Port}", credentials: Credentials, timeoutSeconds: 10));
            Assert.True(named.Success, named.Message); // a name that resolves nowhere: only the pinned address could answer
            Assert.Equal(("kassa-cache.invalid", RedisTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task Keys_hashes_lists_sets_counters_and_channels_through_the_connector()
        {
            var prefix = "stomme:test:" + Guid.NewGuid().ToString("N")[..8] + ":";
            try
            {
                Assert.Equal(true, (await One("set", new() { ["key"] = prefix + "status", ["value"] = "Kassan är öppen", ["ttl"] = "300" }))["set"]);
                Assert.Equal(("string", "Kassan är öppen"), Read(await One("get", new() { ["key"] = prefix + "status" })));
                Assert.InRange(await TtlAsync(prefix + "status"), 1, 300); // a string's time to live, set with it

                await One("set", new() { ["key"] = prefix + "order", ["value"] = "{\"kund\": \"Ada\", \"summa\": 99.5}" });
                await One("set", new() { ["key"] = prefix + "order", ["value"] = "{\"status\": \"paid\"}", ["type"] = "hash" }); // merged into it
                var order = Read(await One("get", new() { ["key"] = prefix + "order" }));
                Assert.Equal("hash", order.Type);
                Assert.Equal(("Ada", "99.5", "paid"), ((string?)JsonNode.Parse(order.Value!)!["kund"], (string?)JsonNode.Parse(order.Value!)!["summa"], (string?)JsonNode.Parse(order.Value!)!["status"]));

                await One("set", new() { ["key"] = prefix + "lines", ["value"] = "[\"kaffe\", \"bulle\"]" });
                await One("set", new() { ["key"] = prefix + "lines", ["value"] = "[\"te\"]" }); // replaced whole
                Assert.Equal(("list", "[\"te\"]"), Read(await One("get", new() { ["key"] = prefix + "lines" })));
                await One("set", new() { ["key"] = prefix + "tags", ["value"] = "[\"kaka\"]", ["type"] = "set" });
                await One("set", new() { ["key"] = prefix + "tags", ["value"] = "[\"fika\", \"bulle\", \"fika\"]", ["type"] = "set", ["ttl"] = "300" }); // replaced whole
                Assert.Equal(("set", "[\"bulle\",\"fika\"]"), Read(await One("get", new() { ["key"] = prefix + "tags" })));

                Assert.Equal(5L, (await One("incr", new() { ["key"] = prefix + "count", ["by"] = "5" }))["value"]);
                Assert.Equal(6L, (await One("incr", new() { ["key"] = prefix + "count", ["ttl"] = "60" }))["value"]);

                Assert.Equal(1L, (await One("push", new() { ["list"] = prefix + "queue", ["value"] = "first" }))["length"]);
                Assert.Equal(2L, (await One("push", new() { ["list"] = prefix + "queue", ["value"] = "last", ["end"] = "tail" }))["length"]);
                Assert.Equal(3L, (await One("push", new() { ["list"] = prefix + "queue", ["value"] = "zeroth" }))["length"]); // at the head, n8n's default
                Assert.Equal(3L, (await One("llen", new() { ["list"] = prefix + "queue" }))["length"]);
                Assert.Equal("last", (await One("pop", new() { ["list"] = prefix + "queue", ["end"] = "tail" }))["value"]);
                Assert.Equal("zeroth", (await One("pop", new() { ["list"] = prefix + "queue" }))["value"]);
                Assert.Equal("first", (await One("pop", new() { ["list"] = prefix + "queue" }))["value"]);
                Assert.Null((await One("pop", new() { ["list"] = prefix + "queue" }))["value"]); // empty

                var keys = await Run("keys", new() { ["pattern"] = prefix + "*" });
                Assert.Equal(new[] { "count", "lines", "order", "status", "tags" }, keys.Select(record => ((string)record.Fields["key"]!)[prefix.Length..]).OrderBy(key => key, StringComparer.Ordinal));
                Assert.Equal("6", keys.Single(record => ((string)record.Fields["key"]!).EndsWith("count", StringComparison.Ordinal)).Fields["value"]);
                Assert.Equal(2, (await Run("keys", new() { ["pattern"] = prefix + "*", ["values"] = "false", ["max"] = "2" })).Count); // no further than asked
                Assert.False((await Run("keys", new() { ["pattern"] = prefix + "*", ["values"] = "false" })).Any(record => record.Fields.ContainsKey("value")));

                Assert.Equal(true, (await One("delete", new() { ["key"] = prefix + "status" }))["deleted"]);
                Assert.Equal(false, (await One("delete", new() { ["key"] = prefix + "status" }))["deleted"]);
                Assert.Equal(("none", (string?)null), Read(await One("get", new() { ["key"] = prefix + "status" })));

                var wrong = await Assert.ThrowsAnyAsync<Exception>(() => Run("get", new() { ["key"] = prefix + "order", ["type"] = "list" }));
                Assert.Contains("WRONGTYPE", wrong.Message, StringComparison.Ordinal); // Redis's own words

                // Each time to live as asked, read by the client itself: a string's, a set's (after its write), a counter's.
                await using (var direct = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(RedisConnector.Build(Endpoint, Credentials, TimeSpan.FromSeconds(10))))
                {
                    var db = direct.GetDatabase();
                    Assert.InRange((await db.KeyTimeToLiveAsync(prefix + "tags"))?.TotalSeconds ?? 0, 1, 300);
                    Assert.InRange((await db.KeyTimeToLiveAsync(prefix + "count"))?.TotalSeconds ?? 0, 1, 60);
                    Assert.Null(await db.KeyTimeToLiveAsync(prefix + "order")); // none asked: none set
                }

                // The connection's database: a key written into database 2 is not database 0's.
                await One("set", new() { ["key"] = prefix + "elsewhere", ["value"] = "två" }, "2");
                Assert.Equal(("string", "två"), Read(await One("get", new() { ["key"] = prefix + "elsewhere" }, "2")));
                Assert.Equal(("none", (string?)null), Read(await One("get", new() { ["key"] = prefix + "elsewhere" })));
                await One("delete", new() { ["key"] = prefix + "elsewhere" }, "2");

                var info = await One("info", new());
                Assert.IsType<string>(info["redis_version"]);
                Assert.IsType<long>(info["connected_clients"]);
                Assert.True(JsonNode.Parse((string)info["db0"]!)!["keys"] is not null, (string?)info["db0"]); // a keyspace's pairs as JSON
                var listener = info.Where(field => field.Key.StartsWith("listener", StringComparison.Ordinal)).Select(field => (string)field.Value!).FirstOrDefault();
                Assert.True(listener is null || JsonNode.Parse(listener) is JsonObject, listener); // a name that repeats in it (bind) kept, not a failure
            }
            finally
            {
                foreach (var name in new[] { "status", "order", "lines", "tags", "count", "queue" })
                {
                    await Run("delete", new() { ["key"] = prefix + name });
                }
            }

            static (string? Type, string? Value) Read(Dictionary<string, object?> record) => ((string?)record["type"], (string?)record["value"]);
        }

        // A key's time to live as the client itself reads it, in seconds - 0 when it has none.
        private static async Task<double> TtlAsync(string key)
        {
            await using var direct = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(RedisConnector.Build(Endpoint, Credentials, TimeSpan.FromSeconds(10)));
            return (await direct.GetDatabase().KeyTimeToLiveAsync(key))?.TotalSeconds ?? 0;
        }

        [Fact]
        public async Task A_message_is_published_to_a_subscriber()
        {
            var channel = "stomme:test:" + Guid.NewGuid().ToString("N")[..8];
            var options = RedisConnector.Build(Endpoint, Credentials, TimeSpan.FromSeconds(10));
            await using var listener = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(options);
            var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await listener.GetSubscriber().SubscribeAsync(StackExchange.Redis.RedisChannel.Literal(channel), (_, message) => heard.TrySetResult((string)message!));
            var published = await One("publish", new() { ["channel"] = channel, ["message"] = "{\"order\": 1042}" });
            Assert.Equal((channel, 1L), ((string?)published["channel"], published["receivers"]));
            Assert.Equal("{\"order\": 1042}", await heard.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }
}
