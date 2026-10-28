#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the MongoDB connector without a server: an address that names no server or carries a user, refused as invalid;
    /// a mongodb+srv address, a list of servers, a replica set and a proxy refused as the egress's denial - each before any pin;
    /// the host pinned, its address dialled - or its name kept where TLS needs it - the connection held direct and every
    /// connection the driver opens checked against that one server; the scheme's credentials applied; every parameter held to
    /// its shape before anything is dialled; a document's fields as a record's.
    /// </summary>
    public sealed class MongoDbConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kassa", ["password"] = "pw-1" };

        [Fact]
        public async Task An_address_that_names_no_server_or_carries_a_user_is_invalid_and_what_reaches_past_the_egress_is_denied()
        {
            var egress = new StubEgress();
            foreach (var (endpoint, said) in new[] { ("", "it names no server"), ("mongodb://ada:pw@db.internal/kassa", "a user or a password is never part of the address"), ("http://db.internal", "") })
            {
                var result = await new MongoDbConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials));
                Assert.False(result.Success);
                Assert.StartsWith("Invalid MongoDB address: " + said, result.Message, StringComparison.Ordinal);
            }

            foreach (var (endpoint, refusal) in new[]
            {
                ("mongodb+srv://cluster0.abcde.mongodb.net/kassa", "a mongodb+srv address's servers are DNS's"),
                ("mongodb://a.internal:27017,b.internal:27017/kassa", "names ONE server"),
                ("mongodb://db.internal/kassa?replicaSet=rs0", "a replica set's members are named by the server"),
                ("mongodb://db.internal/kassa?proxyHost=proxy.internal", "not through a proxy the address names"),
            })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MongoDbConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, credentials: Credentials)));
                Assert.Contains(refusal, denied.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(OutboundConnectGuard.IsDenial(denied));
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_the_connection_held_direct_and_every_connection_checked_against_it()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MongoDbConnector().TestConnectionAsync(Contexts.Test(refused, "mongodb://db.internal:27019/kassa", credentials: Credentials)));
            Assert.Contains("db.internal", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("db.internal", 27019), refused.Pins.Single());

            var (plain, _) = MongoDbConnector.Parse("mongodb://db.internal/kassa", new Dictionary<string, string>());
            var settings = await MongoDbConnector.SettingsAsync(plain, Credentials, new StubEgress { Pinned = IPAddress.Parse("10.0.0.5") }, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal((new MongoServerAddress("10.0.0.5", 27017), true), (settings.Server, settings.DirectConnection)); // the pinned address, MongoDB's own port
            Assert.Equal(("kassa", "kassa", TimeSpan.FromSeconds(10)), (settings.Credential.Username, settings.Credential.Source, settings.ServerSelectionTimeout)); // the address's database the source
            var (tls, _) = MongoDbConnector.Parse("mongodb://db.internal:27017/kassa?tls=true&authSource=admin", new Dictionary<string, string>());
            var named = await MongoDbConnector.SettingsAsync(tls, Credentials, new StubEgress { Pinned = IPAddress.Parse("10.0.0.5") }, TimeSpan.FromMilliseconds(10), CancellationToken.None);
            Assert.Equal((new MongoServerAddress("db.internal", 27017), "admin", TimeSpan.FromSeconds(1)), (named.Server, named.Credential.Source, named.ConnectTimeout)); // TLS needs the name; the timeout's floor

            MongoDbConnector.Guard(new DnsEndPoint("10.0.0.5", 27017), settings.Server);
            MongoDbConnector.Guard(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 27017), settings.Server);
            foreach (var other in new EndPoint[] { new DnsEndPoint("replica.internal", 27017), new DnsEndPoint("10.0.0.5", 27018), new IPEndPoint(IPAddress.Parse("10.0.0.6"), 27017) })
            {
                Assert.Contains("only the server this site's egress policy checked is dialled", Assert.Throws<ConnectorEgressDeniedException>(() => MongoDbConnector.Guard(other, settings.Server)).Message, StringComparison.Ordinal);
            }

            var closed = await new MongoDbConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "mongodb://db.internal:1/kassa", credentials: Credentials, timeoutSeconds: 2));
            Assert.False(closed.Success); // the pinned loopback's closed port: the driver's failure, said, not thrown
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            var egress = new StubEgress();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "mongodb://db.internal/kassa") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MongoDbConnector(), Contexts.Extract(egress, endpoint, operation, parameters, credentials: Credentials)))).Message;

            Assert.StartsWith("MongoDB has no operation 'drop' - find, insert, update,", await Refused("drop", new()), StringComparison.Ordinal);
            Assert.Equal("Give the database - in the address (mongodb://db.example.com/kassa) or the connection's setting.", await Refused("find", new() { ["collection"] = "orders" }, "mongodb://db.internal"));
            Assert.Equal("Give the collection (collection).", await Refused("find", new()));
            Assert.Equal("'$cmd' is not a collection's name (collection).", await Refused("find", new() { ["collection"] = "$cmd" }));
            Assert.Equal("'system.users' is not a collection's name (collection).", await Refused("find", new() { ["collection"] = "system.users" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("find", new() { ["collection"] = "orders", ["max"] = "0" }));
            Assert.Equal("'-1' is not a number (skip).", await Refused("find", new() { ["collection"] = "orders", ["skip"] = "-1" }));
            Assert.Equal("'status = open' is not a JSON object (query).", await Refused("find", new() { ["collection"] = "orders", ["query"] = "status = open" }));
            Assert.Equal("An empty query deletes every document of the collection - give a query, or all = true to mean it.", await Refused("delete", new() { ["collection"] = "orders", ["query"] = "{}" }));
            Assert.Equal("The documents are a JSON object, or an array of objects (documents).", await Refused("insert", new() { ["collection"] = "orders", ["documents"] = "[1, 2]" }));
            Assert.Equal("The document has no '_id' - the value it is found by is one of its fields (document).", await Refused("update", new() { ["collection"] = "orders", ["document"] = "{\"status\": \"paid\"}" }));
            Assert.Equal("The document has no field to set but 'nummer' (document).", await Refused("update", new() { ["collection"] = "orders", ["key"] = "nummer", ["document"] = "{\"nummer\": 1042}" }));
            Assert.Equal("'$where' is not a field's name (key).", await Refused("update", new() { ["collection"] = "orders", ["key"] = "$where", ["document"] = "{}" }));
            Assert.StartsWith("The pipeline is a JSON array of stages, each one $stage", await Refused("aggregate", new() { ["collection"] = "orders", ["pipeline"] = "[{\"match\": {}}]" }), StringComparison.Ordinal);
            Assert.Equal("'ja' is not true or false (upsert).", await Refused("update", new() { ["collection"] = "orders", ["document"] = "{\"_id\": 1, \"a\": 2}", ["upsert"] = "ja" }));
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public void A_documents_fields_are_a_records()
        {
            var id = ObjectId.Parse("6512bd43d9caa6e02c990b0a");
            var record = MongoDbConnector.Record(new BsonDocument
            {
                { "_id", id }, { "kund", "Kafé Ada" }, { "summa", new BsonDecimal128(99.50m) }, { "antal", 3 }, { "stor", 3_000_000_000L }, { "betald", true },
                { "skapad", new BsonDateTime(new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc)) }, { "rader", new BsonArray { "kaffe", "bulle" } }, { "adress", new BsonDocument("stad", "Malmö") }, { "notering", BsonNull.Value },
            }).Fields;
            Assert.Equal(("6512bd43d9caa6e02c990b0a", "Kafé Ada", 99.50m, 3, 3_000_000_000L, true), (record["_id"], record["kund"], record["summa"], record["antal"], record["stor"], record["betald"]));
            Assert.Equal((new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc), "[\"kaffe\", \"bulle\"]", "{ \"stad\" : \"Malmö\" }", (object?)null), (record["skapad"], record["rader"], record["adress"], record["notering"]));
        }
    }

    /// <summary>
    /// BL-229 - the MongoDB connector against a real MongoDB 8.0 (this machine's throwaway container on loopback, or a CI job's
    /// service container - <see cref="MongoDbTestEndpoint"/>) through the HOST's egress: loopback refused until allowlisted, a
    /// test naming the server, a wrong password failing, the driver dialling the pinned address and never the name; documents
    /// inserted - each back with its _id - found by a query, sorted, projected, skipped and no further than asked; one updated
    /// by its _id's hex, one found and updated, one found and replaced - each as it is afterwards - one created when asked;
    /// documents deleted by a query, every one only when asked; a pipeline's group summed. Fails, never skips, without a server.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MongoDbConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"mongodb://{MongoDbTestEndpoint.Host}:{MongoDbTestEndpoint.Port}/kassa_test?authSource=admin";

        private static Dictionary<string, string> Credentials => new() { ["username"] = MongoDbTestEndpoint.User, ["password"] = MongoDbTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { MongoDbTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new MongoDbConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, credentials: Credentials, timeoutSeconds: 15));

        [Fact]
        public async Task A_test_names_the_server_and_a_wrong_password_fails_and_the_driver_dials_the_pinned_address()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MongoDbConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15))); // loopback, not allowlisted

            var result = await new MongoDbConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {result.Success}: {result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.StartsWith("Connected to MongoDB 8.", result.Message, StringComparison.Ordinal);

            var wrong = await new MongoDbConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = MongoDbTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 15));
            Assert.False(wrong.Success);

            var pinned = new StubEgress { Pinned = IPAddress.Parse(MongoDbTestEndpoint.Host) };
            var named = await new MongoDbConnector().TestConnectionAsync(Contexts.Test(pinned, $"mongodb://kassa-db.invalid:{MongoDbTestEndpoint.Port}/kassa_test?authSource=admin", credentials: Credentials, timeoutSeconds: 15));
            Assert.True(named.Success, named.Message); // a name that resolves nowhere: only the pinned address could answer
            Assert.Equal(("kassa-db.invalid", MongoDbTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task A_connection_the_driver_opens_to_any_other_server_is_refused_by_the_guard()
        {
            // The same server by another name: the driver would reach it, and the guard - checking every connection against the
            // address the egress pinned - refuses it in its own words.
            var settings = await MongoDbConnector.SettingsAsync(MongoUrl.Create(Endpoint), Credentials, Egress, TimeSpan.FromSeconds(3), CancellationToken.None);
            settings.Server = new MongoServerAddress("localhost", MongoDbTestEndpoint.Port);
            using var client = new MongoClient(settings);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.GetDatabase("kassa_test").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));
            var said = new List<string>();
            for (Exception? at = refused; at is not null; at = at.InnerException)
            {
                said.Add($"{at.GetType().Name}: {at.Message}");
            }

            output.WriteLine("[live] " + string.Join(" <- ", said));
            Assert.Contains(said, line => line.StartsWith(nameof(ConnectorEgressDeniedException), StringComparison.Ordinal) && line.Contains("only the server this site's egress policy checked is dialled", StringComparison.Ordinal)); // the driver's own exception, the guard's inside it

            settings.Server = new MongoServerAddress(MongoDbTestEndpoint.Host, MongoDbTestEndpoint.Port);
            using var allowed = new MongoClient(settings);
            Assert.Equal(1.0, (await allowed.GetDatabase("kassa_test").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)))["ok"].ToDouble()); // the pinned one: through
        }

        [Fact]
        public async Task Documents_are_inserted_found_updated_replaced_deleted_and_aggregated()
        {
            var collection = "orders_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                var inserted = await Run("insert", new() { ["collection"] = collection, ["documents"] = "[{\"nummer\": 1042, \"kund\": \"Kafé Ada\", \"summa\": 99.5, \"status\": \"open\"}, {\"nummer\": 1043, \"kund\": \"Kafé Bo\", \"summa\": 12, \"status\": \"open\"}, {\"nummer\": 1044, \"kund\": \"Kafé Ada\", \"summa\": 40, \"status\": \"paid\"}]" });
                Assert.Equal(3, inserted.Count);
                var firstId = (string)inserted[0].Fields["_id"]!;
                Assert.Matches("^[0-9a-f]{24}$", firstId); // each back with its _id

                var open = await Run("find", new() { ["collection"] = collection, ["query"] = "{\"status\": \"open\"}", ["sort"] = "{\"nummer\": -1}", ["projection"] = "{\"_id\": 0, \"nummer\": 1, \"kund\": 1}" });
                Assert.Equal(new object?[] { 1043, 1042 }, open.Select(record => record.Fields["nummer"]));
                Assert.Equal(new[] { "kund", "nummer" }, open[0].Fields.Keys.OrderBy(name => name, StringComparer.Ordinal)); // projected
                Assert.Equal(1044, Assert.Single(await Run("find", new() { ["collection"] = collection, ["sort"] = "{\"nummer\": 1}", ["skip"] = "2", ["max"] = "5" })).Fields["nummer"]);
                Assert.Single(await Run("find", new() { ["collection"] = collection, ["max"] = "1" })); // no further than asked

                var updated = Assert.Single(await Run("update", new() { ["collection"] = collection, ["document"] = "{\"_id\": \"" + firstId + "\", \"status\": \"paid\"}" })).Fields;
                Assert.Equal((1L, 1L, (object?)null), (updated["matched"], updated["modified"], updated["upserted_id"])); // found by its _id's hex
                var after = Assert.Single(await Run("find_and_update", new() { ["collection"] = collection, ["key"] = "nummer", ["document"] = "{\"nummer\": 1043, \"status\": \"void\"}" })).Fields;
                Assert.Equal(("void", "Kafé Bo"), (after["status"], after["kund"])); // as it is afterwards
                var replaced = Assert.Single(await Run("find_and_replace", new() { ["collection"] = collection, ["key"] = "nummer", ["document"] = "{\"nummer\": 1044, \"kund\": \"Kafé Ada\", \"summa\": 45}" })).Fields;
                Assert.Equal((45, false), (replaced["summa"], replaced.ContainsKey("status"))); // replaced whole
                Assert.Empty(await Run("find_and_update", new() { ["collection"] = collection, ["key"] = "nummer", ["document"] = "{\"nummer\": 9999, \"status\": \"x\"}" })); // none: none back
                var created = Assert.Single(await Run("update", new() { ["collection"] = collection, ["key"] = "nummer", ["document"] = "{\"nummer\": 1045, \"kund\": \"Kafé Cy\", \"status\": \"open\"}", ["upsert"] = "true" })).Fields;
                Assert.Matches("^[0-9a-f]{24}$", (string)created["upserted_id"]!);

                var totals = await Run("aggregate", new() { ["collection"] = collection, ["pipeline"] = "[{\"$match\": {\"kund\": {\"$regex\": \"^Kafé\"}}}, {\"$group\": {\"_id\": \"$kund\", \"summa\": {\"$sum\": \"$summa\"}}}, {\"$sort\": {\"_id\": 1}}]" });
                Assert.Equal(new[] { ("Kafé Ada", 144.5), ("Kafé Bo", 12.0), ("Kafé Cy", 0.0) }, totals.Select(record => ((string)record.Fields["_id"]!, Convert.ToDouble(record.Fields["summa"], System.Globalization.CultureInfo.InvariantCulture))));
                Assert.Single(await Run("aggregate", new() { ["collection"] = collection, ["pipeline"] = "[{\"$match\": {}}]", ["max"] = "1" })); // no further than asked

                Assert.Equal(1L, Assert.Single(await Run("delete", new() { ["collection"] = collection, ["query"] = "{\"status\": \"void\"}" })).Fields["deleted"]);
                Assert.Equal(3L, Assert.Single(await Run("delete", new() { ["collection"] = collection, ["query"] = "{}", ["all"] = "true" })).Fields["deleted"]); // every one, asked
            }
            finally
            {
                var settings = await MongoDbConnector.SettingsAsync(MongoUrl.Create(Endpoint), Credentials, Egress, TimeSpan.FromSeconds(15), CancellationToken.None);
                using var client = new MongoClient(settings);
                await client.GetDatabase("kassa_test").DropCollectionAsync(collection);
            }
        }
    }
}
