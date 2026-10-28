#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.GoogleFirestore;
using Stomme.Modules.Connectors.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Google Cloud Firestore connector's live tests' database (BL-229): FIRESTORE_TEST_HOST / PORT where set, otherwise this
    /// machine's Firestore emulator that <c>scripts/firestore-test-server.ps1</c> makes on 127.0.0.1:18080. It serves any project
    /// and checks no real token - its admin calls only the emulator's own, "owner", which the tests' token stand-in hands out. An
    /// absent emulator is a FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class FirestoreTestEndpoint
    {
        public const string Owner = "owner";

        public static string Host => Environment.GetEnvironmentVariable("FIRESTORE_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("FIRESTORE_TEST_PORT"), out var port) ? port : 18080;

        public static string Endpoint => $"http://{Host}:{Port}";
    }

    /// <summary>
    /// BL-229 - Google Cloud Firestore (n8n's Google Cloud Firestore node) without a server: every parameter held to its shape
    /// before a token is asked for; every call the database's documents under /v1 with the refresh token's access token as its
    /// Bearer; JSON as Firestore's typed values - a string never guessed to be a number, a boolean or a date as n8n guesses, a typed
    /// object passed as it is - and back; documents listed page by page by Google's page token, read by id with a missing one no
    /// record, made with Firestore's id or one given, set with an update mask of the fields given (a field's path quoted where it
    /// must be) or replaced whole, deleted; collections of the database or of a document; a structured query from the Query box,
    /// its read-time-only answer no record; Google's status and message on a failure.
    /// </summary>
    public sealed class GoogleFirestoreConnectorTests
    {
        private const string Documents = "https://firestore.googleapis.com/v1/projects/kafe-kassa/databases/%28default%29/documents";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private static readonly Dictionary<string, string> Settings = new() { ["project"] = "kafe-kassa" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == GoogleFirestoreConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"fs-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static IEnumerable<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleFirestoreConnector.DefaultTokenUrl);

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new GoogleFirestoreConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, settings ?? Settings, Credentials));

        private const string Order = "{\"name\":\"projects/kafe-kassa/databases/(default)/documents/orders/1042\",\"fields\":{\"till\":{\"stringValue\":\"2\"},\"total\":{\"doubleValue\":35.5},\"count\":{\"integerValue\":\"3\"},\"paid\":{\"booleanValue\":true},\"at\":{\"timestampValue\":\"2026-10-17T08:00:00Z\"},\"lines\":{\"arrayValue\":{\"values\":[{\"stringValue\":\"kaffe\"},{\"integerValue\":\"2\"}]}},\"site\":{\"mapValue\":{\"fields\":{\"country\":{\"stringValue\":\"se\"}}}},\"none\":{\"nullValue\":null}},\"createTime\":\"2026-10-17T18:00:02Z\",\"updateTime\":\"2026-10-17T18:00:03Z\"}";

        [Fact]
        public void It_declares_itself_as_n8ns_google_cloud_firestore_node()
        {
            IConnector connector = new GoogleFirestoreConnector();
            Assert.Equal(("googlefirestore", "Google Cloud Firestore", AuthenticationType.OAuth2RefreshToken, ConnectorEndpointKind.Url), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.Equal(new[] { ConnectorCategory.DataAndFiles }, connector.Categories);
            Assert.Equal(new[] { "collections", "documents", "document_get", "document_create", "document_upsert", "document_delete", "query" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "project", "database", "token_url" }, connector.ConfigSchema.Fields.Select(field => field.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters, settings));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Assert.StartsWith("Google Cloud Firestore has no operation 'set' - collections, documents,", await Refused("set", new()), StringComparison.Ordinal);
            Assert.Equal("Give the connection its project (project).", await Refused("documents", new() { ["collection"] = "orders" }, new()));
            Assert.Equal("'Kafe Kassa' is not a project's id - kafe-kassa (project).", await Refused("documents", new() { ["collection"] = "orders" }, new() { ["project"] = "Kafe Kassa" }));
            Assert.Equal("'Orders' is not a database's id - (default), or kafe-orders (database).", await Refused("documents", new() { ["collection"] = "orders" }, new() { ["project"] = "kafe-kassa", ["database"] = "Orders" }));
            Assert.Equal("Give the collection's path (collection).", await Refused("documents", new()));
            Assert.Equal("'orders/1042' is not a collection's path - orders, or orders/1042/lines (collection).", await Refused("documents", new() { ["collection"] = "orders/1042" }));
            Assert.Equal("'orders//lines' is not a collection's path - orders, or orders/1042/lines (collection).", await Refused("documents", new() { ["collection"] = "orders//lines" }));
            Assert.Equal("'__kassa__' is not a collection's path - orders, or orders/1042/lines (collection).", await Refused("documents", new() { ["collection"] = "__kassa__" }));
            Assert.Equal("'orders' is not a document's path - orders/1042 (document).", await Refused("collections", new() { ["document"] = "orders" }));
            Assert.Equal("Give the document's id (id).", await Refused("document_get", new() { ["collection"] = "orders" }));
            Assert.Equal("'10/42' is not a document's id - no /, not . or .., not __like this__ (id).", await Refused("document_delete", new() { ["collection"] = "orders", ["id"] = "10/42" }));
            Assert.Equal("'..' is not a document's id - no /, not . or .., not __like this__ (id).", await Refused("document_get", new() { ["collection"] = "orders", ["id"] = "1042, .." }));
            Assert.Equal("Give its fields - a JSON object (fields).", await Refused("document_create", new() { ["collection"] = "orders" }));
            Assert.Equal("The fields is not a JSON object (fields).", await Refused("document_create", new() { ["collection"] = "orders", ["fields"] = "[1]" }));
            Assert.Equal("'__kassa__' is not a field's name Firestore keeps - at most 1,500 bytes, not __like this__ (fields).", await Refused("document_create", new() { ["collection"] = "orders", ["fields"] = "{\"__kassa__\": 1}" }));
            Assert.Equal("Give the document's id (id).", await Refused("document_upsert", new() { ["collection"] = "orders", ["fields"] = "{\"till\": \"2\"}" }));
            Assert.Equal("'maybe' is not true or false (replace).", await Refused("document_upsert", new() { ["collection"] = "orders", ["id"] = "1042", ["fields"] = "{\"till\": \"2\"}", ["replace"] = "maybe" }));
            Assert.Equal("Give the structured query - or write it in the step's Query box.", await Refused("query", new()));
            Assert.Equal("The structured query is not a JSON object - {\"from\": [{\"collectionId\": \"orders\"}]}.", await Refused("query", new() { ["structured_query"] = "SELECT * FROM orders" }));
            Assert.Equal("The structured query names no collection - {\"from\": [{\"collectionId\": \"orders\"}], ...}.", await Refused("query", new() { ["structured_query"] = "{\"structuredQuery\": {\"limit\": 5}}" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("documents", new() { ["collection"] = "orders", ["max"] = "0" }));
        }

        [Fact]
        public void Json_is_firestores_typed_values_and_back_a_string_never_guessed()
        {
            var fields = JsonNode.Parse("{\"s\": \"3\", \"t\": \"true\", \"d\": \"2026-10-17\", \"i\": 3, \"big\": 12345678901234567890, \"f\": 35.5, \"b\": false, \"n\": null, \"a\": [1, \"x\"], \"m\": {\"c\": \"se\"}, \"at\": {\"timestampValue\": \"2026-10-17T08:00:00Z\"}, \"geo\": {\"geoPointValue\": {\"latitude\": 59.3, \"longitude\": 18.1}}}")!.AsObject();
            var typed = new JsonObject(fields.Select(field => KeyValuePair.Create(field.Key, (JsonNode?)GoogleFirestoreConnector.ValueOf(field.Value))));
            Assert.Equal(
                "{\"s\":{\"stringValue\":\"3\"},\"t\":{\"stringValue\":\"true\"},\"d\":{\"stringValue\":\"2026-10-17\"},\"i\":{\"integerValue\":\"3\"},\"big\":{\"doubleValue\":1.2345678901234567E+19},\"f\":{\"doubleValue\":35.5},\"b\":{\"booleanValue\":false},\"n\":{\"nullValue\":null},"
                + "\"a\":{\"arrayValue\":{\"values\":[{\"integerValue\":\"1\"},{\"stringValue\":\"x\"}]}},\"m\":{\"mapValue\":{\"fields\":{\"c\":{\"stringValue\":\"se\"}}}},\"at\":{\"timestampValue\":\"2026-10-17T08:00:00Z\"},\"geo\":{\"geoPointValue\":{\"latitude\":59.3,\"longitude\":18.1}}}",
                typed.ToJsonString());
            var back = new JsonObject(typed.Select(field => KeyValuePair.Create(field.Key, GoogleFirestoreConnector.PlainOf((JsonObject)field.Value!))));
            Assert.Equal("{\"s\":\"3\",\"t\":\"true\",\"d\":\"2026-10-17\",\"i\":3,\"big\":1.2345678901234567E+19,\"f\":35.5,\"b\":false,\"n\":null,\"a\":[1,\"x\"],\"m\":{\"c\":\"se\"},\"at\":\"2026-10-17T08:00:00Z\",\"geo\":{\"latitude\":59.3,\"longitude\":18.1}}", back.ToJsonString());
        }

        [Fact]
        public async Task Documents_are_listed_page_by_page_and_read_by_id_as_plain_records()
        {
            string Page(int from, int count, bool more) => "{\"documents\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"name\":\"projects/kafe-kassa/databases/(default)/documents/orders/" + i + "\",\"fields\":{\"total\":{\"integerValue\":\"" + i + "\"}}}")) + "]" + (more ? ",\"nextPageToken\":\"page-" + (from + count) + "\"" : "") + "}";
            var google = new Google { Answer = sent => sent.Url.Contains("pageToken=page-100", StringComparison.Ordinal) ? (HttpStatusCode.OK, Page(100, 30, false)) : (HttpStatusCode.OK, Page(0, 100, true)) };
            var all = await Run(google, "documents", new() { ["collection"] = "orders" });
            Assert.Equal(130, all.Count);
            Assert.Equal(new[] { Documents + "/orders?pageSize=100", Documents + "/orders?pageSize=100&pageToken=page-100" }, Calls(google).Select(sent => sent.Url));
            Assert.All(Calls(google), sent => Assert.Equal("Bearer fs-token", sent.Authorization));
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "0", ["total"] = 0L }, all[0].Fields);

            google.Requests.Clear();
            Assert.Equal(20, (await Run(google, "documents", new() { ["collection"] = "orders/1042/lines", ["max"] = "20" })).Count);
            Assert.Equal(Documents + "/orders/1042/lines?pageSize=20", Calls(google).Single().Url); // a subcollection, and a page no larger than what is wanted

            var got = new Google { Answer = _ => (HttpStatusCode.OK, "[{\"found\":" + Order + ",\"readTime\":\"x\"},{\"missing\":\"projects/kafe-kassa/databases/(default)/documents/orders/9\",\"readTime\":\"x\"}]") };
            var read = Assert.Single(await Run(got, "document_get", new() { ["collection"] = "orders", ["id"] = "1042, 9" })).Fields; // a missing one is no record, as n8n's
            var batch = Calls(got).Single();
            Assert.Equal(("POST", Documents + ":batchGet", "{\"documents\":[\"projects/kafe-kassa/databases/(default)/documents/orders/1042\",\"projects/kafe-kassa/databases/(default)/documents/orders/9\"]}"), (batch.Method, batch.Url, batch.Body));
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "1042", ["till"] = "2", ["total"] = 35.5, ["count"] = 3L, ["paid"] = true, ["at"] = "2026-10-17T08:00:00Z", ["lines"] = "[\"kaffe\",2]", ["site"] = "{\"country\":\"se\"}", ["none"] = null }, read);
            var full = Assert.Single(await Run(got, "document_get", new() { ["collection"] = "orders", ["id"] = "1042", ["simple"] = "false" })).Fields;
            Assert.Equal(("projects/kafe-kassa/databases/(default)/documents/orders/1042", "2026-10-17T18:00:02Z", "2026-10-17T18:00:03Z"), ((string?)full["name"], (string?)full["createTime"], (string?)full["updateTime"]));
        }

        [Fact]
        public async Task A_document_is_made_set_by_its_update_mask_or_replaced_and_deleted()
        {
            var google = new Google { Answer = sent => (HttpStatusCode.OK, sent.Method == "DELETE" ? "{}" : Order) };
            await Run(google, "document_create", new() { ["collection"] = "orders", ["id"] = "1042", ["fields"] = "{\"till\": \"2\", \"total\": 35.5}" });
            var made = Calls(google).Last();
            Assert.Equal(("POST", Documents + "/orders?documentId=1042", "{\"fields\":{\"till\":{\"stringValue\":\"2\"},\"total\":{\"doubleValue\":35.5}}}"), (made.Method, made.Url, made.Body));
            await Run(google, "document_create", new() { ["collection"] = "orders", ["fields"] = "{\"till\": \"1\"}" });
            Assert.Equal(Documents + "/orders", Calls(google).Last().Url); // Firestore's own id

            await Run(google, "document_upsert", new() { ["collection"] = "orders", ["id"] = "kassa å", ["fields"] = "{\"total\": 40, \"note x\": \"hej\", \"back`tick\": 1}" });
            var set = Calls(google).Last();
            Assert.Equal(("PATCH", Documents + "/orders/kassa%20%C3%A5?updateMask.fieldPaths=total&updateMask.fieldPaths=%60note%20x%60&updateMask.fieldPaths=%60back%5C%60tick%60"), (set.Method, set.Url)); // only the fields given; a path quoted where it must be
            await Run(google, "document_upsert", new() { ["collection"] = "orders", ["id"] = "1042", ["fields"] = "{\"till\": \"2\"}", ["replace"] = "true" });
            Assert.Equal(Documents + "/orders/1042", Calls(google).Last().Url); // no mask: the document replaced whole

            Assert.Equal(new Dictionary<string, object?> { ["id"] = "1042", ["deleted"] = true }, Assert.Single(await Run(google, "document_delete", new() { ["collection"] = "orders", ["id"] = "1042" })).Fields);
            Assert.Equal(("DELETE", Documents + "/orders/1042"), (Calls(google).Last().Method, Calls(google).Last().Url));
            await Run(google, "document_delete", new() { ["collection"] = "orders", ["id"] = "rabatt 50%25" });
            Assert.Equal(Documents + "/orders/rabatt%2050%2525", Calls(google).Last().Url); // an id that looks escaped is escaped again, so Firestore reads it as written
        }

        [Fact]
        public async Task Collections_and_a_structured_query_are_sent_as_asked()
        {
            var google = new Google
            {
                Answer = sent => sent.Url.EndsWith(":listCollectionIds", StringComparison.Ordinal)
                    ? sent.Body!.Contains("pageToken", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"collectionIds\":[\"guests\"]}") : (HttpStatusCode.OK, "{\"collectionIds\":[\"orders\"],\"nextPageToken\":\"more\"}")
                    : (HttpStatusCode.OK, "[{\"document\":" + Order + ",\"readTime\":\"x\"},{\"document\":" + Order.Replace("/1042", "/1043") + "},{\"readTime\":\"x\",\"done\":true}]"),
            };
            var collections = await Run(google, "collections", new());
            Assert.Equal(new[] { ("orders", "orders"), ("guests", "guests") }, collections.Select(record => ((string)record.Fields["name"]!, (string)record.Fields["path"]!)));
            Assert.Equal(new[] { "{\"pageSize\":100}", "{\"pageSize\":100,\"pageToken\":\"more\"}" }, Calls(google).Select(sent => sent.Body));
            google.Requests.Clear();
            Assert.Equal(2, (await Run(google, "collections", new() { ["max"] = "2" })).Count);
            Assert.Equal(new[] { "{\"pageSize\":2}", "{\"pageSize\":1,\"pageToken\":\"more\"}" }, Calls(google).Select(sent => sent.Body)); // a page no larger than what is still wanted
            google.Requests.Clear();
            var lines = await Run(google, "collections", new() { ["document"] = "orders/1042", ["max"] = "1" });
            Assert.Equal(("orders", "orders/1042/orders"), ((string)Assert.Single(lines).Fields["name"]!, (string)lines[0].Fields["path"]!));
            Assert.Equal(Documents + "/orders/1042:listCollectionIds", Calls(google).Single().Url);

            google.Requests.Clear();
            var found = await Run(google, "query", new() { ["query"] = "{\"structuredQuery\": {\n  \"from\": [{\"collectionId\": \"orders\"}],\n  \"where\": {\"fieldFilter\": {\"field\": {\"fieldPath\": \"till\"}, \"op\": \"EQUAL\", \"value\": {\"stringValue\": \"2\"}}}\n}}" });
            Assert.Equal(new[] { "1042", "1043" }, found.Select(record => (string)record.Fields["id"]!)); // the read-time-only answer is no record
            var asked = Calls(google).Single();
            Assert.Equal(("POST", Documents + ":runQuery"), (asked.Method, asked.Url));
            Assert.Equal("{\"structuredQuery\":{\"from\":[{\"collectionId\":\"orders\"}],\"where\":{\"fieldFilter\":{\"field\":{\"fieldPath\":\"till\"},\"op\":\"EQUAL\",\"value\":{\"stringValue\":\"2\"}}}}}", asked.Body);
            google.Requests.Clear();
            Assert.Single(await Run(google, "query", new() { ["structured_query"] = "{\"from\": [{\"collectionId\": \"lines\"}]}", ["document"] = "orders/1042", ["max"] = "1" })); // without its wrapper, under a document, held to its most
            Assert.Equal((Documents + "/orders/1042:runQuery", "{\"structuredQuery\":{\"from\":[{\"collectionId\":\"lines\"}]}}"), (Calls(google).Single().Url, Calls(google).Single().Body));
        }

        [Fact]
        public async Task A_failure_is_said_with_googles_status_and_message()
        {
            async Task<string> Failed(HttpStatusCode status, string body) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Google { Answer = _ => (status, body) }, "documents", new() { ["collection"] = "orders" }))).Message;

            Assert.Equal("Firestore answered HTTP 409 ALREADY_EXISTS: entity already exists", await Failed(HttpStatusCode.Conflict, "{\"error\":{\"code\":409,\"message\":\"entity already exists\",\"status\":\"ALREADY_EXISTS\"}}"));
            Assert.Equal("Firestore answered HTTP 403 PERMISSION_DENIED: Missing or insufficient permissions. - check the refresh token and its scope.", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"Missing or insufficient permissions.\",\"status\":\"PERMISSION_DENIED\"}}"));
            Assert.Equal("Firestore answered HTTP 400 INVALID_ARGUMENT: Payload isn't valid for request.", await Failed(HttpStatusCode.BadRequest, "[{\"error\":{\"code\":400,\"message\":\"Payload isn't valid for request.\",\"status\":\"INVALID_ARGUMENT\"}}]")); // runQuery answers its error in a list
            Assert.Equal("Firestore answered HTTP 502.", await Failed(HttpStatusCode.BadGateway, ""));
            Assert.Equal("Firestore answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            var tested = await new GoogleFirestoreConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(new Google { Answer = _ => (HttpStatusCode.OK, "{\"collectionIds\":[\"orders\",\"guests\"]}") }), "", Settings, Credentials));
            Assert.Equal((true, "Google Cloud Firestore: the project kafe-kassa's database (default) - orders, guests."), (tested.Success, tested.Message));
        }
    }

    /// <summary>
    /// BL-229 - the Google Cloud Firestore connector against Google's own Firestore emulator (<c>scripts/firestore-test-server.ps1</c>'s
    /// on loopback) through the HOST's egress, Google's token endpoint a loopback stand-in: loopback refused until allowlisted; a
    /// test naming the database; a document made with each of Firestore's types and read back as plain JSON, a second refused;
    /// documents paged and held to the most; one set by its update mask with its other fields kept and a quoted field path, then
    /// replaced whole; a subcollection made and listed under its document; a structured query from the Query box; a document
    /// deleted and read as no record. The emulator checks no real token, so this proves the protocol. Fails, never skips, when
    /// the emulator is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class GoogleFirestoreConnectorLiveTests(ITestOutputHelper output) : IDisposable
    {
        private readonly GoogleTokenStandIn _tokens = new("kafe-client", "kafe-secret", "kafe-refresh", FirestoreTestEndpoint.Owner);

        private readonly string _collection = "orders" + Guid.NewGuid().ToString("N")[..8];

        private static Dictionary<string, string> Credentials => new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private Dictionary<string, string> Settings => new() { ["token_url"] = _tokens.Url, ["project"] = "kafe-kassa" };

        private static HostConnectorEgress Egress => new(new[] { FirestoreTestEndpoint.Host });

        public void Dispose() => _tokens.Dispose();

        private Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new GoogleFirestoreConnector(new OAuth2TokenProvider()), Contexts.Extract(Egress, FirestoreTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30));

        [Fact]
        public async Task A_test_names_the_database_and_loopback_is_refused_until_allowed()
        {
            var denied = await Assert.ThrowsAnyAsync<Exception>(() => new GoogleFirestoreConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), FirestoreTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied)); // loopback refused until allowlisted
            await Run("document_create", new() { ["collection"] = _collection, ["id"] = "seed", ["fields"] = "{\"seed\": true}" });
            var right = await new GoogleFirestoreConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(Egress, FirestoreTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.StartsWith("Google Cloud Firestore: the project kafe-kassa's database (default) - ", right.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Documents_are_made_read_set_replaced_queried_and_deleted()
        {
            var made = Assert.Single(await Run("document_create", new() { ["collection"] = _collection, ["id"] = "1042", ["fields"] = "{\"till\": \"2\", \"total\": 35.5, \"count\": 3, \"paid\": true, \"none\": null, \"lines\": [\"kaffe\", 2], \"site\": {\"country\": \"se\"}, \"at\": {\"timestampValue\": \"2026-10-17T08:00:00Z\"}}" })).Fields;
            Assert.Equal(("1042", "2", 35.5, 3L), ((string?)made["id"], (string?)made["till"], (double?)made["total"], (long?)made["count"]));
            var again = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("document_create", new() { ["collection"] = _collection, ["id"] = "1042", ["fields"] = "{\"till\": \"2\"}" }));
            output.WriteLine($"[live] {again.Message}");
            Assert.StartsWith("Firestore answered HTTP 409 ALREADY_EXISTS", again.Message, StringComparison.Ordinal);

            var read = Assert.Single(await Run("document_get", new() { ["collection"] = _collection, ["id"] = "1042, nope" })).Fields;
            Assert.Equal((true, (object?)null, "[\"kaffe\",2]", "{\"country\":\"se\"}", "2026-10-17T08:00:00Z"), ((bool?)read["paid"], read["none"], (string?)read["lines"], (string?)read["site"], (string?)read["at"]));

            var set = Assert.Single(await Run("document_upsert", new() { ["collection"] = _collection, ["id"] = "1042", ["fields"] = "{\"total\": 40, \"note x\": \"hej\"}" })).Fields;
            Assert.Equal((40L, "hej", "2", true), ((long?)set["total"], (string?)set["note x"], (string?)set["till"], (bool?)set["paid"])); // its other fields kept
            var replaced = Assert.Single(await Run("document_upsert", new() { ["collection"] = _collection, ["id"] = "1042", ["fields"] = "{\"till\": \"2\"}", ["replace"] = "true" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "1042", ["till"] = "2" }, replaced);

            for (var i = 1; i <= 4; i++)
            {
                await Run("document_upsert", new() { ["collection"] = _collection, ["id"] = "g" + i, ["fields"] = $"{{\"till\": \"{(i % 2 == 0 ? "2" : "1")}\", \"total\": {i}}}" }); // made where there is none
            }

            Assert.Equal(5, (await Run("documents", new() { ["collection"] = _collection })).Count);
            Assert.Equal(3, (await Run("documents", new() { ["collection"] = _collection, ["max"] = "3" })).Count);
            var twos = await Run("query", new() { ["query"] = $"{{\"structuredQuery\": {{\n  \"from\": [{{\"collectionId\": \"{_collection}\"}}],\n  \"where\": {{\"fieldFilter\": {{\"field\": {{\"fieldPath\": \"till\"}}, \"op\": \"EQUAL\", \"value\": {{\"stringValue\": \"2\"}}}}}}\n}}}}" });
            Assert.Equal(new[] { "1042", "g2", "g4" }, twos.Select(record => (string)record.Fields["id"]!).OrderBy(id => id, StringComparer.Ordinal));

            await Run("document_create", new() { ["collection"] = _collection + "/1042/lines", ["id"] = "1", ["fields"] = "{\"item\": \"bulle\"}" });
            Assert.Equal(("lines", $"{_collection}/1042/lines"), ((string?)Assert.Single(await Run("collections", new() { ["document"] = $"{_collection}/1042" })).Fields["name"], (string?)(await Run("collections", new() { ["document"] = $"{_collection}/1042" }))[0].Fields["path"]));
            Assert.Contains(await Run("collections", new()), collection => (string?)collection.Fields["name"] == _collection);

            Assert.True((bool)Assert.Single(await Run("document_delete", new() { ["collection"] = _collection, ["id"] = "g1" })).Fields["deleted"]!);
            Assert.Empty(await Run("document_get", new() { ["collection"] = _collection, ["id"] = "g1" }));
        }
    }
}
