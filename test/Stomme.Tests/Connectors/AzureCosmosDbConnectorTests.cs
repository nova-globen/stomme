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
using Stomme.Modules.Connectors.AzureCosmosDb;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Azure Cosmos DB connector's live tests' account (BL-229): COSMOSDB_TEST_HOST / PORT where set, otherwise this machine's
    /// throwaway Cosmos DB emulator that <c>scripts/cosmosdb-test-server.ps1</c> makes on 127.0.0.1:18081, with its database kafe.
    /// Its key is Microsoft's published well-known emulator key - the same in every install, no secret - and the emulator checks
    /// no signature (measured), so the signing is proven against Microsoft's worked example instead. An absent emulator is a
    /// FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class CosmosDbTestEndpoint
    {
        public const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        public const string Database = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("COSMOSDB_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("COSMOSDB_TEST_PORT"), out var port) ? port : 18081;

        public static string Endpoint => $"http://{Host}:{Port}";
    }

    /// <summary>
    /// BL-229 - Azure Cosmos DB (n8n's Azure Cosmos DB node) without an account: the master-key token as Microsoft's own worked
    /// example computes it, and every request signed over its verb, resource type, link and date with x-ms-date and the API
    /// version; every parameter held to its shape before anything is signed; containers listed, read, made - a partition key
    /// path or a hierarchical one, a manual or autoscale throughput, an indexing policy - and deleted; items read page by page by
    /// Cosmos DB's continuation up to the most asked, system fields left out unless asked; an item made with its partition key
    /// read from it by the container's paths, or upserted; read, deleted and updated by a partition key given or the id where the
    /// container is partitioned by /id; an update merged over the stored item under its ETag, or the item replaced whole; a query
    /// from the Query box, n8n's $1 as @Param1, its parameters bound, across partitions; Cosmos DB's code and message on a failure.
    /// </summary>
    public sealed class AzureCosmosDbConnectorTests
    {
        private const string Account = "https://kafe.documents.azure.com";

        // Microsoft's published emulator key - any base64 key would do; this one is no secret.
        private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = EmulatorKey };

        private static readonly Dictionary<string, string> Settings = new() { ["database"] = "kafe" };

        private static readonly DateTimeOffset Now = new(2026, 10, 17, 16, 30, 0, TimeSpan.Zero);

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed record Sent(string Method, string Url, string? Body, string? ContentType, Dictionary<string, string> Headers);

        private sealed class Cosmos : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public string PartitionPaths { get; set; } = "[\"/till\"]";

            public Func<Sent, (HttpStatusCode Status, string Body, string? Continuation)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, body, request.Content?.Headers.ContentType?.MediaType,
                    request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(",", header.Value)));
                Requests.Add(sent);
                var (status, answer, continuation) = sent.Method == "GET" && sent.Url.EndsWith("/colls/orders", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"id\":\"orders\",\"partitionKey\":{\"paths\":" + PartitionPaths + ",\"kind\":\"Hash\",\"version\":2}}", null)
                    : Answer(sent);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (continuation is not null)
                {
                    response.Headers.Add("x-ms-continuation", continuation);
                }

                return response;
            }
        }

        private static Task<List<ConnectorRecord>> Run(Cosmos cosmos, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new AzureCosmosDbConnector(new FixedClock(Now)), Contexts.Extract(new StubEgress(cosmos), Account, operation, parameters, settings ?? Settings, Credentials));

        private static string Signed(string verb, string type, string link) => AzureCosmosDbConnector.Token(EmulatorKey, verb, type, link, Now.UtcDateTime.ToString("r"));

        private const string Order = "{\"id\":\"1042\",\"till\":\"2\",\"total\":35.5,\"items\":[\"kaffe\",\"bulle\"],\"_rid\":\"AQAAAIEA\",\"_etag\":\"\\\"0100-etag\\\"\",\"_ts\":1792253498}";

        [Fact]
        public void It_declares_itself_as_n8ns_azure_cosmos_db_node()
        {
            IConnector connector = new AzureCosmosDbConnector();
            Assert.Equal(("azurecosmosdb", "Azure Cosmos DB", AuthenticationType.ApiKey, ConnectorEndpointKind.Url), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.Equal(new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory }, connector.Categories);
            Assert.Equal(new[] { "containers", "container_get", "container_create", "container_delete", "items", "item_get", "item_create", "item_update", "item_delete", "query" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "database" }, connector.ConfigSchema.Fields.Select(field => field.Name));
        }

        [Fact]
        public async Task The_token_is_microsofts_own_worked_example_and_every_request_is_signed_for_its_resource()
        {
            // Microsoft's example (learn.microsoft.com, "Access control in the Azure Cosmos DB SQL API"): its key, verb, type, link and date.
            var token = AzureCosmosDbConnector.Token("dsZQi3KtZmCv1ljt3VNWNm7sQUF1y5rJfC6kv5JiwvW0EndXdDku/dkKBp8/ufDToSxLzR4y+O/0H/t4bQtVNw==", "GET", "dbs", "dbs/ToDoList", "Thu, 27 Apr 2017 00:51:12 GMT");
            Assert.Equal("type=master&ver=1.0&sig=c09PEVJrgp2uQRkr934kFbTqhByc7TVr3OHyqlu+c+c=", Uri.UnescapeDataString(token));
            Assert.Equal(token.ToUpperInvariant(), "type%3dmaster%26ver%3d1.0%26sig%3dc09PEVJrgp2uQRkr934kFbTqhByc7TVr3OHyqlu%2bc%2bc%3d".ToUpperInvariant()); // escaped as Microsoft's is
            var refused = Assert.Throws<InvalidOperationException>(() => AzureCosmosDbConnector.Token("not base64!", "GET", "dbs", "", "x"));
            Assert.Equal("The Azure Cosmos DB key is not the account's key - it is base64, from the account's Keys.", refused.Message);

            var cosmos = new Cosmos { Answer = sent => (HttpStatusCode.OK, sent.Url.EndsWith("/docs/1042", StringComparison.Ordinal) ? Order : "{\"id\":\"orders\",\"partitionKey\":{\"paths\":[\"/till\"]}}", null) };
            await Run(cosmos, "item_get", new() { ["container"] = "orders", ["id"] = "1042", ["partition"] = "2" });
            var sent = cosmos.Requests[^1];
            Assert.Equal(("GET", Account + "/dbs/kafe/colls/orders/docs/1042"), (sent.Method, sent.Url));
            Assert.Equal(("Sat, 17 Oct 2026 16:30:00 GMT", "2018-12-31", Signed("GET", "docs", "dbs/kafe/colls/orders/docs/1042"), "[\"2\"]"),
                (sent.Headers["x-ms-date"], sent.Headers["x-ms-version"], sent.Headers["authorization"], sent.Headers["x-ms-documentdb-partitionkey"]));

            await Run(cosmos, "containers", new());
            Assert.Equal((Account + "/dbs/kafe/colls", Signed("GET", "colls", "dbs/kafe")), (cosmos.Requests[^1].Url, cosmos.Requests[^1].Headers["authorization"])); // a feed signed by its parent's link
            var tested = await new AzureCosmosDbConnector(new FixedClock(Now)).TestConnectionAsync(Contexts.Test(new StubEgress(new Cosmos { Answer = sent => (HttpStatusCode.OK, sent.Url.EndsWith("/colls", StringComparison.Ordinal) ? "{\"DocumentCollections\":[{\"id\":\"orders\"},{\"id\":\"guests\"}]}" : "{\"id\":\"kafe-account\"}", null) }), Account, Settings, Credentials));
            Assert.Equal((true, "Azure Cosmos DB: the database 'kafe' on kafe-account - 2 containers."), (tested.Success, tested.Message));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var cosmos = new Cosmos();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(cosmos, operation, parameters, settings));
                Assert.Empty(cosmos.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Azure Cosmos DB has no operation 'upsert' - containers, container_get,", await Refused("upsert", new()), StringComparison.Ordinal);
            Assert.Equal("Give the container's id (container).", await Refused("items", new()));
            Assert.Equal("'or ders' is not a container's id - letters, digits, - and _ (container).", await Refused("items", new() { ["container"] = "or ders" }));
            Assert.Equal("Give the item's id (id).", await Refused("item_get", new() { ["container"] = "orders" }));
            Assert.Equal("'10/42' is not an item id - at most 255 characters, none of / \\ ? # (id).", await Refused("item_delete", new() { ["container"] = "orders", ["id"] = "10/42" }));
            Assert.Equal("'[2' is not a partition key - a JSON array of one to three values, or text (partition).", await Refused("item_get", new() { ["container"] = "orders", ["id"] = "1", ["partition"] = "[2" }));
            Assert.Equal("'[]' is not a partition key - a JSON array of one to three values, or text (partition).", await Refused("item_get", new() { ["container"] = "orders", ["id"] = "1", ["partition"] = "[]" }));
            Assert.Equal("Give the item - a JSON object with its id (item).", await Refused("item_create", new() { ["container"] = "orders" }));
            Assert.Equal("The item is not a JSON object (item).", await Refused("item_create", new() { ["container"] = "orders", ["item"] = "[1]" }));
            Assert.Equal("Give the item its id - {\"id\": \"1042\", ...} (item).", await Refused("item_create", new() { ["container"] = "orders", ["item"] = "{\"till\": \"2\"}" }));
            Assert.Equal("'maybe' is not true or false (upsert).", await Refused("item_create", new() { ["container"] = "orders", ["item"] = "{\"id\": \"1\"}", ["upsert"] = "maybe" }));
            Assert.Equal("The item's id is '1042' - its fields cannot give it another ('1043').", await Refused("item_update", new() { ["container"] = "orders", ["id"] = "1042", ["item"] = "{\"id\": \"1043\"}" }));
            Assert.Equal("Give the fields that change - a JSON object (item).", await Refused("item_update", new() { ["container"] = "orders", ["id"] = "1042" }));
            Assert.Equal("'till' is not a partition key path - /till, or up to three as /tenant,/till (partition_key).", await Refused("container_create", new() { ["container"] = "orders", ["partition_key"] = "till" }));
            Assert.Equal("'/a,/b,/c,/d' is not a partition key path - /till, or up to three as /tenant,/till (partition_key).", await Refused("container_create", new() { ["container"] = "orders", ["partition_key"] = "/a,/b,/c,/d" }));
            Assert.Equal("'399' is not a whole number of RU/s, 400 or more (throughput).", await Refused("container_create", new() { ["container"] = "orders", ["throughput"] = "399" }));
            Assert.Equal("'999' is not a whole number of RU/s, 1000 or more (max_throughput).", await Refused("container_create", new() { ["container"] = "orders", ["max_throughput"] = "999" }));
            Assert.Equal("Give a manual throughput or an autoscale most, not both.", await Refused("container_create", new() { ["container"] = "orders", ["throughput"] = "400", ["max_throughput"] = "1000" }));
            Assert.Equal("The indexing_policy is not a JSON object (indexing_policy).", await Refused("container_create", new() { ["container"] = "orders", ["indexing_policy"] = "consistent" }));
            Assert.Equal("Give the query - or write it in the step's Query box.", await Refused("query", new() { ["container"] = "orders" }));
            Assert.Equal("'1min' is not a parameter's name - @min (parameters).", await Refused("query", new() { ["container"] = "orders", ["sql"] = "SELECT * FROM c", ["parameters"] = "{\"1min\": 10}" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("items", new() { ["container"] = "orders", ["max"] = "0" }));
            Assert.Equal("Give the connection its database (database).", await Refused("containers", new(), new()));
            Assert.Equal("'kafe db' is not a database's id - letters, digits, - and _ (database).", await Refused("containers", new(), new() { ["database"] = "kafe db" }));
        }

        [Fact]
        public async Task Containers_are_listed_read_made_and_deleted()
        {
            var cosmos = new Cosmos
            {
                Answer = sent => (sent.Method, sent.Url) switch
                {
                    ("GET", var url) when url.EndsWith("/colls", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"_count\":2,\"DocumentCollections\":[{\"id\":\"orders\",\"partitionKey\":{\"paths\":[\"/till\"],\"kind\":\"Hash\"},\"_rid\":\"x\"},{\"id\":\"guests\"}]}", null),
                    ("POST", _) => (HttpStatusCode.Created, "{\"id\":\"orders\",\"partitionKey\":{\"paths\":[\"/tenant\",\"/till\"],\"kind\":\"MultiHash\",\"version\":2}}", null),
                    ("DELETE", _) => (HttpStatusCode.NoContent, "", null),
                    _ => (HttpStatusCode.OK, "{\"id\":\"orders\",\"partitionKey\":{\"paths\":[\"/till\"]}}", null),
                },
            };
            var listed = await Run(cosmos, "containers", new() { ["max"] = "1" });
            Assert.Equal(("orders", "{\"paths\":[\"/till\"],\"kind\":\"Hash\"}", "x"), ((string?)Assert.Single(listed).Fields["id"], (string?)listed[0].Fields["partitionKey"], (string?)listed[0].Fields["_rid"])); // a container's own fields kept whole

            Assert.Equal("orders", Assert.Single(await Run(cosmos, "container_get", new() { ["container"] = "orders" })).Fields["id"]);
            Assert.Equal(Signed("GET", "colls", "dbs/kafe/colls/orders"), cosmos.Requests[^1].Headers["authorization"]);

            await Run(cosmos, "container_create", new() { ["container"] = "orders", ["partition_key"] = "/tenant, /till", ["max_throughput"] = "4000", ["indexing_policy"] = "{\"indexingMode\": \"consistent\"}" });
            var made = cosmos.Requests[^1];
            Assert.Equal(("POST", Account + "/dbs/kafe/colls", Signed("POST", "colls", "dbs/kafe")), (made.Method, made.Url, made.Headers["authorization"]));
            Assert.Equal("{\"id\":\"orders\",\"partitionKey\":{\"paths\":[\"/tenant\",\"/till\"],\"kind\":\"MultiHash\",\"version\":2},\"indexingPolicy\":{\"indexingMode\":\"consistent\"}}", made.Body);
            Assert.Equal("{\"maxThroughput\":4000}", made.Headers["x-ms-cosmos-offer-autopilot-settings"]);
            Assert.False(made.Headers.ContainsKey("x-ms-offer-throughput"));

            await Run(cosmos, "container_create", new() { ["container"] = "guests", ["throughput"] = "400" });
            Assert.Equal(("{\"id\":\"guests\",\"partitionKey\":{\"paths\":[\"/id\"],\"kind\":\"Hash\",\"version\":2}}", "400"), (cosmos.Requests[^1].Body, cosmos.Requests[^1].Headers["x-ms-offer-throughput"])); // /id, n8n's default

            Assert.Equal(new Dictionary<string, object?> { ["id"] = "orders", ["deleted"] = true }, Assert.Single(await Run(cosmos, "container_delete", new() { ["container"] = "orders" })).Fields);
            Assert.Equal(("DELETE", Account + "/dbs/kafe/colls/orders", Signed("DELETE", "colls", "dbs/kafe/colls/orders")), (cosmos.Requests[^1].Method, cosmos.Requests[^1].Url, cosmos.Requests[^1].Headers["authorization"]));
        }

        [Fact]
        public async Task Items_are_read_page_by_page_up_to_the_most_their_system_fields_left_out()
        {
            string Page(int from, int count) => "{\"Documents\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"" + i + "\",\"till\":\"1\",\"_rid\":\"r" + i + "\",\"_ts\":1}")) + "]}";
            var cosmos = new Cosmos
            {
                Answer = sent => sent.Headers.TryGetValue("x-ms-continuation", out var token)
                    ? token == "page-2" ? (HttpStatusCode.OK, Page(100, 60), "page-3") : (HttpStatusCode.OK, Page(160, 10), null)
                    : (HttpStatusCode.OK, Page(0, 100), "page-2"),
            };
            var all = await Run(cosmos, "items", new() { ["container"] = "orders" });
            Assert.Equal(170, all.Count);
            Assert.Equal(new[] { "100", "100", "100" }, cosmos.Requests.Select(sent => sent.Headers["x-ms-max-item-count"]));
            Assert.Equal(new[] { (string?)null, "page-2", "page-3" }, cosmos.Requests.Select(sent => sent.Headers.GetValueOrDefault("x-ms-continuation")));
            Assert.Equal(Signed("GET", "docs", "dbs/kafe/colls/orders"), cosmos.Requests[0].Headers["authorization"]);
            Assert.Equal(new[] { "id", "till" }, all[0].Fields.Keys); // simplified: no _rid, no _ts

            cosmos.Requests.Clear();
            var some = await Run(cosmos, "items", new() { ["container"] = "orders", ["max"] = "120", ["simple"] = "false" });
            Assert.Equal(120, some.Count);
            Assert.Equal(new[] { "100", "20" }, cosmos.Requests.Select(sent => sent.Headers["x-ms-max-item-count"])); // a page no larger than what is still wanted
            Assert.Equal("r0", some[0].Fields["_rid"]);
        }

        [Fact]
        public async Task An_item_is_made_with_its_partition_key_read_from_it_and_read_and_deleted_by_one_given()
        {
            var cosmos = new Cosmos
            {
                PartitionPaths = "[\"/site/country\",\"/till\"]",
                Answer = sent => sent.Method switch
                {
                    "POST" => (HttpStatusCode.Created, Order, null),
                    "DELETE" => (HttpStatusCode.NoContent, "", null),
                    _ => (HttpStatusCode.OK, sent.Url.Contains("/docs/", StringComparison.Ordinal) ? Order : "{}", null),
                },
            };
            var made = Assert.Single(await Run(cosmos, "item_create", new() { ["container"] = "orders", ["item"] = "{\"id\": \"1042\", \"site\": {\"country\": \"se\"}, \"till\": 2}", ["upsert"] = "true" })).Fields;
            Assert.Equal(new[] { "GET", "POST" }, cosmos.Requests.Select(sent => sent.Method)); // the container's paths read first
            var created = cosmos.Requests[^1];
            Assert.Equal(("[\"se\",2]", "True", Signed("POST", "docs", "dbs/kafe/colls/orders")), (created.Headers["x-ms-documentdb-partitionkey"], created.Headers["x-ms-documentdb-is-upsert"], created.Headers["authorization"])); // each path's value, its JSON type kept
            Assert.Equal("{\"id\":\"1042\",\"site\":{\"country\":\"se\"},\"till\":2}", created.Body);
            Assert.Equal(new[] { "id", "till", "total", "items" }, made.Keys);
            Assert.Equal("[\"kaffe\",\"bulle\"]", made["items"]);

            Assert.Equal("[{},null]", AzureCosmosDbConnector.PartitionFromItem(new JsonObject { ["site"] = new JsonObject(), ["till"] = null }, new[] { "/site/country", "/till" })); // a path the item lacks: undefined; a null: itself
            cosmos.Requests.Clear();
            await Run(cosmos, "item_get", new() { ["container"] = "orders", ["id"] = "1042", ["partition"] = "[\"se\", 2]" });
            Assert.Equal(new[] { "GET" }, cosmos.Requests.Select(sent => sent.Method)); // a partition given: no read of the container
            Assert.Equal("[\"se\",2]", cosmos.Requests[^1].Headers["x-ms-documentdb-partitionkey"]);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(cosmos, "item_delete", new() { ["container"] = "orders", ["id"] = "1042" }));
            Assert.Equal("Give its partition key value (partition) - the container is not partitioned by /id.", refused.Message);

            cosmos.PartitionPaths = "[\"/id\"]";
            var deleted = Assert.Single(await Run(cosmos, "item_delete", new() { ["container"] = "orders", ["id"] = "kassa å" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "kassa å", ["deleted"] = true }, deleted);
            var removal = cosmos.Requests[^1];
            Assert.Equal(("DELETE", Account + "/dbs/kafe/colls/orders/docs/kassa%20%C3%A5", "[\"kassa å\"]", Signed("DELETE", "docs", "dbs/kafe/colls/orders/docs/kassa å")), (removal.Method, removal.Url, removal.Headers["x-ms-documentdb-partitionkey"], removal.Headers["authorization"])); // the id its own key; escaped in the address, as itself in the signature
            await Run(cosmos, "item_delete", new() { ["container"] = "orders", ["id"] = "rabatt 50%25" });
            Assert.Equal(Account + "/dbs/kafe/colls/orders/docs/rabatt%2050%2525", cosmos.Requests[^1].Url); // an id that looks escaped is escaped again, so the server reads it as written
        }

        [Fact]
        public async Task An_update_is_merged_over_the_stored_item_under_its_etag_or_replaces_it_whole()
        {
            var cosmos = new Cosmos { Answer = sent => (HttpStatusCode.OK, sent.Method == "PUT" ? sent.Body! : Order, null) };
            var updated = Assert.Single(await Run(cosmos, "item_update", new() { ["container"] = "orders", ["id"] = "1042", ["partition"] = "2", ["item"] = "{\"total\": 40, \"paid\": true}" })).Fields;
            Assert.Equal(new[] { "GET", "PUT" }, cosmos.Requests.Select(sent => sent.Method));
            var put = cosmos.Requests[^1];
            Assert.Equal(("\"0100-etag\"", "[\"2\"]", Signed("PUT", "docs", "dbs/kafe/colls/orders/docs/1042")), (put.Headers["if-match"], put.Headers["x-ms-documentdb-partitionkey"], put.Headers["authorization"]));
            Assert.Equal((40L, true, "[\"kaffe\",\"bulle\"]"), ((long?)updated["total"], (bool?)updated["paid"], (string?)updated["items"])); // its other fields kept

            cosmos.Requests.Clear();
            await Run(cosmos, "item_update", new() { ["container"] = "orders", ["id"] = "1042", ["partition"] = "2", ["item"] = "{\"till\": \"2\", \"total\": 1}", ["replace"] = "true" });
            Assert.Equal(new[] { "PUT" }, cosmos.Requests.Select(sent => sent.Method));
            Assert.Equal("{\"till\":\"2\",\"total\":1,\"id\":\"1042\"}", cosmos.Requests[^1].Body); // whole, its id given it
            Assert.False(cosmos.Requests[^1].Headers.ContainsKey("if-match"));
        }

        [Fact]
        public async Task A_query_is_sent_from_the_query_box_with_its_parameters_across_partitions_page_by_page()
        {
            var cosmos = new Cosmos { Answer = sent => sent.Headers.ContainsKey("x-ms-continuation") ? (HttpStatusCode.OK, "{\"Documents\":[{\"id\":\"1043\",\"total\":12}]}", null) : (HttpStatusCode.OK, "{\"Documents\":[{\"id\":\"1042\",\"total\":40}]}", "next") };
            var found = await Run(cosmos, "query", new() { ["container"] = "orders", ["query"] = "SELECT c.id, c.total FROM c\nWHERE c.total > $1 AND c.till = @till", ["parameters"] = "{\"Param1\": 10, \"@till\": \"2\"}" });
            Assert.Equal(new[] { "1042", "1043" }, found.Select(record => (string)record.Fields["id"]!));
            var first = cosmos.Requests[0];
            Assert.Equal(("POST", Account + "/dbs/kafe/colls/orders/docs", "application/query+json", Signed("POST", "docs", "dbs/kafe/colls/orders")), (first.Method, first.Url, first.ContentType, first.Headers["authorization"]));
            Assert.Equal(("True", "True"), (first.Headers["x-ms-documentdb-isquery"], first.Headers["x-ms-documentdb-query-enablecrosspartition"]));
            Assert.Equal("{\"query\":\"SELECT c.id, c.total FROM c\\nWHERE c.total > @Param1 AND c.till = @till\",\"parameters\":[{\"name\":\"@Param1\",\"value\":10},{\"name\":\"@till\",\"value\":\"2\"}]}", first.Body); // n8n's $1 as @Param1
            Assert.Equal("next", cosmos.Requests[1].Headers["x-ms-continuation"]);
            await Run(cosmos, "query", new() { ["container"] = "orders", ["sql"] = "SELECT VALUE COUNT(1) FROM c", ["query"] = "ignored" });
            Assert.Contains("SELECT VALUE COUNT(1) FROM c", cosmos.Requests[^1].Body, StringComparison.Ordinal); // the parameter before the box
        }

        [Fact]
        public async Task A_failure_is_said_with_cosmos_dbs_code_and_message()
        {
            async Task<string> Failed(HttpStatusCode status, string body) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Cosmos { Answer = _ => (status, body, null) }, "containers", new()))).Message;

            Assert.Equal("Azure Cosmos DB answered HTTP 409 Conflict: The document already exists in the collection.", await Failed(HttpStatusCode.Conflict, "{\"code\":\"Conflict\",\"message\":\"The document already exists in the collection.\"}"));
            Assert.Equal("Azure Cosmos DB answered HTTP 401 Unauthorized: The input authorization token can't serve the request. - check the key.", await Failed(HttpStatusCode.Unauthorized, "{\"code\":\"Unauthorized\",\"message\":\"The input authorization token can't serve the request.\\r\\nActivityId: 7a1b\"}"));
            Assert.Equal("Azure Cosmos DB answered HTTP 404 NotFound: Resource Not Found. Learn more: https://aka.ms/cosmosdb-tsg-not-found", await Failed(HttpStatusCode.NotFound, "{\"code\":\"NotFound\",\"message\":\"Resource Not Found. Learn more: https://aka.ms/cosmosdb-tsg-not-found\"}"));
            Assert.Equal("Azure Cosmos DB answered HTTP 502.", await Failed(HttpStatusCode.BadGateway, ""));
            Assert.Equal("Azure Cosmos DB answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
        }
    }

    /// <summary>
    /// BL-229 - the Azure Cosmos DB connector against Microsoft's Linux emulator (<c>scripts/cosmosdb-test-server.ps1</c>'s throwaway
    /// on loopback) through the HOST's egress: loopback refused until allowlisted; a test naming the database; a container made
    /// partitioned by /till, items made in two partitions, a duplicate refused in Cosmos DB's words and upserted instead; read by
    /// their partition, read page by page and held to the most, queried with a parameter across partitions; one updated with its
    /// other fields kept, one replaced whole, one deleted; a container partitioned by /id read and deleted by the id alone; the
    /// containers dropped. The emulator checks no signature (measured), so this proves the protocol, not the key. Fails, never
    /// skips, when the emulator is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AzureCosmosDbConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["api-key"] = CosmosDbTestEndpoint.Key };

        private static Dictionary<string, string> Settings => new() { ["database"] = CosmosDbTestEndpoint.Database };

        private static HostConnectorEgress Egress => new(new[] { CosmosDbTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AzureCosmosDbConnector(), Contexts.Extract(Egress, CosmosDbTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30));

        [Fact]
        public async Task A_test_names_the_database_and_loopback_is_refused_until_allowed()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AzureCosmosDbConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), CosmosDbTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var right = await new AzureCosmosDbConnector().TestConnectionAsync(Contexts.Test(Egress, CosmosDbTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.StartsWith("Azure Cosmos DB: the database 'kafe' on ", right.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Items_are_made_read_queried_updated_and_deleted_in_their_partitions()
        {
            var container = "orders" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Assert.Equal(container, Assert.Single(await Run("container_create", new() { ["container"] = container, ["partition_key"] = "/till" })).Fields["id"]);
                Assert.Contains(await Run("containers", new()), found => (string?)found.Fields["id"] == container);
                await Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"1042\", \"till\": \"2\", \"total\": 35.5, \"items\": [\"kaffe\", \"bulle\"]}" });
                await Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"1043\", \"till\": \"1\", \"total\": 12}" });
                await Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"1044\", \"till\": \"2\", \"total\": 7}" });
                var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"1042\", \"till\": \"2\"}" }));
                output.WriteLine($"[live] {duplicate.Message}");
                Assert.StartsWith("Azure Cosmos DB answered HTTP 409 Conflict", duplicate.Message, StringComparison.Ordinal);
                Assert.Equal(40L, Assert.Single(await Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"1042\", \"till\": \"2\", \"total\": 40, \"items\": [\"kaffe\", \"bulle\"]}", ["upsert"] = "true" })).Fields["total"]);

                var read = Assert.Single(await Run("item_get", new() { ["container"] = container, ["id"] = "1042", ["partition"] = "2" })).Fields;
                Assert.Equal((40L, "[\"kaffe\",\"bulle\"]"), ((long?)read["total"], (string?)read["items"]));
                Assert.DoesNotContain("_rid", read.Keys);
                var wrongPartition = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("item_get", new() { ["container"] = container, ["id"] = "1042", ["partition"] = "1" }));
                Assert.StartsWith("Azure Cosmos DB answered HTTP 404 NotFound", wrongPartition.Message, StringComparison.Ordinal);

                Assert.Equal(new[] { "1042", "1043", "1044" }, (await Run("items", new() { ["container"] = container })).Select(item => (string)item.Fields["id"]!).OrderBy(id => id));
                Assert.Equal(2, (await Run("items", new() { ["container"] = container, ["max"] = "2" })).Count);
                Assert.True((await Run("items", new() { ["container"] = container, ["simple"] = "false" })).All(item => item.Fields.ContainsKey("_etag")));

                var queried = await Run("query", new() { ["container"] = container, ["query"] = "SELECT c.id, c.total FROM c\nWHERE c.total > @min", ["parameters"] = "{\"@min\": 10}" });
                Assert.Equal(new[] { "1042", "1043" }, queried.Select(item => (string)item.Fields["id"]!).OrderBy(id => id)); // across both partitions

                var merged = Assert.Single(await Run("item_update", new() { ["container"] = container, ["id"] = "1043", ["partition"] = "1", ["item"] = "{\"total\": 13, \"paid\": true}" })).Fields;
                Assert.Equal((13L, true, "1"), ((long?)merged["total"], (bool?)merged["paid"], (string?)merged["till"])); // its other fields kept
                var replaced = Assert.Single(await Run("item_update", new() { ["container"] = container, ["id"] = "1044", ["partition"] = "2", ["item"] = "{\"till\": \"2\", \"note\": \"void\"}", ["replace"] = "true" })).Fields;
                Assert.Equal(("void", false), ((string?)replaced["note"], replaced.ContainsKey("total")));
                Assert.True((bool)Assert.Single(await Run("item_delete", new() { ["container"] = container, ["id"] = "1044", ["partition"] = "2" })).Fields["deleted"]!);
                Assert.Equal(2, (await Run("items", new() { ["container"] = container })).Count);
            }
            finally
            {
                await Run("container_delete", new() { ["container"] = container });
            }
        }

        [Fact]
        public async Task A_container_partitioned_by_its_ids_is_read_and_deleted_by_the_id_alone()
        {
            var container = "guests" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await Run("container_create", new() { ["container"] = container });
                await Run("item_create", new() { ["container"] = container, ["item"] = "{\"id\": \"ada\", \"name\": \"Ada Kafé\"}" });
                Assert.Equal("Ada Kafé", Assert.Single(await Run("item_get", new() { ["container"] = container, ["id"] = "ada" })).Fields["name"]);
                Assert.True((bool)Assert.Single(await Run("item_delete", new() { ["container"] = container, ["id"] = "ada" })).Fields["deleted"]!);
                Assert.Empty(await Run("items", new() { ["container"] = container }));
            }
            finally
            {
                Assert.True((bool)Assert.Single(await Run("container_delete", new() { ["container"] = container })).Fields["deleted"]!);
            }
        }
    }
}
