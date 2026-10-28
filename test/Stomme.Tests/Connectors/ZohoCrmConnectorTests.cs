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
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Connectors.ZohoCrm;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Zoho CRM (n8n's Zoho CRM node: any module's records created, upserted, got, read, updated and deleted): the
    /// refresh token exchanged at the data centre's accounts server, the access token sent as Zoho-oauthtoken; a module's records
    /// page by page - the page's size the same from page to page - up to the most asked for, its fields, an order and Zoho's own
    /// parameters; a search by criteria, an address, a phone or a word, nothing found an empty answer; each record its fields by
    /// API name, a lookup its name and id, lists joined, Zoho's $ fields left out; a record got, made, upserted by its duplicate
    /// check fields, changed and deleted, what Zoho did said; a record Zoho refused thrown in its words; fields and modules; what
    /// could go wrong refused before anything is sent; a test naming the user.
    /// </summary>
    public sealed class ZohoCrmConnectorTests
    {
        private const string Api = "https://www.zohoapis.example.test/crm/v2";

        private const string TokenUrl = "https://accounts.zoho.example.test/oauth/v2/token";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Zoho : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, body));
                var (status, answer) = url.StartsWith(TokenUrl, StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer","api_domain":"https://www.zohoapis.example.test"}""")
                    : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Zoho zoho, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(zoho), Api, operation, parameters, settings: new Dictionary<string, string> { ["token_url"] = TokenUrl }, credentials: Credentials);

        private static ZohoCrmConnector Connector() => new(new OAuth2TokenProvider());

        private static string Leads(int from, int count, bool more) =>
            "{\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"41508680000" + n.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "\",\"Last_Name\":\"Café <b>" + n + "</b>\","
                + "\"Owner\":{\"name\":\"Ada Lovelace\",\"id\":\"4150868000000225013\",\"email\":\"ada@example.com\"},\"Account_Name\":{\"name\":\"Acme\",\"id\":\"4150868000000224005\"},"
                + "\"Tag\":[{\"name\":\"VIP\",\"id\":\"1\"},{\"name\":\"EU\",\"id\":\"2\"}],\"Interests\":[\"Tea\",\"Café\"],\"Annual_Revenue\":120000,\"Email_Opt_Out\":false,\"Fax\":null,"
                + "\"Product_Details\":[{\"product\":{\"id\":\"9\"},\"quantity\":2}],\"$approved\":true,\"$state\":\"save\"}")) + "],\"info\":{\"per_page\":200,\"count\":" + count + ",\"page\":1,\"more_records\":" + (more ? "true" : "false") + "}}";

        [Fact]
        public async Task Records_are_read_page_by_page_by_the_exchanged_token_up_to_the_most_asked_for()
        {
            var zoho = new Zoho { Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("&page=2", StringComparison.Ordinal) ? Leads(201, 100, more: false) : Leads(1, 200, more: true)) };
            var records = await Contexts.DrainAsync(Connector(), Context(zoho, ZohoCrmConnector.RecordsOperation, new()
            {
                ["module"] = "Leads", ["fields"] = "Last_Name, Email", ["order"] = "Created_Time DESC", ["query"] = "approved=false, cvid=4150868000000087501, include_child=a+b",
            }));

            Assert.Equal(300, records.Count); // the last page's more_records false: the end
            Assert.Equal(TokenUrl, zoho.Requests[0].Url);
            Assert.Contains("refresh_token=refresh-1", zoho.Requests[0].Body);
            Assert.Contains("client_secret=secret-1", zoho.Requests[0].Body);
            Assert.All(zoho.Requests.Skip(1), request => Assert.Equal("Zoho-oauthtoken access-1", request.Authorization));
            const string Asked = Api + "/Leads?fields=Last_Name%2CEmail&sort_by=Created_Time&sort_order=desc&approved=false&cvid=4150868000000087501&include_child=a%2Bb&per_page=200";
            Assert.Equal(new[] { Asked + "&page=1", Asked + "&page=2" }, zoho.Requests.Skip(1).Select(request => request.Url));
            var lead = records[0].Fields;
            Assert.Equal(("4150868000000001", "Café <b>1</b>", "Ada Lovelace", "4150868000000225013", "Acme", "4150868000000224005"),
                ((string?)lead["id"], (string?)lead["Last_Name"], (string?)lead["Owner"], (string?)lead["Owner_id"], (string?)lead["Account_Name"], (string?)lead["Account_Name_id"]));
            Assert.Equal(("VIP, EU", "Tea, Café", 120000L, false, (object?)null, "[{\"product\":{\"id\":\"9\"},\"quantity\":2}]"),
                ((string?)lead["Tag"], (string?)lead["Interests"], (long?)lead["Annual_Revenue"], (bool?)lead["Email_Opt_Out"], lead["Fax"], (string?)lead["Product_Details"]));
            Assert.True(lead.ContainsKey("Fax"));
            Assert.DoesNotContain(lead.Keys, key => key.StartsWith('$')); // Zoho's own left out

            var few = new Zoho { Answer = (_, url) => (HttpStatusCode.OK, Leads(1, 30, more: true)) };
            Assert.Equal(70, (await Contexts.DrainAsync(Connector(), Context(few, ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Deals", ["max"] = "70", ["order"] = "Amount" }))).Count);
            Assert.Equal(new[] { Api + "/Deals?sort_by=Amount&sort_order=asc&per_page=70&page=1", Api + "/Deals?sort_by=Amount&sort_order=asc&per_page=70&page=2", Api + "/Deals?sort_by=Amount&sort_order=asc&per_page=70&page=3" },
                few.Requests.Skip(1).Select(request => request.Url)); // one page size throughout, the most asked for when fewer than 200

            var many = new Zoho { Answer = (_, _) => (HttpStatusCode.OK, Leads(1, 200, more: true)) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(many, ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Contacts" }))).Count); // by default up to 1,000
            Assert.Equal(6, many.Requests.Count);

            var empty = new Zoho { Answer = (_, _) => (HttpStatusCode.NoContent, "") };
            Assert.Empty(await Contexts.DrainAsync(Connector(), Context(empty, ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads", ["criteria"] = "(Last_Name:equals:Lovelace)and(City:starts_with:Lon)" })));
            Assert.Equal(Api + "/Leads/search?criteria=%28Last_Name%3Aequals%3ALovelace%29and%28City%3Astarts_with%3ALon%29&per_page=200&page=1", empty.Requests[^1].Url); // nothing found: Zoho's 204
            await Contexts.DrainAsync(Connector(), Context(empty, ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Contacts", ["email"] = "ada+crm@example.com", ["max"] = "5" }));
            Assert.Equal(Api + "/Contacts/search?email=ada%2Bcrm%40example.com&per_page=5&page=1", empty.Requests[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(empty, ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Contacts", ["word"] = "Café & co" }));
            Assert.Equal(Api + "/Contacts/search?word=Caf%C3%A9%20%26%20co&per_page=200&page=1", empty.Requests[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(empty, ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Contacts", ["phone"] = "+46 8 123" }));
            Assert.Equal(Api + "/Contacts/search?phone=%2B46%208%20123&per_page=200&page=1", empty.Requests[^1].Url);
        }

        [Fact]
        public async Task A_record_is_got_made_upserted_changed_and_deleted_and_what_zoho_did_said()
        {
            var zoho = new Zoho
            {
                Answer = (method, url) => method == HttpMethod.Get ? (HttpStatusCode.OK, Leads(1, 1, more: false))
                    : url.EndsWith("/upsert", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"data":[{"code":"SUCCESS","duplicate_field":"Email","action":"update","details":{"Modified_Time":"2026-10-12T14:00:00+02:00","id":"4150868000000001"},"message":"record updated","status":"success"}]}""")
                    : method == HttpMethod.Post ? (HttpStatusCode.Created, """{"data":[{"code":"SUCCESS","details":{"Modified_Time":"2026-10-12T14:00:00+02:00","Created_Time":"2026-10-12T14:00:00+02:00","id":"4150868000000002"},"message":"record added","status":"success"}]}""")
                    : (HttpStatusCode.OK, """{"data":[{"code":"SUCCESS","details":{"id":"4150868000000001"},"message":"record updated","status":"success"}]}"""),
            };
            (string, string, string?) Last() => (zoho.Requests[^1].Method.Method, zoho.Requests[^1].Url, zoho.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(zoho, operation, parameters));

            var got = Assert.Single(await Run(ZohoCrmConnector.GetRecordOperation, new() { ["module"] = "Leads", ["id"] = "4150868000000001" }));
            Assert.Equal(("GET", Api + "/Leads/4150868000000001", (string?)null), Last());
            Assert.Equal("Acme", (string?)got.Fields["Account_Name"]);

            var made = Assert.Single(await Run(ZohoCrmConnector.CreateRecordOperation, new() { ["module"] = "Leads", ["values"] = "{\"Last_Name\": \"Café <b>Lovelace</b>\", \"Company\": \"Acme\", \"Annual_Revenue\": 120000}" }));
            Assert.Equal(("POST", Api + "/Leads", "{\"data\":[{\"Last_Name\":\"Café <b>Lovelace</b>\",\"Company\":\"Acme\",\"Annual_Revenue\":120000}]}"), Last());
            Assert.Equal(("4150868000000002", "SUCCESS", "2026-10-12T14:00:00+02:00", "2026-10-12T14:00:00+02:00"), ((string?)made.Fields["id"], (string?)made.Fields["status"], (string?)made.Fields["created"], (string?)made.Fields["modified"]));
            Assert.False(made.Fields.ContainsKey("action"));

            var upserted = Assert.Single(await Run(ZohoCrmConnector.UpsertRecordOperation, new() { ["module"] = "Contacts", ["values"] = "{\"Last_Name\": \"Lovelace\", \"Email\": \"ada@example.com\"}", ["duplicate_check_fields"] = "Email, Phone" }));
            Assert.Equal(("POST", Api + "/Contacts/upsert", "{\"data\":[{\"Last_Name\":\"Lovelace\",\"Email\":\"ada@example.com\"}],\"duplicate_check_fields\":[\"Email\",\"Phone\"]}"), Last());
            Assert.Equal(("4150868000000001", "update"), ((string?)upserted.Fields["id"], (string?)upserted.Fields["action"]));
            await Run(ZohoCrmConnector.UpsertRecordOperation, new() { ["module"] = "Contacts", ["values"] = "{\"Last_Name\": \"Lovelace\"}" });
            Assert.Equal("{\"data\":[{\"Last_Name\":\"Lovelace\"}]}", Last().Item3); // the module's own duplicate check

            var changed = Assert.Single(await Run(ZohoCrmConnector.UpdateRecordOperation, new() { ["module"] = "Deals", ["id"] = "4150868000000001", ["values"] = "{\"Stage\": \"Closed Won\", \"Account_Name\": {\"id\": \"4150868000000224005\"}}" }));
            Assert.Equal(("PUT", Api + "/Deals/4150868000000001", "{\"data\":[{\"Stage\":\"Closed Won\",\"Account_Name\":{\"id\":\"4150868000000224005\"}}]}"), Last());
            Assert.Equal("SUCCESS", (string?)changed.Fields["status"]);
            var gone = Assert.Single(await Run(ZohoCrmConnector.DeleteRecordOperation, new() { ["module"] = "Deals", ["id"] = "4150868000000001" }));
            Assert.Equal(("DELETE", Api + "/Deals/4150868000000001", (string?)null), Last());
            Assert.Equal("4150868000000001", (string?)gone.Fields["id"]);

            var refusing = new Zoho { Answer = (_, _) => (HttpStatusCode.Accepted, """{"data":[{"code":"INVALID_DATA","details":{"expected_data_type":"email","api_name":"Email"},"message":"invalid data","status":"error"}]}""") };
            Assert.Equal("Zoho CRM refused it: INVALID_DATA - invalid data (Email).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, ZohoCrmConnector.CreateRecordOperation, new() { ["module"] = "Leads", ["values"] = "{\"Email\": \"x\"}" })))).Message);
            var missing = new Zoho { Answer = (_, _) => (HttpStatusCode.NoContent, "") };
            Assert.Equal("No Leads record has the id 4150868000000009.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, ZohoCrmConnector.GetRecordOperation, new() { ["module"] = "Leads", ["id"] = "4150868000000009" })))).Message);

            var settings = new Zoho
            {
                Answer = (_, url) => url.Contains("/settings/fields", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"fields":[{"api_name":"Last_Name","field_label":"Last Name","data_type":"text","custom_field":false,"read_only":false},{"api_name":"Tier","field_label":"Tier","data_type":"picklist","custom_field":true,"read_only":false}]}""")
                    : (HttpStatusCode.OK, """{"modules":[{"api_name":"Leads","module_name":"Leads","plural_label":"Leads","singular_label":"Lead","api_supported":true,"creatable":true},{"api_name":"Sales_Orders","plural_label":"Sales Orders","singular_label":"Sales Order","api_supported":true,"creatable":true}]}"""),
            };
            var fields = await Contexts.DrainAsync(Connector(), Context(settings, ZohoCrmConnector.FieldsOperation, new() { ["module"] = "Leads" }));
            Assert.Equal(Api + "/settings/fields?module=Leads", settings.Requests[^1].Url);
            Assert.Equal(new[] { ("Last_Name", "Last Name", "text", false), ("Tier", "Tier", "picklist", true) }, fields.Select(field => ((string)field.Fields["api_name"]!, (string)field.Fields["label"]!, (string)field.Fields["type"]!, (bool)field.Fields["custom"]!)));
            var modules = await Contexts.DrainAsync(Connector(), Context(settings, ZohoCrmConnector.ModulesOperation, new()));
            Assert.Equal(Api + "/settings/modules", settings.Requests[^1].Url);
            Assert.Equal(new[] { ("Leads", "Leads", "Lead"), ("Sales_Orders", "Sales Orders", "Sales Order") }, modules.Select(module => ((string)module.Fields["api_name"]!, (string)module.Fields["name"]!, (string)module.Fields["singular"]!)));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_zohos_refusal_is_its_words()
        {
            var zoho = new Zoho();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(zoho, operation, parameters)))).Message;

            Assert.Equal("Give the module - its API name, Leads.", await Refused(ZohoCrmConnector.RecordsOperation, new()));
            Assert.Equal("'Sales Orders' is not a module's API name - Leads, Sales_Orders.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Sales Orders" }));
            Assert.Equal("'../users' is not a module's API name - Leads, Sales_Orders.", await Refused(ZohoCrmConnector.GetRecordOperation, new() { ["module"] = "../users", ["id"] = "1" }));
            Assert.Equal("Give the record's id.", await Refused(ZohoCrmConnector.GetRecordOperation, new() { ["module"] = "Leads" }));
            Assert.Equal("'abc' is not a record's id.", await Refused(ZohoCrmConnector.DeleteRecordOperation, new() { ["module"] = "Leads", ["id"] = "abc" }));
            Assert.Equal("Give the values - a JSON object of API names.", await Refused(ZohoCrmConnector.CreateRecordOperation, new() { ["module"] = "Leads" }));
            Assert.Equal("Give the values - a JSON object of API names.", await Refused(ZohoCrmConnector.CreateRecordOperation, new() { ["module"] = "Leads", ["values"] = "{}" }));
            Assert.Equal("The values are not a JSON object - {\"Last_Name\": \"Lovelace\"}.", await Refused(ZohoCrmConnector.UpdateRecordOperation, new() { ["module"] = "Leads", ["id"] = "1", ["values"] = "Last_Name=Lovelace" }));
            Assert.Equal("'Last Name' is not a field's API name - Last_Name.", await Refused(ZohoCrmConnector.UpsertRecordOperation, new() { ["module"] = "Leads", ["values"] = "{\"Last Name\": \"Lovelace\"}" }));
            Assert.Equal("'E-mail' is not a field's API name - Last_Name.", await Refused(ZohoCrmConnector.UpsertRecordOperation, new() { ["module"] = "Leads", ["values"] = "{\"Last_Name\": \"Lovelace\"}", ["duplicate_check_fields"] = "E-mail" }));
            Assert.Equal("'Last Name' is not a field's API name - Last_Name.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["fields"] = "Email, Last Name" }));
            Assert.Equal("'Created_Time newest' is not an order - a field's API name, then asc or desc: Created_Time desc.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["order"] = "Created_Time newest" }));
            Assert.Equal("'Created-Time desc' is not an order - a field's API name, then asc or desc: Created_Time desc.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["order"] = "Created-Time desc" }));
            Assert.Equal("'Created Time desc' is not an order - a field's API name, then asc or desc: Created_Time desc.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["order"] = "Created Time desc" }));
            Assert.Equal("'approved' is not a parameter - name=value, approved=false.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["query"] = "approved" }));
            Assert.Equal("'a&b=1' is not a parameter - name=value, approved=false.", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["query"] = "a&b=1" }));
            Assert.Equal("Give one of criteria, an e-mail address, a phone or a word.", await Refused(ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads" }));
            Assert.Equal("Give one of criteria, an e-mail address, a phone or a word.", await Refused(ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads", ["email"] = "ada@example.com", ["word"] = "Ada" }));
            Assert.Equal("'Last_Name:equals:Lovelace' are not criteria - (Last_Name:equals:Lovelace).", await Refused(ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads", ["criteria"] = "Last_Name:equals:Lovelace" }));
            Assert.Equal("The criteria has a line break - one line only.", await Refused(ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads", ["criteria"] = "(a:equals:b)\nor(c:equals:d)" }));
            Assert.Equal("'ada@x@y' is not an e-mail address.", await Refused(ZohoCrmConnector.SearchRecordsOperation, new() { ["module"] = "Leads", ["email"] = "ada@x@y" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads", ["max"] = "0" }));
            Assert.StartsWith("Zoho CRM has no operation 'convert_lead' - records, search_records,", await Refused("convert_lead", new()));
            Assert.Empty(zoho.Requests); // nothing sent, not even the token asked for

            var expired = new Zoho { Answer = (_, _) => (HttpStatusCode.Unauthorized, """{"code":"INVALID_TOKEN","details":{},"message":"invalid oauth token","status":"error"}""") };
            Assert.Equal("HTTP 401 INVALID_TOKEN: invalid oauth token", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(expired, ZohoCrmConnector.RecordsOperation, new() { ["module"] = "Leads" })))).Message);
            var invalid = new Zoho { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"data":[{"code":"MANDATORY_NOT_FOUND","details":{"api_name":"Last_Name"},"message":"required field not found","status":"error"}]}""") };
            Assert.Equal("HTTP 400 MANDATORY_NOT_FOUND: required field not found (Last_Name)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(invalid, ZohoCrmConnector.CreateRecordOperation, new() { ["module"] = "Leads", ["values"] = "{\"Company\": \"Acme\"}" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var zoho = new Zoho { Answer = (_, _) => (HttpStatusCode.OK, """{"users":[{"full_name":"Ada Lovelace","email":"ada@example.com","id":"4150868000000225013"}]}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(zoho), Api, settings: new Dictionary<string, string> { ["token_url"] = TokenUrl }, credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users?type=CurrentUser", "Zoho-oauthtoken access-1"), (zoho.Requests[1].Url, zoho.Requests[1].Authorization));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "zohocrm"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Crm }, Connector().Categories);
            Assert.Equal(9, Connector().Operations.Count);
            Assert.Equal(ZohoCrmConnector.DefaultTokenUrl, Assert.Single(Connector().ConfigSchema.Fields).Default);
        }
    }
}
