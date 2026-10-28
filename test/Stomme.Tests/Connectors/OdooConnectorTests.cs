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
using Stomme.Modules.Connectors.Odoo;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Odoo (n8n's Odoo node: any model's records created, got, read, updated and deleted): over JSON-RPC the user's id
    /// asked for once with the login, then execute_kw with the records (or a domain, or a create's values) first and the rest by
    /// name; over JSON-2 the API key as a bearer, the database in its header, the arguments by name; the database the setting's or
    /// the host's first label; a model's records page by page up to the most asked for, in an order, a link its id and its name, a
    /// list of links its ids; records got, made, changed and deleted; the models and a model's fields; any public method;
    /// what could go wrong refused before anything is sent; Odoo's refusal its exception and message; a test counting contacts.
    /// </summary>
    public sealed class OdooConnectorTests
    {
        private const string Url = "https://mycompany.odoo.example";

        // JSON as the connector writes it: text, its accents and signs, as it is.
        private static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "ada@example.com", ["password"] = "key-1" };

        private sealed class Odoo : HttpMessageHandler
        {
            public List<(string Url, string? Authorization, string? Database, JsonNode? Body)> Requests { get; } = new();

            public Func<string, JsonNode?, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":7}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-Odoo-Database", out var db) ? db.Single() : null, body));
                var (status, answer) = Answer(url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Odoo odoo, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.Extract(new StubEgress(odoo), Url, operation, parameters, settings: settings ?? new Dictionary<string, string>(), credentials: credentials ?? Credentials);

        private static readonly Dictionary<string, string> Json2 = new() { ["api"] = "JSON2", ["database"] = "prod_db" };

        private static string Rpc(string result) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}";

        private static string Partners(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":" + n + ",\"name\":\"Café <b>" + n + "</b>\",\"country_id\":[233,\"Sweden\"],\"category_id\":[1,2],\"email\":false,\"credit\":12.5}")) + "]";

        [Fact]
        public async Task Over_json_rpc_the_user_signs_in_once_and_records_are_read_page_by_page()
        {
            var odoo = new Odoo
            {
                Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7")
                    : body?["params"]?["args"]?[6]?["offset"]?.GetValue<int>() == 100 ? Rpc(Partners(101, 20)) : Rpc(Partners(1, 100))),
            };
            var records = await Contexts.DrainAsync(new OdooConnector(), Context(odoo, OdooConnector.RecordsOperation, new()
            {
                ["model"] = "res.partner", ["domain"] = "[[\"is_company\", \"=\", true], \"|\", [\"country_id.code\", \"=\", \"SE\"], [\"name\", \"ilike\", \"Café\"]]", ["fields"] = "name, country_id, category_id, email, credit", ["order"] = "name asc, id desc", ["max"] = "150",
            }));

            Assert.Equal(120, records.Count); // the last page short: the end
            Assert.All(odoo.Requests, request => Assert.Equal(Url + "/jsonrpc", request.Url));
            Assert.Equal("""{"jsonrpc":"2.0","method":"call","id":1,"params":{"service":"common","method":"login","args":["mycompany","ada@example.com","key-1"]}}""", odoo.Requests[0].Body!.ToJsonString(Relaxed)); // the host's first label the database
            Assert.Equal(3, odoo.Requests.Count); // signed in once
            Assert.Equal("""{"service":"object","method":"execute_kw","args":["mycompany",7,"key-1","res.partner","search_read",[[["is_company","=",true],"|",["country_id.code","=","SE"],["name","ilike","Café"]]],{"fields":["name","country_id","category_id","email","credit"],"offset":0,"limit":100,"order":"name asc, id desc"}]}""",
                odoo.Requests[1].Body!["params"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.Equal((100, 50), (odoo.Requests[2].Body!["params"]!["args"]![6]!["offset"]!.GetValue<int>(), odoo.Requests[2].Body!["params"]!["args"]![6]!["limit"]!.GetValue<int>())); // no more than is left
            var ada = records[0].Fields;
            Assert.Equal((1L, "Café <b>1</b>", 233L, "Sweden", "1, 2", (object?)false, (object?)12.5),
                ((long?)ada["id"], (string?)ada["name"], (long?)ada["country_id"], (string?)ada["country_id_name"], (string?)ada["category_id"], ada["email"], ada["credit"]));

            var greedy = new Odoo { Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7") : Rpc(Partners(1, 100))) };
            Assert.Equal(130, (await Contexts.DrainAsync(new OdooConnector(), Context(greedy, OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["max"] = "130" }))).Count); // a page holding more than asked for: never past the most

            var everything = new Odoo { Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7") : Rpc(Partners(1, 100))) };
            Assert.Equal(1000, (await Contexts.DrainAsync(new OdooConnector(), Context(everything, OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" }))).Count); // by default up to 1,000
            Assert.Equal("""[[[]],{"fields":[],"offset":0,"limit":100}]""", new JsonArray(everything.Requests[1].Body!["params"]!["args"]![5]!.DeepClone(), everything.Requests[1].Body!["params"]!["args"]![6]!.DeepClone()).ToJsonString(Relaxed)); // every record, Odoo's fields
        }

        [Fact]
        public async Task Records_are_got_made_changed_and_deleted_over_either_api()
        {
            var rpc = new Odoo
            {
                Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7")
                    : body?["params"]?["args"]?[4]?.GetValue<string>() switch { "read" => Rpc(Partners(7, 2)), "create" => Rpc("42"), _ => Rpc("true") }),
            };
            JsonNode? Args(int index) => rpc.Requests[index].Body!["params"]!["args"];
            var got = await Contexts.DrainAsync(new OdooConnector(), Context(rpc, OdooConnector.GetRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "7, 8", ["fields"] = "name" }, new() { ["database"] = "prod_db" }));
            Assert.Equal("""["prod_db",7,"key-1","res.partner","read",[[7,8]],{"fields":["name"]}]""", Args(1)!.ToJsonString(Relaxed));
            Assert.Equal(new long[] { 7, 8 }, got.Select(record => (long)record.Fields["id"]!));
            var made = Assert.Single(await Contexts.DrainAsync(new OdooConnector(), Context(rpc, OdooConnector.CreateRecordOperation, new() { ["model"] = "crm.lead", ["values"] = "{\"name\": \"Big deal\", \"expected_revenue\": 5000}" })));
            Assert.Equal("""["mycompany",7,"key-1","crm.lead","create",[{"name":"Big deal","expected_revenue":5000}],{}]""", Args(3)!.ToJsonString(Relaxed)); // one record's values
            Assert.Equal(42L, (long?)made.Fields["id"]);
            var changed = await Contexts.DrainAsync(new OdooConnector(), Context(rpc, OdooConnector.UpdateRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "7,8", ["values"] = "{\"phone\": \"+46 8 555\"}" }));
            Assert.Equal("""["mycompany",7,"key-1","res.partner","write",[[7,8],{"phone":"+46 8 555"}],{}]""", Args(5)!.ToJsonString(Relaxed));
            Assert.Equal(new[] { (7L, true), (8L, true) }, changed.Select(record => ((long)record.Fields["id"]!, (bool)record.Fields["updated"]!)));
            var gone = Assert.Single(await Contexts.DrainAsync(new OdooConnector(), Context(rpc, OdooConnector.DeleteRecordsOperation, new() { ["model"] = "note.note", ["ids"] = "9" })));
            Assert.Equal("""["mycompany",7,"key-1","note.note","unlink",[[9]],{}]""", Args(7)!.ToJsonString(Relaxed));
            Assert.Equal((9L, true), ((long?)gone.Fields["id"], (bool?)gone.Fields["deleted"]));
            await Contexts.DrainAsync(new OdooConnector(), Context(rpc, OdooConnector.CallOperation, new() { ["model"] = "sale.order", ["method"] = "action_confirm", ["arguments"] = "{\"ids\": [3], \"context\": {\"lang\": \"sv_SE\"}}" }));
            Assert.Equal("""["mycompany",7,"key-1","sale.order","action_confirm",[[3]],{"context":{"lang":"sv_SE"}}]""", Args(9)!.ToJsonString(Relaxed)); // its records first, the rest by name

            var json2 = new Odoo
            {
                Answer = (url, _) => (HttpStatusCode.OK, url.EndsWith("/create", StringComparison.Ordinal) ? "[42]" : url.EndsWith("/fields_get", StringComparison.Ordinal)
                    ? """{"name":{"string":"Name","type":"char","required":true},"country_id":{"string":"Country","type":"many2one","relation":"res.country","readonly":false}}"""
                    : url.EndsWith("/search_read", StringComparison.Ordinal) ? """[{"model":"res.partner","name":"Contact"}]""" : "true"),
            };
            (string, string?, string?, string) Last() => (json2.Requests[^1].Url, json2.Requests[^1].Authorization, json2.Requests[^1].Database, json2.Requests[^1].Body!.ToJsonString(Relaxed));
            var made2 = Assert.Single(await Contexts.DrainAsync(new OdooConnector(), Context(json2, OdooConnector.CreateRecordOperation, new() { ["model"] = "res.partner", ["values"] = "{\"name\": \"Ada\"}" }, Json2, new() { ["password"] = "key-2" })));
            Assert.Equal((Url + "/json/2/res.partner/create", "bearer key-2", "prod_db", """{"vals_list":[{"name":"Ada"}]}"""), Last()); // no login asked for: the key a bearer
            Assert.Equal(42L, (long?)made2.Fields["id"]);
            await Contexts.DrainAsync(new OdooConnector(), Context(json2, OdooConnector.UpdateRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "42", ["values"] = "{\"name\": \"Ada L\"}" }, Json2));
            Assert.Equal((Url + "/json/2/res.partner/write", """{"ids":[42],"vals":{"name":"Ada L"}}"""), (Last().Item1, Last().Item4));
            var fields = await Contexts.DrainAsync(new OdooConnector(), Context(json2, OdooConnector.FieldsOperation, new() { ["model"] = "res.partner" }, Json2));
            Assert.Equal((Url + "/json/2/res.partner/fields_get", """{"attributes":["string","type","required","readonly","relation"]}"""), (Last().Item1, Last().Item4));
            Assert.Equal(new[] { ("country_id", "Country", "many2one", false, "res.country"), ("name", "Name", "char", true, (string?)null) },
                fields.Select(field => ((string)field.Fields["name"]!, (string)field.Fields["label"]!, (string)field.Fields["type"]!, (bool)field.Fields["required"]!, (string?)field.Fields["relation"])));
            var models = await Contexts.DrainAsync(new OdooConnector(), Context(json2, OdooConnector.ModelsOperation, new() { ["query"] = "partner" }, Json2));
            Assert.Equal((Url + "/json/2/ir.model/search_read", """{"domain":["|",["model","ilike","partner"],["name","ilike","partner"]],"fields":["model","name"],"offset":0,"limit":100,"order":"model"}"""), (Last().Item1, Last().Item4));
            Assert.Equal(("res.partner", "Contact"), ((string?)Assert.Single(models).Fields["model"], (string?)models[0].Fields["name"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_odoos_refusal_is_its_words()
        {
            var odoo = new Odoo();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new OdooConnector(), Context(odoo, operation, parameters, settings, credentials)))).Message;

            Assert.Equal("'xmlrpc' is not an Odoo API - jsonrpc or json2.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" }, new() { ["api"] = "xmlrpc" }));
            Assert.Equal("'prod db' is not a database's name.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" }, new() { ["database"] = "prod db" }));
            Assert.Equal("Give the connection's password or API key.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" }, credentials: new() { ["username"] = "ada" }));
            Assert.Equal("Give the connection's login - JSON-RPC signs in with it.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" }, credentials: new() { ["password"] = "key" }));
            Assert.Equal("'Res Partner' is not a model - res.partner, crm.lead.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "Res Partner" }));
            Assert.Equal("'res.partner/../x' is not a model - res.partner, crm.lead.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner/../x" }));
            Assert.Equal("'is_company = true' is not an Odoo domain - [[\"is_company\", \"=\", true], [\"name\", \"ilike\", \"Ada\"]].", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["domain"] = "is_company = true" }));
            Assert.Equal("'[[\"a\", \"=\"]]' is not an Odoo domain - [[\"is_company\", \"=\", true], [\"name\", \"ilike\", \"Ada\"]].", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["domain"] = "[[\"a\", \"=\"]]" }));
            Assert.Equal("'Name' is not a field's name.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["fields"] = "Name" }));
            Assert.Equal("'name; drop' is not an order - name asc, create_date desc.", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["order"] = "name; drop" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(OdooConnector.RecordsOperation, new() { ["model"] = "res.partner", ["max"] = "0" }));
            Assert.Equal("Give the records' ids - 7, or 7, 8, 9.", await Refused(OdooConnector.GetRecordsOperation, new() { ["model"] = "res.partner" }));
            Assert.Equal("'x7' is not a record's id.", await Refused(OdooConnector.DeleteRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "7, x7" }));
            Assert.Equal("'0' is not a record's id.", await Refused(OdooConnector.GetRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "0" }));
            Assert.Equal("The values are not a JSON object - {\"name\": \"Ada\"}.", await Refused(OdooConnector.CreateRecordOperation, new() { ["model"] = "res.partner", ["values"] = "name=Ada" }));
            Assert.Equal("Give at least one field's value.", await Refused(OdooConnector.UpdateRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "7", ["values"] = "{}" }));
            Assert.Equal("'_private' is not a public method's name - action_confirm.", await Refused(OdooConnector.CallOperation, new() { ["model"] = "res.partner", ["method"] = "_private" }));
            Assert.Equal("The arguments are not a JSON object - {\"ids\": [7]}.", await Refused(OdooConnector.CallOperation, new() { ["model"] = "res.partner", ["method"] = "action_x", ["arguments"] = "[7]" }));
            Assert.StartsWith("Odoo has no operation 'purge' - records, get_records,", await Refused("purge", new() { ["model"] = "res.partner" }));
            Assert.Empty(odoo.Requests); // nothing sent

            var badLogin = new Odoo { Answer = (_, _) => (HttpStatusCode.OK, Rpc("false")) };
            Assert.Equal("Odoo refused the login to mycompany - check the login, the password or API key and the database.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new OdooConnector(), Context(badLogin, OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" })))).Message);
            var noUser = new Odoo { Answer = (_, _) => (HttpStatusCode.OK, Rpc("0")) };
            Assert.StartsWith("Odoo refused the login to mycompany", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new OdooConnector(), Context(noUser, OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" })))).Message); // no user's id
            var refusing = new Odoo { Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7") : """{"jsonrpc":"2.0","id":1,"error":{"code":200,"message":"Odoo Server Error","data":{"name":"odoo.exceptions.AccessError","message":"You are not allowed to access 'Contact' (res.partner) records."}}}""") };
            Assert.Equal("Odoo refused - odoo.exceptions.AccessError: You are not allowed to access 'Contact' (res.partner) records.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new OdooConnector(), Context(refusing, OdooConnector.RecordsOperation, new() { ["model"] = "res.partner" })))).Message);
            var refusing2 = new Odoo { Answer = (_, _) => (HttpStatusCode.NotFound, """{"name":"odoo.exceptions.MissingError","message":"Record does not exist or has been deleted."}""") };
            Assert.Equal("HTTP 404 odoo.exceptions.MissingError: Record does not exist or has been deleted.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new OdooConnector(), Context(refusing2, OdooConnector.GetRecordsOperation, new() { ["model"] = "res.partner", ["ids"] = "7" }, Json2)))).Message);
        }

        [Fact]
        public async Task A_test_counts_the_contacts()
        {
            var odoo = new Odoo { Answer = (_, body) => (HttpStatusCode.OK, body?["params"]?["service"]?.GetValue<string>() == "common" ? Rpc("7") : Rpc("42")) };
            var tested = await new OdooConnector().TestConnectionAsync(Contexts.Test(new StubEgress(odoo), Url, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Signed in to mycompany - 42 contacts."), (tested.Success, tested.Message));
            Assert.Equal("""["mycompany",7,"key-1","res.partner","search_count",[[]],{}]""", odoo.Requests[1].Body!["params"]!["args"]!.ToJsonString(Relaxed));
            var refused = await new OdooConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Odoo { Answer = (_, _) => (HttpStatusCode.OK, Rpc("false")) }), Url, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((false, "Odoo refused the login to mycompany - check the login, the password or API key and the database."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "odoo"), (new OdooConnector().AuthType, new OdooConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Crm, ConnectorCategory.Commerce }, new OdooConnector().Categories);
        }
    }
}
