#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Supabase;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Supabase (n8n's Supabase node: a table's rows read, got, created, updated and deleted) over a project's REST API:
    /// the secret key in Supabase's apikey header, and as a bearer only when it is a JWT; a table's rows the filters keep -
    /// every filter, or any one inside or=(...), PostgREST's own query beside them - the columns and order asked, page by page by
    /// offset up to the most asked for; a row got, rows created, updated and deleted - each answering the rows it touched; a table,
    /// a column, an operator, a filter, an order, rows and fields that are not JSON objects, a query with a space and an update or
    /// delete without a filter refused before anything is sent; Postgres's refusal its code, message, details and hint; a test
    /// counting the tables.
    /// </summary>
    public sealed class SupabaseConnectorTests
    {
        private const string Project = "https://abcd.supabase.example";

        private const string Rest = Project + "/rest/v1";

        private const string SecretKey = "sb_secret_test_1";

        private const string JwtKey = "eyJhbGciOiJIUzI1NiJ9.service.role";

        private sealed class SupabaseApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? ApiKey, string? Authorization, string? Prefer, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "[]");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null, request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(SupabaseApi supabase, string operation, Dictionary<string, string> parameters, string key = SecretKey, string endpoint = Project) =>
            Contexts.Extract(new StubEgress(supabase), endpoint, operation, parameters, null, new Dictionary<string, string> { ["token"] = key });

        private static SupabaseConnector Connector() => new();

        private static string Rows(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"email\":\"user" + id + "@example.com\",\"total\":12.5,\"paid\":true,\"tags\":[\"vip\"],\"meta\":{\"source\":\"web\"},\"note\":null}")) + "]";

        [Fact]
        public async Task A_tables_rows_are_read_page_by_page_by_offset_up_to_the_most_asked_for_the_key_its_apikey()
        {
            var supabase = new SupabaseApi
            {
                Answer = (_, url) => url.Contains("offset=0", StringComparison.Ordinal) ? (HttpStatusCode.OK, Rows(1, 1000)) : (HttpStatusCode.OK, Rows(1001, 1000)),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.RowsOperation, new()
            {
                ["table"] = "orders", ["filters"] = "status eq paid\ntotal gte 100\nemail ilike %@acme.com\nid in 1, 2,\"3\"\ndeleted_at is NULL", ["columns"] = "id, email, total",
                ["order"] = "created_at.desc, id", ["max"] = "1500",
            }, endpoint: Project + "/project/abcd/editor"));

            Assert.Equal(1500, records.Count); // the most asked for
            Assert.Equal(2, supabase.Requests.Count);
            Assert.Equal(Rest + "/orders?status=eq.paid&total=gte.100&email=ilike.%25%40acme.com&id=in.%28%221%22%2C%222%22%2C%22%5C%223%5C%22%22%29&deleted_at=is.null&select=id,email,total&order=created_at.desc,id&limit=1000&offset=0", supabase.Requests[0].Url);
            Assert.EndsWith("&limit=500&offset=1000", supabase.Requests[1].Url, StringComparison.Ordinal); // a page no bigger than what is left
            Assert.All(supabase.Requests, request => Assert.Equal((SecretKey, (string?)null, (string?)null), (request.ApiKey, request.Authorization, request.Prefer))); // a new key in apikey alone
            var row = records[0].Fields;
            Assert.Equal((1L, "user1@example.com", 12.5, true, "[\"vip\"]", "{\"source\":\"web\"}"), ((long?)row["id"], (string?)row["email"], (double?)row["total"], (bool?)row["paid"], (string?)row["tags"], (string?)row["meta"]));
            Assert.Null(row["note"]);

            var lastPage = new SupabaseApi { Answer = (_, _) => (HttpStatusCode.OK, Rows(1, 3)) };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(lastPage, SupabaseConnector.RowsOperation, new() { ["table"] = "orders" }, JwtKey))).Count);
            Assert.Single(lastPage.Requests); // a page shorter than asked: the last
            Assert.Equal((Rest + "/orders?limit=1000&offset=0", JwtKey, "Bearer " + JwtKey), (lastPage.Requests[0].Url, lastPage.Requests[0].ApiKey, lastPage.Requests[0].Authorization)); // a JWT key a bearer too

            var any = new SupabaseApi();
            await Contexts.DrainAsync(Connector(), Context(any, SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "status eq paid\nemail eq ada@example.com", ["match"] = "Any", ["query"] = "?total=gte.100" }));
            Assert.Equal(Rest + "/orders?or=%28status.eq.paid%2Cemail.eq.%22ada%40example.com%22%29&total=gte.100&limit=1000&offset=0", any.Requests[0].Url); // any one, PostgREST's own beside
        }

        [Fact]
        public async Task A_row_is_got_and_rows_are_created_updated_and_deleted_each_answering_the_rows_it_touched()
        {
            var supabase = new SupabaseApi { Answer = (method, _) => method == HttpMethod.Post ? (HttpStatusCode.Created, Rows(7, 2)) : (HttpStatusCode.OK, Rows(7, 1)) };

            var got = await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.GetRowOperation, new() { ["table"] = "orders", ["filters"] = "id eq 7" }));
            Assert.Equal((HttpMethod.Get, Rest + "/orders?id=eq.7&limit=1&offset=0"), (supabase.Requests[^1].Method, supabase.Requests[^1].Url));
            Assert.Equal(7L, Assert.Single(got).Fields["id"]);

            var made = await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.CreateRowsOperation, new() { ["table"] = "orders", ["rows"] = "[{\"email\": \"ada@example.com\"}, {\"email\": \"grace@example.com\"}]" }));
            Assert.Equal((HttpMethod.Post, Rest + "/orders", "return=representation", "[{\"email\":\"ada@example.com\"},{\"email\":\"grace@example.com\"}]"), (supabase.Requests[^1].Method, supabase.Requests[^1].Url, supabase.Requests[^1].Prefer, supabase.Requests[^1].Body));
            Assert.Equal(2, made.Count); // the rows stored
            await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.CreateRowsOperation, new() { ["table"] = "orders", ["rows"] = "{\"email\": \"ada@example.com\"}" }));
            Assert.Equal("{\"email\":\"ada@example.com\"}", supabase.Requests[^1].Body); // one row

            var changed = await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.UpdateRowsOperation, new() { ["table"] = "orders", ["filters"] = "id eq 7", ["fields"] = "{\"status\": \"shipped\"}" }));
            Assert.Equal((HttpMethod.Patch, Rest + "/orders?id=eq.7", "return=representation", "{\"status\":\"shipped\"}"), (supabase.Requests[^1].Method, supabase.Requests[^1].Url, supabase.Requests[^1].Prefer, supabase.Requests[^1].Body));
            Assert.Single(changed);

            var removed = await Contexts.DrainAsync(Connector(), Context(supabase, SupabaseConnector.DeleteRowsOperation, new() { ["table"] = "orders", ["filters"] = "id eq 7" }));
            Assert.Equal((HttpMethod.Delete, Rest + "/orders?id=eq.7", "return=representation"), (supabase.Requests[^1].Method, supabase.Requests[^1].Url, supabase.Requests[^1].Prefer));
            Assert.Equal(7L, Assert.Single(removed).Fields["id"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_postgres_refusal_is_its_words()
        {
            var supabase = new SupabaseApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Project) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(supabase, operation, parameters, endpoint: endpoint)))).Message;

            Assert.Equal("'orders;drop' is not a table's name (letters, digits and _).", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders;drop" }));
            Assert.Equal("'total)' is not a column's name (letters, digits and _).", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "total) gt 1" }));
            Assert.StartsWith("'~' is not an operator", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "total ~ 1" }));
            Assert.Equal("'status' is not a filter - column operator value (status eq paid).", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "status" }));
            Assert.Equal("'maybe' is not what is compares with - null, true, false or unknown.", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "paid is maybe" }));
            Assert.Equal("'both' is not a match - all or any.", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["match"] = "both" }));
            Assert.Equal("'id.up' is not an order - column.asc or column.desc.", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["order"] = "id.up" }));
            Assert.Equal("The PostgREST query has a space or a # - write it as PostgREST takes it, status=eq.paid&total=gte.100.", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["query"] = "status = paid" }));
            Assert.Equal("Give at least one filter - a get, an update or a delete names the rows it touches.", await Refused(SupabaseConnector.DeleteRowsOperation, new() { ["table"] = "orders" }));
            Assert.Equal("Give at least one filter - a get, an update or a delete names the rows it touches.", await Refused(SupabaseConnector.UpdateRowsOperation, new() { ["table"] = "orders", ["fields"] = "{\"status\": \"x\"}" }));
            Assert.Equal("The rows are not a JSON object or a list of them.", await Refused(SupabaseConnector.CreateRowsOperation, new() { ["table"] = "orders", ["rows"] = "[1, 2]" }));
            Assert.Equal("The rows are not JSON - {\"email\": \"ada@example.com\"}.", await Refused(SupabaseConnector.CreateRowsOperation, new() { ["table"] = "orders", ["rows"] = "email=ada" }));
            Assert.Equal("'Email Address' is not a column's name (letters, digits and _).", await Refused(SupabaseConnector.CreateRowsOperation, new() { ["table"] = "orders", ["rows"] = "{\"Email Address\": \"x\"}" }));
            Assert.Equal("The fields are not a JSON object.", await Refused(SupabaseConnector.UpdateRowsOperation, new() { ["table"] = "orders", ["filters"] = "id eq 1", ["fields"] = "[1]" }));
            Assert.Equal("Give at least one field.", await Refused(SupabaseConnector.UpdateRowsOperation, new() { ["table"] = "orders", ["filters"] = "id eq 1", ["fields"] = "{}" }));
            Assert.Equal("'status;drop' is not a column's name (letters, digits and _).", await Refused(SupabaseConnector.UpdateRowsOperation, new() { ["table"] = "orders", ["filters"] = "id eq 1", ["fields"] = "{\"status;drop\": \"x\"}" }));
            Assert.Equal("'ftp://x.example' is not a Supabase project's address - https://your-project.supabase.co.", await Refused(SupabaseConnector.RowsOperation, new() { ["table"] = "orders" }, "ftp://x.example"));
            Assert.Empty(supabase.Requests);

            var refusing = new SupabaseApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"code\":\"42703\",\"details\":null,\"hint\":\"Perhaps you meant to reference the column \\\"orders.total\\\".\",\"message\":\"column orders.totl does not exist\"}") };
            Assert.Equal("HTTP 400 (42703): column orders.totl does not exist - Perhaps you meant to reference the column \"orders.total\".",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, SupabaseConnector.RowsOperation, new() { ["table"] = "orders", ["filters"] = "totl gt 1" })))).Message);
        }

        [Fact]
        public async Task A_test_counts_the_tables()
        {
            var supabase = new SupabaseApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"swagger\":\"2.0\",\"paths\":{\"/\":{},\"/orders\":{},\"/customers\":{},\"/rpc/total\":{}}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(supabase), Project, credentials: new Dictionary<string, string> { ["token"] = SecretKey }));
            Assert.Equal((true, "Reached abcd.supabase.example: 2 tables and views."), (tested.Success, tested.Message));
            Assert.Equal((Rest + "/", SecretKey), (supabase.Requests[0].Url, supabase.Requests[0].ApiKey));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new SupabaseApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Invalid API key\",\"hint\":\"Double check your Supabase `anon` or `service_role` API key.\"}") }), Project, credentials: new Dictionary<string, string> { ["token"] = SecretKey }));
            Assert.Equal((false, "HTTP 401: Invalid API key - Double check your Supabase `anon` or `service_role` API key."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "supabase", ConnectorCategory.DataAndFiles), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "rows", "get_row", "create_rows", "update_rows", "delete_rows" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
