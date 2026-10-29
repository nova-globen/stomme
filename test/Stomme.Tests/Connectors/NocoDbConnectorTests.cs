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
using Stomme.Modules.Connectors.NocoDb;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// NocoDB (n8n's NocoDB node: a table's rows created, got, read many, updated, upserted, counted and deleted): every
    /// call NocoDB's v2 data API with the API token in xc-token; a table's records page by page by offset - a page no bigger than
    /// what is left, NocoDB's last page or a short one the end - filtered by a where formula, sorted, some fields, a view; a
    /// record got by its id, created (one, or a list) with its key as NocoDB gave it, updated by its id or its key among its fields,
    /// upserted (an id updates, none creates), deleted, counted; the bases and a base's tables; a primary key the connection names;
    /// what could go wrong refused before anything is sent; NocoDB's word on a failure; a test naming the bases seen.
    /// </summary>
    public sealed class NocoDbConnectorTests
    {
        private const string Api = "https://nocodb.example.test";

        private const string Table = "md_0a1b2c3d4e5f6g";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "nc-token-1" };

        private sealed class NocoDb : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("xc-token", out var token) ? token.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(NocoDb nocodb, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(nocodb), Api, operation, parameters, settings: settings ?? new Dictionary<string, string>(), credentials: Credentials);

        [Fact]
        public async Task Records_are_read_page_by_page_filtered_sorted_and_counted()
        {
            var nocodb = new NocoDb
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("/count", StringComparison.Ordinal) ? "{\"count\":250}"
                    : url.Contains("offset=0&", StringComparison.Ordinal) ? "{\"list\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => "{\"Id\":" + i + ",\"Name\":\"Café " + i + "\",\"Total\":99.5,\"Paid\":true,\"Tags\":[\"a\",\"b\"],\"Note\":null}")) + "],\"pageInfo\":{\"totalRows\":250,\"page\":1,\"pageSize\":100,\"isFirstPage\":true,\"isLastPage\":false}}"
                    : url.Contains("offset=100&", StringComparison.Ordinal) ? "{\"list\":[" + string.Join(",", Enumerable.Range(101, 50).Select(i => "{\"Id\":" + i + "}")) + "],\"pageInfo\":{\"totalRows\":150,\"isLastPage\":true}}"
                    : "{\"list\":[]}"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new NocoDbConnector(), Context(nocodb, operation, parameters));

            var records = await Run(NocoDbConnector.RecordsOperation, new() { ["table"] = Table, ["where"] = "(Status,eq,Paid)~and(Total,gt,100)", ["sort"] = "Status, -Total", ["columns"] = "Name,Total", ["view"] = "vw5t20qcex4d5zpk", ["max"] = "1000" });
            Assert.Equal(150, records.Count);
            Assert.Equal(new[]
            {
                $"{Api}/api/v2/tables/{Table}/records?where=%28Status%2Ceq%2CPaid%29~and%28Total%2Cgt%2C100%29&sort=Status%2C-Total&fields=Name%2CTotal&viewId=vw5t20qcex4d5zpk&offset=0&limit=100",
                $"{Api}/api/v2/tables/{Table}/records?where=%28Status%2Ceq%2CPaid%29~and%28Total%2Cgt%2C100%29&sort=Status%2C-Total&fields=Name%2CTotal&viewId=vw5t20qcex4d5zpk&offset=100&limit=100",
            }, nocodb.Requests.Select(request => request.Url)); // NocoDB's last page the end
            Assert.All(nocodb.Requests, request => Assert.Equal("nc-token-1", request.Token));
            var first = records[0].Fields;
            Assert.Equal((1L, "Café 1", 99.5, true, "[\"a\",\"b\"]"), ((long?)first["Id"], (string?)first["Name"], (double?)first["Total"], (bool?)first["Paid"], (string?)first["Tags"]));
            Assert.Null(first["Note"]);

            Assert.Equal(30, (await Run(NocoDbConnector.RecordsOperation, new() { ["table"] = Table, ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/api/v2/tables/{Table}/records?offset=0&limit=30", nocodb.Requests[^1].Url); // a page no bigger than what is left
            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "{\"list\":[{\"Id\":1},{\"Id\":2}],\"pageInfo\":{}}");
            Assert.Equal(2, (await Run(NocoDbConnector.RecordsOperation, new() { ["table"] = Table })).Count);
            Assert.Equal($"{Api}/api/v2/tables/{Table}/records?offset=0&limit=100", nocodb.Requests[^1].Url); // a short page the end, 100 a page
            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "{\"list\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => "{\"Id\":" + i + "}")) + "],\"pageInfo\":{\"isLastPage\":true}}");
            var asked = nocodb.Requests.Count;
            Assert.Equal(100, (await Run(NocoDbConnector.RecordsOperation, new() { ["table"] = Table })).Count);
            Assert.Equal(asked + 1, nocodb.Requests.Count); // NocoDB's last page the end, though full

            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "{\"count\":250}");
            var counted = Assert.Single(await Run(NocoDbConnector.CountRecordsOperation, new() { ["table"] = Table, ["where"] = "(Status,eq,Paid)", ["sort"] = "Name", ["columns"] = "Name" })).Fields;
            Assert.Equal(($"{Api}/api/v2/tables/{Table}/records/count?where=%28Status%2Ceq%2CPaid%29", 250L), (nocodb.Requests[^1].Url, (long?)counted["count"])); // no sort and no fields on a count
        }

        [Fact]
        public async Task A_record_is_got_created_updated_upserted_and_deleted()
        {
            var nocodb = new NocoDb
            {
                Answer = (method, url) => (HttpStatusCode.OK, method == HttpMethod.Get ? "{\"Id\":42,\"Name\":\"Ada\",\"Owner\":{\"id\":\"u1\",\"name\":\"Café\",\"email\":\"ada@example.com\"}}"
                    : method == HttpMethod.Post ? "{\"Id\":43}"
                    : "[{\"Id\":42}]"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) => Contexts.DrainAsync(new NocoDbConnector(), Context(nocodb, operation, parameters, settings));
            (string, string, string?) Last() => (nocodb.Requests[^1].Method.Method, nocodb.Requests[^1].Url, nocodb.Requests[^1].Body);

            var got = Assert.Single(await Run(NocoDbConnector.GetRecordOperation, new() { ["table"] = Table, ["id"] = "42", ["columns"] = "Name, Owner", ["where"] = "(x,eq,1)" })).Fields;
            Assert.Equal(("GET", $"{Api}/api/v2/tables/{Table}/records/42?fields=Name%2COwner", (string?)null), Last()); // no where on a get
            Assert.Equal((42L, "Ada", "{\"id\":\"u1\",\"name\":\"Café\",\"email\":\"ada@example.com\"}"), ((long?)got["Id"], (string?)got["Name"], (string?)got["Owner"])); // an object its JSON, its accents as themselves

            var made = Assert.Single(await Run(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "{\"Name\": \"Café Ada\", \"Total\": 99.5}", ["id"] = "7" })).Fields;
            Assert.Equal(("POST", $"{Api}/api/v2/tables/{Table}/records", (string?)"{\"Name\":\"Café Ada\",\"Total\":99.5}"), Last());
            Assert.Equal(("Café Ada", 43L), ((string?)made["Name"], (long?)made["Id"])); // its key as NocoDB gave it
            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "[{\"Id\":44},{\"Id\":45}]");
            var many = await Run(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "[{\"Name\": \"A\"}, {\"Name\": \"B\"}]" });
            Assert.Equal("[{\"Name\":\"A\"},{\"Name\":\"B\"}]", Last().Item3);
            Assert.Equal(new (string?, long?)[] { ("A", 44L), ("B", 45L) }, many.Select(record => ((string?)record.Fields["Name"], (long?)record.Fields["Id"])));

            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "[{\"Id\":42}]");
            var changed = Assert.Single(await Run(NocoDbConnector.UpdateRecordOperation, new() { ["table"] = Table, ["id"] = "42", ["fields"] = "{\"Paid\": true}" })).Fields;
            Assert.Equal(("PATCH", $"{Api}/api/v2/tables/{Table}/records", (string?)"{\"Paid\":true,\"Id\":42}"), Last());
            Assert.Equal((true, 42L), ((bool?)changed["Paid"], (long?)changed["Id"]));
            await Run(NocoDbConnector.UpdateRecordOperation, new() { ["table"] = Table, ["fields"] = "{\"Id\": 42, \"Paid\": false}" });
            Assert.Equal("{\"Id\":42,\"Paid\":false}", Last().Item3); // its key among its fields
            await Run(NocoDbConnector.UpdateRecordOperation, new() { ["table"] = Table, ["id"] = "rec-7", ["fields"] = "{\"Paid\": true}" }, new() { ["primary_key"] = "Code" });
            Assert.Equal("{\"Paid\":true,\"Code\":\"rec-7\"}", Last().Item3); // a primary key the connection names, a key not a number as text

            await Run(NocoDbConnector.UpsertRecordOperation, new() { ["table"] = Table, ["id"] = "42", ["fields"] = "{\"Paid\": true}" });
            Assert.Equal(("PATCH", $"{Api}/api/v2/tables/{Table}/records", (string?)"{\"Paid\":true,\"Id\":42}"), Last()); // an id updates
            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "{\"Id\":46}");
            var upserted = Assert.Single(await Run(NocoDbConnector.UpsertRecordOperation, new() { ["table"] = Table, ["fields"] = "{\"Id\": 9, \"Paid\": true}" })).Fields;
            Assert.Equal(("POST", $"{Api}/api/v2/tables/{Table}/records", (string?)"{\"Paid\":true}"), Last()); // none creates - a key among its fields left to NocoDB
            Assert.Equal(46L, (long?)upserted["Id"]);

            nocodb.Answer = (_, _) => (HttpStatusCode.OK, "[{\"Id\":42}]");
            var gone = Assert.Single(await Run(NocoDbConnector.DeleteRecordOperation, new() { ["table"] = Table, ["id"] = "42" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/api/v2/tables/{Table}/records", (string?)"{\"Id\":42}"), Last());
            Assert.Equal((Table, "42", true), ((string?)gone["table"], (string?)gone["id"], (bool?)gone["deleted"]));
        }

        [Fact]
        public async Task The_bases_and_a_bases_tables_are_listed()
        {
            var nocodb = new NocoDb { Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/tables", StringComparison.Ordinal) ? "{\"list\":[{\"id\":\"md_orders\",\"title\":\"Orders\",\"table_name\":\"nc_orders\"}]}" : "{\"list\":[{\"id\":\"p979g1063032uw4\",\"title\":\"Shop\"},{\"id\":\"p2\",\"title\":\"CRM\"}]}") };
            var bases = await Contexts.DrainAsync(new NocoDbConnector(), Context(nocodb, NocoDbConnector.BasesOperation, new()));
            Assert.Equal(new[] { ("p979g1063032uw4", "Shop"), ("p2", "CRM") }, bases.Select(item => ((string?)item.Fields["id"], (string?)item.Fields["title"])));
            Assert.Equal($"{Api}/api/v2/meta/bases/", nocodb.Requests[^1].Url);
            var tables = await Contexts.DrainAsync(new NocoDbConnector(), Context(nocodb, NocoDbConnector.TablesOperation, new() { ["base"] = "p979g1063032uw4" }));
            Assert.Equal(("md_orders", "Orders"), ((string?)tables[0].Fields["id"], (string?)tables[0].Fields["title"]));
            Assert.Equal($"{Api}/api/v2/meta/bases/p979g1063032uw4/tables", nocodb.Requests[^1].Url);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_nocodbs_word_on_a_failure()
        {
            var nocodb = new NocoDb();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(nocodb, operation, parameters, settings)))).Message;

            Assert.Equal("Give the table - its id.", await Refused(NocoDbConnector.RecordsOperation, new()));
            Assert.Equal("'Orders table' is not a table's id - letters, digits, _ and -.", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = "Orders table" }));
            Assert.Equal("'../meta' is not a table's id - letters, digits, _ and -.", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = "../meta" }));
            Assert.Equal("Give the record - its primary key's value.", await Refused(NocoDbConnector.GetRecordOperation, new() { ["table"] = Table }));
            Assert.Equal("Give the record - its primary key's value.", await Refused(NocoDbConnector.DeleteRecordOperation, new() { ["table"] = Table }));
            Assert.Equal("'4/2' is not a record's id - letters, digits, _ and -.", await Refused(NocoDbConnector.GetRecordOperation, new() { ["table"] = Table, ["id"] = "4/2" }));
            Assert.Equal("The where formula is one line of up to 4,096 characters.", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = Table, ["where"] = "(a,eq,1)\n~or(b,eq,2)" }));
            Assert.Equal("The where formula is one line of up to 4,096 characters.", await Refused(NocoDbConnector.CountRecordsOperation, new() { ["table"] = Table, ["where"] = "(a,eq," + new string('x', 4090) + ")" })); // 4,097 characters
            Assert.Equal("'Name,,Total' is not a list of fields - Status,-Total.", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = Table, ["sort"] = "Name,,Total" }));
            Assert.Equal("'vw 1' is not a view's id.", await Refused(NocoDbConnector.CountRecordsOperation, new() { ["table"] = Table, ["view"] = "vw 1" }));
            Assert.Equal("Give the fields - a JSON object.", await Refused(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table }));
            Assert.Equal("The fields are not a JSON object - {\"Name\": \"Ada\"}.", await Refused(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "Name=Ada" }));
            Assert.Equal("Give one to 100 records - JSON objects.", await Refused(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "[1, 2]" }));
            Assert.Equal("Give one to 100 records - JSON objects.", await Refused(NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "[" + string.Join(",", Enumerable.Range(1, 101).Select(_ => "{}")) + "]" }));
            Assert.Equal("Give one record's fields - a JSON object.", await Refused(NocoDbConnector.UpdateRecordOperation, new() { ["table"] = Table, ["id"] = "1", ["fields"] = "[{\"a\": 1}]" }));
            Assert.Equal("Give the record - its id, or its primary key (Id) among the fields.", await Refused(NocoDbConnector.UpdateRecordOperation, new() { ["table"] = Table, ["fields"] = "{\"Paid\": true}" }));
            Assert.Equal("'Na,me' is not a field's name (primary key).", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = Table }, new() { ["primary_key"] = "Na,me" }));
            Assert.Equal("Give the base - its id.", await Refused(NocoDbConnector.TablesOperation, new()));
            Assert.Equal("'../tables' is not a base's id - letters, digits, _ and -.", await Refused(NocoDbConnector.TablesOperation, new() { ["base"] = "../tables" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(NocoDbConnector.RecordsOperation, new() { ["table"] = Table, ["max"] = "0" }));
            Assert.StartsWith("NocoDB has no operation 'upload' - records, get_record,", await Refused("upload", new()));
            Assert.Empty(nocodb.Requests); // nothing sent

            var tokenless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Contexts.Extract(new StubEgress(nocodb), Api, NocoDbConnector.BasesOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>())));
            Assert.Equal("The NocoDB connection has no API token.", tokenless.Message);

            var missing = new NocoDb { Answer = (_, _) => (HttpStatusCode.NotFound, "{\"msg\":\"Table 'md_x' not found\"}") };
            Assert.Equal("HTTP 404: Table 'md_x' not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(missing, NocoDbConnector.RecordsOperation, new() { ["table"] = "md_x" })))).Message);
            var invalid = new NocoDb { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"error\":\"INVALID_REQUEST_BODY\",\"message\":\"Invalid request body\"}") };
            Assert.Equal("HTTP 400: Invalid request body", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(invalid, NocoDbConnector.CreateRecordOperation, new() { ["table"] = Table, ["fields"] = "{}" })))).Message);
            var verbose = new NocoDb { Answer = (_, _) => (HttpStatusCode.InternalServerError, "{\"msg\":\"" + new string('e', 400) + "\"}") };
            Assert.Equal("HTTP 500: " + new string('e', 300), (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(verbose, NocoDbConnector.BasesOperation, new())))).Message); // a long word cut
            var page = new NocoDb { Answer = (_, _) => (HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>") };
            Assert.Equal("NocoDB answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(page, NocoDbConnector.RecordsOperation, new() { ["table"] = Table })))).Message); // never read as no records
            var silent = new NocoDb { Answer = (_, _) => (HttpStatusCode.Unauthorized, "") };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NocoDbConnector(), Context(silent, NocoDbConnector.BasesOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_bases_seen()
        {
            var nocodb = new NocoDb { Answer = (_, _) => (HttpStatusCode.OK, "{\"list\":[{\"id\":\"p1\",\"title\":\"Shop\"},{\"id\":\"p2\",\"title\":\"CRM\"}]}") };
            var tested = await new NocoDbConnector().TestConnectionAsync(Contexts.Test(new StubEgress(nocodb), "", credentials: Credentials));
            Assert.Equal((true, "NocoDB: 2 bases seen."), (tested.Success, tested.Message));
            Assert.Equal(("https://app.nocodb.com/api/v2/meta/bases/", "nc-token-1"), (nocodb.Requests[0].Url, nocodb.Requests[0].Token)); // NocoDB's cloud when no address is given
            var one = new NocoDb { Answer = (_, _) => (HttpStatusCode.OK, "{\"list\":[{\"id\":\"p1\"}]}") };
            Assert.Equal("NocoDB: one base seen.", (await new NocoDbConnector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.ApiKey, "nocodb"), (new NocoDbConnector().AuthType, new NocoDbConnector().ConnectorType));
            Assert.Equal(9, new NocoDbConnector().Operations.Count);
        }
    }
}
