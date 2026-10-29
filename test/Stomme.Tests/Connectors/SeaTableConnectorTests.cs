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
using Stomme.Modules.Connectors.SeaTable;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// SeaTable (n8n's SeaTable node) without a server: the API token exchanged for the base's access token, every
    /// call under the connection's own address and never the server SeaTable names; every field held to its column by the
    /// table's metadata before a write is sent - a word in a number, a date that is none, a select option the column does not
    /// offer (SeaTable would add it), a read-only column, a field the table has not got (SeaTable would drop it); a row made and
    /// changed and read back by its columns' names, a change reading the row first; a search's term BOUND as a parameter, never
    /// spliced (n8n's escaping breaks on a quote); rows page by page; a delete saying whether it deleted; links by their
    /// column's name; a snapshot refused for being too soon said as a failure; SeaTable's messages; a test naming the base.
    /// </summary>
    public sealed class SeaTableConnectorTests
    {
        private const string Address = "https://seatable.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "api-token-1" };

        private const string Ada = "ZWFP4GdRT5GvObBD-FPLuQ";

        private const string Lund = "HCOrOKHHTIGUjXYQjNdexA";

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Server : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)?> answer;

            public Server(Func<Sent, (HttpStatusCode Status, string Body)?>? answer = null) => this.answer = answer ?? (_ => null);

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                var (status, text) = answer(sent) ?? Default(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }

            private static (HttpStatusCode, string) Default(Sent sent) =>
                sent.Url.Contains("/app-access-token/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"access_token\":\"base-token-1\",\"dtable_uuid\":\"uuid-1\",\"dtable_name\":\"Kafe\",\"dtable_server\":\"http://seatable.internal/api-gateway/\"}")
                : sent.Url.EndsWith("/metadata/", StringComparison.Ordinal) ? (HttpStatusCode.OK, Metadata)
                : sent.Method == "GET" && sent.Url.Contains("/rows/" + Ada, StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"_id\":\"" + Ada + "\",\"Name\":\"Ada\",\"Qty\":3}")
                : sent.Method == "GET" && sent.Url.Contains("/rows/", StringComparison.Ordinal) && !sent.Url.Contains("/rows/?", StringComparison.Ordinal) ? (HttpStatusCode.NotFound, "{\"error_message\":\"row _id not exits\"}")
                : sent.Method == "POST" && sent.Url.EndsWith("/rows/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"inserted_row_count\":1,\"row_ids\":[{\"_id\":\"" + Ada + "\"}],\"first_row\":{\"0000\":\"Ada\"}}")
                : sent.Method == "DELETE" && sent.Url.EndsWith("/rows/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"deleted_rows\":1}")
                : (HttpStatusCode.OK, "{\"success\":true}");
        }

        // The base Kafe as SeaTable 6.2 describes it (measured): Orders - text, number, checkbox, date, a select, a link to
        // Customers and a formula - and Customers.
        private const string Metadata = "{\"metadata\":{\"tables\":[{\"_id\":\"dwEe\",\"name\":\"Orders\",\"columns\":["
            + "{\"key\":\"0000\",\"name\":\"Name\",\"type\":\"text\"},{\"key\":\"2qxJ\",\"name\":\"Qty\",\"type\":\"number\",\"data\":{\"format\":\"number\"}},{\"key\":\"Q7fD\",\"name\":\"Paid\",\"type\":\"checkbox\"},"
            + "{\"key\":\"oAcu\",\"name\":\"Due\",\"type\":\"date\",\"data\":{\"format\":\"YYYY-MM-DD\"}},{\"key\":\"1AsR\",\"name\":\"Kind\",\"type\":\"single-select\",\"data\":{\"options\":[{\"name\":\"Kaffe\",\"id\":\"1\"},{\"name\":\"Bulle\",\"id\":\"2\"}]}},"
            + "{\"key\":\"tGs9\",\"name\":\"Tags\",\"type\":\"multiple-select\",\"data\":{\"options\":[{\"name\":\"stamkund\",\"id\":\"1\"}]}},"
            + "{\"key\":\"4oiJ\",\"name\":\"Customer\",\"type\":\"link\",\"data\":{\"link_id\":\"IP5k\",\"table_id\":\"dwEe\",\"other_table_id\":\"q4Y7\"}},{\"key\":\"Tt0t\",\"name\":\"Total\",\"type\":\"formula\"},"
            + "{\"key\":\"ImG1\",\"name\":\"Receipt\",\"type\":\"image\"}],\"views\":[{\"name\":\"Default View\"},{\"name\":\"Unpaid\"}]},"
            + "{\"_id\":\"q4Y7\",\"name\":\"Customers\",\"columns\":[{\"key\":\"0000\",\"name\":\"Name\",\"type\":\"text\"},{\"key\":\"Nt5s\",\"name\":\"Ada`s note\",\"type\":\"text\"},"
            + "{\"key\":\"q8Pm\",\"name\":\"Orders\",\"type\":\"link\",\"data\":{\"link_id\":\"IP5k\",\"table_id\":\"dwEe\",\"other_table_id\":\"q4Y7\"}}],\"views\":[{\"name\":\"Default View\"}]}]}}";

        private static Task<List<ConnectorRecord>> Run(Server server, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new SeaTableConnector(), Contexts.Extract(new StubEgress(server), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Gateway(string route) => Address + "/api-gateway/api/v2/dtables/uuid-1/" + route;

        [Fact]
        public void It_declares_itself_as_n8ns_seatable_node()
        {
            IConnector connector = new SeaTableConnector();
            Assert.Equal(("seatable", "SeaTable", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            var names = ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name).ToList();
            Assert.Equal(15, names.Count);
            Assert.Equal(new[] { "rows", "row", "rows_search", "row_create", "row_update", "row_delete", "row_lock", "row_unlock" }, names.Take(8));
        }

        [Fact]
        public async Task Every_field_is_held_to_its_column_before_a_write_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var server = new Server();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(server, operation, parameters, endpoint));
                Assert.DoesNotContain(server.Requests, sent => sent.Method != "GET" || (sent.Url.Contains("/rows/", StringComparison.Ordinal) && !sent.Url.Contains("/rows/" + Ada, StringComparison.Ordinal))); // at most the token, the metadata and a read
                return refusal.Message;
            }

            Assert.StartsWith("SeaTable has no operation 'row_archive'", await Refused("row_archive", new()));
            Assert.Equal("Give the table - its name, Orders (table).", await Refused("rows", new()));
            Assert.Equal("The base has no table 'Order' - it has Orders, Customers (table).", await Refused("rows", new() { ["table"] = "Order" }));
            Assert.Equal("The table Orders has no column 'Quantity' (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Quantity\": 3}" })); // SeaTable drops it without a word
            Assert.Equal("The table Orders has no column 'Nam' - Name? (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Nam\": \"Ada\"}" }));
            Assert.Equal("Qty is a number column - '\"many\"' is no number (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Qty\": \"many\"}" })); // SeaTable empties it
            Assert.Equal("Qty is a number column - '\"Infinity\"' is no number (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Qty\": \"Infinity\"}" }));
            Assert.Equal("Paid is a checkbox - true or false (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Paid\": \"ja\"}" }));
            Assert.Equal("Due is a date column - 2026-10-21, or 2026-10-21 08:00 (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Due\": \"igår\"}" }));
            Assert.Equal("Kind offers \"Kaffe\", \"Bulle\" - SeaTable would add another option to the column (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Kind\": \"Te\"}" }));
            Assert.Equal("Tags is a list of \"stamkund\" - SeaTable would add another option to the column (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Tags\": [\"ny\"]}" }));
            Assert.Equal("Customer is a link column - link rows with link_add (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Customer\": [\"" + Lund + "\"]}" }));
            Assert.Equal("Total is a formula column - SeaTable fills it itself (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Total\": 1}" }));
            Assert.Equal("Receipt is a image column - an asset is uploaded to the base, not written here (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Receipt\": \"x.png\"}" }));
            Assert.Equal("Name is a text column - give it a value, not a list or an object (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{\"Name\": {\"a\": 1}}" }));
            Assert.Equal("The fields are not JSON - {\"Name\": \"Ada\"} (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "Name = Ada" }));
            Assert.Equal("Give at least one field (fields).", await Refused("row_create", new() { ["table"] = "Orders", ["fields"] = "{}" }));
            Assert.Equal("'ada' is not a row's id - 22 letters and digits (row).", await Refused("row_update", new() { ["table"] = "Orders", ["row"] = "ada", ["fields"] = "{\"Qty\": 1}" }));
            Assert.Equal("The table Orders has no view 'Paid' - it has Default View, Unpaid (view).", await Refused("rows", new() { ["table"] = "Orders", ["view"] = "Paid" }));
            Assert.Equal("The table Orders has no column 'Namn' (column).", await Refused("rows_search", new() { ["table"] = "Orders", ["column"] = "Namn", ["term"] = "Ada" }));
            Assert.Equal("Give the search - one line (term).", await Refused("rows_search", new() { ["table"] = "Orders", ["column"] = "Name", ["term"] = "a\nb" }));
            Assert.Equal("Qty is no link column - it is a number (column).", await Refused("link_add", new() { ["table"] = "Orders", ["column"] = "Qty", ["row"] = Ada, ["other_rows"] = Lund }));
            Assert.Equal("'lund' is not a row's id - 22 letters and digits (other_rows).", await Refused("link_add", new() { ["table"] = "Orders", ["column"] = "Customer", ["row"] = Ada, ["other_rows"] = "lund" }));
            Assert.Equal("Give the asset's path in the base - /images/2026-10/receipt.png (path).", await Refused("asset_link", new() { ["path"] = "/images/../../etc/passwd" }));
            Assert.Equal("'kanske' is not true or false (exact).", await Refused("rows_search", new() { ["table"] = "Orders", ["column"] = "Name", ["term"] = "Ada", ["exact"] = "kanske" }));
            Assert.Equal("'https://seatable.kafe.example/dtable/1' is not SeaTable's address - https://cloud.seatable.io, its server alone.", await Refused("rows", new() { ["table"] = "Orders" }, "https://seatable.kafe.example/dtable/1"));
            Assert.Equal("The SeaTable connection has no API token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SeaTableConnector(), Contexts.Extract(new StubEgress(new Server()), Address, "rows", new Dictionary<string, string> { ["table"] = "Orders" }, new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_row_is_made_and_changed_under_the_connections_address_and_read_back_by_its_names()
        {
            var server = new Server();
            var made = Assert.Single(await Run(server, "row_create", new() { ["table"] = "Orders", ["query"] = "{\"Name\": \"Ada\", \"Qty\": \"3\", \"Paid\": true, \"Due\": \"2026-11-01\", \"Kind\": \"Kaffe\", \"Tags\": [\"stamkund\"]}" })).Fields;
            Assert.Equal((Ada, "Ada", 3L), ((string?)made["_id"], (string?)made["Name"], (long?)made["Qty"]));
            Assert.Equal(new[] { ("GET", Address + "/api/v2.1/dtable/app-access-token/", "Token api-token-1"), ("GET", Gateway("metadata/"), "Token base-token-1"), ("POST", Gateway("rows/"), "Token base-token-1"), ("GET", Gateway($"rows/{Ada}/?table_name=Orders&convert_keys=true"), "Token base-token-1") },
                server.Requests.Select(sent => (sent.Method, sent.Url, sent.Authorization))); // never the server SeaTable names (seatable.internal)
            Assert.Equal("{\"table_name\":\"Orders\",\"rows\":[{\"Name\":\"Ada\",\"Qty\":3,\"Paid\":true,\"Due\":\"2026-11-01\",\"Kind\":\"Kaffe\",\"Tags\":[\"stamkund\"]}]}", server.Requests[2].Body);

            var changed = new Server();
            await Run(changed, "row_update", new() { ["table"] = "Orders", ["row"] = Ada, ["fields"] = "{\"Qty\": 4}" });
            Assert.Equal(new[] { ("GET", Gateway($"rows/{Ada}/?table_name=Orders&convert_keys=true")), ("PUT", Gateway("rows/")), ("GET", Gateway($"rows/{Ada}/?table_name=Orders&convert_keys=true")) }, changed.Requests.Skip(2).Select(sent => (sent.Method, sent.Url)));
            Assert.Equal("{\"table_name\":\"Orders\",\"updates\":[{\"row_id\":\"" + Ada + "\",\"row\":{\"Qty\":4}}]}", changed.Requests[3].Body);
            var missing = new Server();
            Assert.Equal("The table Orders has no row JwUOOPa0T1SU34YXyxaF0A (row).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "row_update", new() { ["table"] = "Orders", ["row"] = "JwUOOPa0T1SU34YXyxaF0A", ["fields"] = "{\"Qty\": 4}" }))).Message); // SeaTable: "base not found in big data storage"
            Assert.DoesNotContain(missing.Requests, sent => sent.Method == "PUT");
        }

        [Fact]
        public async Task A_search_binds_its_term_and_rows_go_page_by_page()
        {
            var search = new Server(sent => sent.Url.EndsWith("/sql/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"results\":[{\"_id\":\"" + Ada + "\",\"Name\":\"O'Brien\"}]}") : null);
            Assert.Equal("O'Brien", (string?)Assert.Single(await Run(search, "rows_search", new() { ["table"] = "Orders", ["column"] = "Name", ["term"] = "O'Brien", ["max"] = "5" })).Fields["Name"]);
            Assert.Equal("{\"sql\":\"SELECT * FROM `Orders` WHERE `Name` LIKE ? LIMIT 5\",\"parameters\":[\"%O'Brien%\"],\"convert_keys\":true}", search.Requests[^1].Body); // bound - n8n's escaping found nothing for O'Brien
            await Run(search, "rows_search", new() { ["table"] = "Orders", ["column"] = "Name", ["term"] = "\" OR 1=1", ["exact"] = "true", ["ignore_case"] = "true" });
            Assert.Equal("{\"sql\":\"SELECT * FROM `Orders` WHERE lower(`Name`) = ? LIMIT 50\",\"parameters\":[\"\\\" or 1=1\"],\"convert_keys\":true}", search.Requests[^1].Body);
            await Run(search, "rows_search", new() { ["table"] = "Customers", ["column"] = "Ada`s note", ["term"] = "x" });
            Assert.Equal("{\"sql\":\"SELECT * FROM `Customers` WHERE `Ada``s note` LIKE ? LIMIT 50\",\"parameters\":[\"%x%\"],\"convert_keys\":true}", search.Requests[^1].Body); // a backtick in a name doubled

            var page = "{\"rows\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"{{\"_id\":\"r{i}\"}}")) + "]}";
            var rows = new Server(sent => sent.Url.Contains("/rows/?", StringComparison.Ordinal) ? (HttpStatusCode.OK, sent.Url.Contains("start=0&", StringComparison.Ordinal) ? page : "{\"rows\":[{\"_id\":\"last\"}]}") : null);
            Assert.Equal(1001, (await Run(rows, "rows", new() { ["table"] = "Orders", ["view"] = "Unpaid", ["max"] = "5000" })).Count);
            Assert.Equal(new[] { Gateway("rows/?table_name=Orders&view_name=Unpaid&start=0&limit=1000&convert_keys=true"), Gateway("rows/?table_name=Orders&view_name=Unpaid&start=1000&limit=1000&convert_keys=true") }, rows.Requests.Where(sent => sent.Url.Contains("/rows/?", StringComparison.Ordinal)).Select(sent => sent.Url));
            Assert.Equal(3, (await Run(new Server(sent => sent.Url.Contains("/rows/?", StringComparison.Ordinal) ? (HttpStatusCode.OK, page) : null), "rows", new() { ["table"] = "Orders", ["max"] = "3" })).Count);
        }

        [Fact]
        public async Task A_delete_says_whether_it_deleted_and_links_go_by_their_columns_name()
        {
            Assert.Equal(true, (bool?)Assert.Single(await Run(new Server(), "row_delete", new() { ["table"] = "Orders", ["row"] = Ada })).Fields["deleted"]);
            Assert.Equal(false, (bool?)Assert.Single(await Run(new Server(sent => sent.Method == "DELETE" ? (HttpStatusCode.OK, "{\"deleted_rows\":0}") : null), "row_delete", new() { ["table"] = "Orders", ["row"] = Ada })).Fields["deleted"]); // SeaTable answers 200 either way
            var locked = new Server();
            Assert.Equal(true, (bool?)Assert.Single(await Run(locked, "row_lock", new() { ["table"] = "Orders", ["row"] = Ada })).Fields["locked"]);
            Assert.Equal(("PUT", Gateway("lock-rows/"), "{\"table_name\":\"Orders\",\"row_ids\":[\"" + Ada + "\"]}"), (locked.Requests[^1].Method, locked.Requests[^1].Url, locked.Requests[^1].Body));
            var unlocked = new Server();
            Assert.Equal(false, (bool?)Assert.Single(await Run(unlocked, "row_unlock", new() { ["table"] = "Orders", ["row"] = Ada })).Fields["locked"]);
            Assert.Equal(("PUT", Gateway("unlock-rows/")), (unlocked.Requests[^1].Method, unlocked.Requests[^1].Url));

            var linked = new Server();
            Assert.Equal(true, (bool?)Assert.Single(await Run(linked, "link_add", new() { ["table"] = "Orders", ["column"] = "Customer", ["row"] = Ada, ["other_rows"] = Lund })).Fields["linked"]);
            Assert.Equal(("POST", Gateway("links/"), "{\"link_id\":\"IP5k\",\"table_name\":\"Orders\",\"other_table_name\":\"Customers\",\"other_rows_ids_map\":{\"" + Ada + "\":[\"" + Lund + "\"]}}"), (linked.Requests[^1].Method, linked.Requests[^1].Url, linked.Requests[^1].Body));
            var fromCustomers = new Server(); // the same link seen from its other table: the linked table is the link's first
            await Run(fromCustomers, "link_add", new() { ["table"] = "Customers", ["column"] = "Orders", ["row"] = Lund, ["other_rows"] = Ada });
            Assert.Equal("{\"link_id\":\"IP5k\",\"table_name\":\"Customers\",\"other_table_name\":\"Orders\",\"other_rows_ids_map\":{\"" + Lund + "\":[\"" + Ada + "\"]}}", fromCustomers.Requests[^1].Body);
            var listed = new Server(sent => sent.Url.EndsWith("/query-links/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"" + Ada + "\":[{\"row_id\":\"" + Lund + "\",\"display_value\":\"Kafé Lund\"}]}") : null);
            Assert.Equal((Lund, "Kafé Lund"), ((string?)Assert.Single(await Run(listed, "links", new() { ["table"] = "Orders", ["column"] = "Customer", ["row"] = Ada })).Fields["_id"], (string?)(await Run(listed, "links", new() { ["table"] = "Orders", ["column"] = "Customer", ["row"] = Ada }))[0].Fields["display_value"]));
            Assert.Equal("{\"table_id\":\"dwEe\",\"link_column_key\":\"4oiJ\",\"rows\":[{\"row_id\":\"" + Ada + "\",\"offset\":0,\"limit\":50}]}", listed.Requests[^1].Body);
            var unlinked = new Server(sent => sent.Method == "DELETE" ? (HttpStatusCode.OK, "{\"deleted_links_count\":1,\"success\":true}") : null);
            Assert.Equal(true, (bool?)Assert.Single(await Run(unlinked, "link_remove", new() { ["table"] = "Orders", ["column"] = "Customer", ["row"] = Ada, ["other_rows"] = Lund })).Fields["unlinked"]);
        }

        [Fact]
        public async Task The_base_is_described_snapshotted_and_its_collaborators_found()
        {
            var tables = await Run(new Server(), "base_metadata", new());
            Assert.Equal(new[] { "Orders", "Customers" }, tables.Select(table => (string?)table.Fields["name"]));
            Assert.Contains("{\"name\":\"Kind\",\"type\":\"single-select\"}", (string?)tables[0].Fields["columns"], StringComparison.Ordinal);
            Assert.Equal("[\"Default View\",\"Unpaid\"]", (string?)tables[0].Fields["views"]);
            Assert.Equal("SeaTable made no snapshot: the last one is too recent (time_is_short).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Server(sent => sent.Url.EndsWith("/snapshot/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"status\":\"time_is_short\"}") : null), "base_snapshot", new()))).Message); // n8n: done
            Assert.Equal(true, (bool?)Assert.Single(await Run(new Server(sent => sent.Url.EndsWith("/snapshot/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"success\":true}") : null), "base_snapshot", new())).Fields["snapshot"]);
            var people = new Server(sent => sent.Url.EndsWith("/related-users/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"user_list\":[{\"email\":\"a@auth.local\",\"name\":\"Ada\",\"contact_email\":\"ada@kafe.example\"},{\"email\":\"b@auth.local\",\"name\":\"Bo\",\"contact_email\":\"bo@kafe.example\"}]}") : null);
            Assert.Equal("Ada", (string?)Assert.Single(await Run(people, "base_collaborators", new() { ["search"] = "ADA@" })).Fields["name"]);
            var asset = new Server(sent => sent.Url.Contains("/app-download-link/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"download_link\":\"https://seatable.kafe.example/seafhttp/files/x/receipt.png\"}") : null);
            Assert.Equal("https://seatable.kafe.example/seafhttp/files/x/receipt.png", (string?)Assert.Single(await Run(asset, "asset_link", new() { ["path"] = "/images/2026-10/receipt.png" })).Fields["download_link"]);
            Assert.Equal((Address + "/api/v2.1/dtable/app-download-link/?path=%2Fimages%2F2026-10%2Freceipt.png", "Token api-token-1"), (asset.Requests[0].Url, asset.Requests[0].Authorization)); // the API token, as SeaTable has it for this one
        }

        [Fact]
        public async Task SeaTables_messages_are_said_and_an_answer_that_is_not_its_refused()
        {
            async Task<string> Failed(Func<Sent, (HttpStatusCode, string)?> answer, string operation = "rows", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Run(new Server(answer), operation, parameters ?? new() { ["table"] = "Orders" }))).Message;

            Assert.Equal("SeaTable answered HTTP 403: Permission denied. - check the API token.", await Failed(sent => sent.Url.Contains("/app-access-token/", StringComparison.Ordinal) ? (HttpStatusCode.Forbidden, "{\"error_msg\":\"Permission denied.\"}") : null));
            Assert.Equal("SeaTable answered HTTP 400: parse error: unexpected invalid near OR.", await Failed(sent => sent.Url.EndsWith("/sql/", StringComparison.Ordinal) ? (HttpStatusCode.BadRequest, "{\"error_message\":\"parse error: unexpected invalid near OR\"}") : null, "rows_search", new() { ["table"] = "Orders", ["column"] = "Name", ["term"] = "x" }));
            Assert.Equal("SeaTable answered HTTP 502.", await Failed(sent => sent.Url.Contains("/rows/?", StringComparison.Ordinal) ? (HttpStatusCode.BadGateway, "<html>bad gateway</html>") : null));
            Assert.Equal("SeaTable answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(sent => sent.Url.Contains("/app-access-token/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "<html>SeaTable</html>") : null));
            Assert.Equal("SeaTable answered without the base's access token - check the address.", await Failed(sent => sent.Url.Contains("/app-access-token/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"dtable_name\":\"Kafe\"}") : null));
            Assert.Equal("SeaTable answered with what is not a list of rows - check the address.", await Failed(sent => sent.Url.Contains("/rows/?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"rows\":{}}") : null));
        }

        [Fact]
        public async Task A_test_names_the_base()
        {
            var connector = new SeaTableConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Server()), Address, null, Credentials));
            Assert.Equal((true, "SeaTable answers: the token is the base Kafe's."), (ok.Success, ok.Message));
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Server(_ => (HttpStatusCode.Forbidden, "{\"error_msg\":\"Permission denied.\"}"))), Address, null, Credentials));
            Assert.Equal((false, "SeaTable answered HTTP 403: Permission denied. - check the API token."), (refused.Success, refused.Message));
        }
    }

    /// <summary>
    /// SeaTable LIVE, against this machine's throwaway SeaTable 6.2 (scripts/seatable-test-server.ps1) - fail-not-skip
    /// without it: a test naming the base Kafe; a table of its own made for the test over SeaTable's API; a row made with
    /// its number, checkbox, date and choice, read back by name, changed, found by a search for a name with a quote (n8n's
    /// escaping found nothing), refused a choice the column does not offer (SeaTable would have added it), locked and
    /// unlocked; a second row linked, listed and unlinked; both deleted, a second delete saying it deleted nothing. The table is
    /// deleted afterwards.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SeaTableConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Token => new() { ["api-key"] = SeaTableTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { new Uri(SeaTableTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new SeaTableConnector(), Contexts.Extract(Egress, SeaTableTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Token, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        [Fact]
        public async Task A_row_is_made_found_linked_and_deleted_in_a_table_of_its_own()
        {
            var tested = await new SeaTableConnector().TestConnectionAsync(Contexts.Test(Egress, SeaTableTestEndpoint.Endpoint, credentials: Token, timeoutSeconds: 60));
            Assert.Equal((true, "SeaTable answers: the token is the base Kafe's."), (tested.Success, tested.Message));

            var marker = Guid.NewGuid().ToString("N")[..8];
            var (orders, customers) = ("Orders" + marker, "Customers" + marker);
            await using var tables = await SeaTableTestEndpoint.TablesAsync(orders, customers);
            output.WriteLine($"tables {orders}, {customers}");

            var made = await One("row_create", new() { ["table"] = orders, ["fields"] = "{\"Name\": \"O'Brien\", \"Qty\": 3, \"Paid\": true, \"Due\": \"2026-11-01\", \"Kind\": \"Kaffe\"}" });
            var row = (string)made["_id"]!;
            Assert.Equal(("O'Brien", 3L, true, "Kaffe"), ((string?)made["Name"], Convert.ToInt64(made["Qty"], System.Globalization.CultureInfo.InvariantCulture), (bool?)made["Paid"], (string?)made["Kind"]));
            Assert.Equal(4L, Convert.ToInt64((await One("row_update", new() { ["table"] = orders, ["row"] = row, ["fields"] = "{\"Qty\": 4}" }))["Qty"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(row, (string?)Assert.Single(await All("rows_search", new() { ["table"] = orders, ["column"] = "Name", ["term"] = "O'Brien" })).Fields["_id"]); // n8n's escaping found nothing
            Assert.Equal(row, (string?)Assert.Single(await All("rows_search", new() { ["table"] = orders, ["column"] = "Name", ["term"] = "o'brien", ["exact"] = "true", ["ignore_case"] = "true" })).Fields["_id"]);
            Assert.Empty(await All("rows_search", new() { ["table"] = orders, ["column"] = "Name", ["term"] = "\" OR 1=1 OR \"" }));
            Assert.StartsWith("Kind offers \"Kaffe\", \"Bulle\"", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("row_update", new() { ["table"] = orders, ["row"] = row, ["fields"] = "{\"Kind\": \"Te\"}" }))).Message, StringComparison.Ordinal);
            Assert.Equal(true, (bool?)(await One("row_lock", new() { ["table"] = orders, ["row"] = row }))["locked"]);
            Assert.Equal(false, (bool?)(await One("row_unlock", new() { ["table"] = orders, ["row"] = row }))["locked"]);
            Assert.Single(await All("rows", new() { ["table"] = orders }));

            var customer = (string)(await One("row_create", new() { ["table"] = customers, ["fields"] = "{\"Name\": \"Kafé Lund\"}" }))["_id"]!;
            Assert.Equal(true, (bool?)(await One("link_add", new() { ["table"] = orders, ["column"] = "Customer", ["row"] = row, ["other_rows"] = customer }))["linked"]);
            Assert.Equal((customer, "Kafé Lund"), ((string?)Assert.Single(await All("links", new() { ["table"] = orders, ["column"] = "Customer", ["row"] = row })).Fields["_id"], (string?)(await All("links", new() { ["table"] = orders, ["column"] = "Customer", ["row"] = row }))[0].Fields["display_value"]));
            Assert.Equal(true, (bool?)(await One("link_remove", new() { ["table"] = orders, ["column"] = "Customer", ["row"] = row, ["other_rows"] = customer }))["unlinked"]);

            Assert.Equal(true, (bool?)(await One("row_delete", new() { ["table"] = orders, ["row"] = row }))["deleted"]);
            Assert.Equal(false, (bool?)(await One("row_delete", new() { ["table"] = orders, ["row"] = row }))["deleted"]); // SeaTable answers 200 either way
            Assert.Equal($"The table {orders} has no row {row} (row).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("row", new() { ["table"] = orders, ["row"] = row }))).Message);
            Assert.Contains(await All("base_metadata", new()), table => (string?)table.Fields["name"] == orders);
        }
    }
}
