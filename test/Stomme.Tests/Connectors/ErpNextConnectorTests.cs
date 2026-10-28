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
using Stomme.Modules.Connectors.ErpNext;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - ERPNext (n8n's ERPNext node) without a server: every doctype, name, field, type, choice, child row, filter and
    /// order held to the doctype's own definition before anything is sent - read once a run; the key and secret in Frappe's
    /// token header and in no address; a document made with its child rows as JSON (n8n's text was a 500), changed and
    /// submitted by its docstatus, read and deleted, its name escaped; a list page by page with its fields, its filters in Frappe's
    /// form or as an object - n8n's operators taken - and its order; Frappe's own messages, its HTML taken out; a test naming the
    /// key's user.
    /// </summary>
    public sealed class ErpNextConnectorTests
    {
        private const string Address = "https://erp.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "a1b2c3d4e5f6a7b", ["secret-key"] = "s3cr3t-kafe" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Frappe : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)?> answer;

            public Frappe(Func<Sent, (HttpStatusCode Status, string Body)?>? answer = null) => this.answer = answer ?? (_ => null);

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                var (status, text) = answer(sent) ?? (sent.Url.Contains("getdoctype?doctype=ToDo", StringComparison.Ordinal) ? (HttpStatusCode.OK, ToDo)
                    : sent.Url.Contains("getdoctype?doctype=Contact", StringComparison.Ordinal) ? (HttpStatusCode.OK, Contact)
                    : sent.Method == "DELETE" ? (HttpStatusCode.Accepted, "{\"data\":\"ok\"}")
                    : (HttpStatusCode.OK, "{\"data\":{\"name\":\"v5vvcgq0fc\"}}"));
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        // ToDo and Contact as ERPNext 16 defines them (the fields the tests use), measured.
        private const string ToDo = "{\"docs\":[{\"name\":\"ToDo\",\"fields\":[{\"fieldname\":\"section\",\"fieldtype\":\"Section Break\"},{\"fieldname\":\"status\",\"fieldtype\":\"Select\",\"options\":\"Open\\nClosed\\nCancelled\",\"default\":\"Open\"},"
            + "{\"fieldname\":\"priority\",\"fieldtype\":\"Select\",\"options\":\"High\\nMedium\\nLow\",\"default\":\"Medium\"},{\"fieldname\":\"date\",\"fieldtype\":\"Date\"},{\"fieldname\":\"description\",\"fieldtype\":\"Text Editor\",\"reqd\":1},"
            + "{\"fieldname\":\"allocated_to\",\"fieldtype\":\"Link\",\"options\":\"User\"}]}]}";

        private const string Contact = "{\"docs\":[{\"name\":\"Contact\",\"fields\":[{\"fieldname\":\"first_name\",\"fieldtype\":\"Data\"},{\"fieldname\":\"unsubscribed\",\"fieldtype\":\"Check\"},{\"fieldname\":\"email_ids\",\"fieldtype\":\"Table\",\"options\":\"Contact Email\"}]},"
            + "{\"name\":\"Contact Email\",\"fields\":[{\"fieldname\":\"email_id\",\"fieldtype\":\"Data\",\"reqd\":1},{\"fieldname\":\"is_primary\",\"fieldtype\":\"Check\"}]}]}";

        private static Task<List<ConnectorRecord>> Run(Frappe frappe, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new ErpNextConnector(), Contexts.Extract(new StubEgress(frappe), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string At(string route) => Address + route;

        [Fact]
        public void It_declares_itself_as_n8ns_erpnext_node()
        {
            IConnector connector = new ErpNextConnector();
            Assert.Equal(("erpnext", "ERPNext", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "documents", "document", "document_create", "document_update", "document_delete" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_the_doctypes_definition_before_a_write_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var frappe = new Frappe();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(frappe, operation, parameters, endpoint));
                Assert.DoesNotContain(frappe.Requests, sent => sent.Method != "GET" || sent.Url.Contains("/api/resource/", StringComparison.Ordinal)); // at most the definition read
                return refusal.Message;
            }

            Assert.StartsWith("ERPNext has no operation 'document_submit'", await Refused("document_submit", new() { ["doctype"] = "ToDo" }));
            Assert.Equal("Give the document type - Customer, Sales Order, ToDo (doctype).", await Refused("documents", new()));
            Assert.Equal("'ToDo/../User' is not a doctype's name - Customer, Sales Order (doctype).", await Refused("documents", new() { ["doctype"] = "ToDo/../User" }));
            Assert.Equal("Give the document - its name, one line (name).", await Refused("document", new() { ["doctype"] = "ToDo" }));
            Assert.Equal("Give the fields - a JSON object, {\"description\": \"Ring Ada\"} (fields).", await Refused("document_create", new() { ["doctype"] = "ToDo" }));
            Assert.Equal("The fields are not JSON (fields).", await Refused("document_create", new() { ["doctype"] = "ToDo", ["fields"] = "description = Ring Ada" }));
            Assert.Equal("Give at least one field (fields).", await Refused("document_update", new() { ["doctype"] = "ToDo", ["name"] = "v5", ["fields"] = "{}" }));
            Assert.Equal("ToDo has no field 'descripton' (fields).", await Refused("document_create", new() { ["doctype"] = "ToDo", ["fields"] = "{\"descripton\": \"Ring Ada\"}" })); // ERPNext drops it without a word
            Assert.Equal("ToDo has no field 'prio' - priority? (fields).", await Refused("document_update", new() { ["doctype"] = "ToDo", ["name"] = "v5", ["fields"] = "{\"prio\": \"High\"}" }));
            Assert.Equal("priority is one of \"High\", \"Medium\", \"Low\" (fields).", await Refused("document_create", new() { ["doctype"] = "ToDo", ["fields"] = "{\"description\": \"X\", \"priority\": \"Urgent\"}" }));
            Assert.Equal("'imorgon' is not a date - 2026-10-21 (date).", await Refused("document_create", new() { ["doctype"] = "ToDo", ["fields"] = "{\"description\": \"X\", \"date\": \"imorgon\"}" })); // ERPNext: a 500
            Assert.Equal("Give description - ToDo requires it (fields).", await Refused("document_create", new() { ["doctype"] = "ToDo", ["fields"] = "{\"priority\": \"High\"}" }));
            Assert.Equal("A docstatus is 0 (draft), 1 (submitted) or 2 (cancelled) (fields).", await Refused("document_update", new() { ["doctype"] = "ToDo", ["name"] = "v5", ["fields"] = "{\"docstatus\": 3}" }));
            Assert.Equal("unsubscribed is true or false (fields).", await Refused("document_create", new() { ["doctype"] = "Contact", ["fields"] = "{\"unsubscribed\": \"kanske\"}" }));
            Assert.Equal("email_ids is a list of rows - [{\"item_code\": \"KAFFE\", \"qty\": 3}] (email_ids).", await Refused("document_create", new() { ["doctype"] = "Contact", ["fields"] = "{\"email_ids\": {\"email_id\": \"ada@kafe.example\"}}" }));
            Assert.Equal("Give email_id - Contact Email requires it (email_ids).", await Refused("document_create", new() { ["doctype"] = "Contact", ["fields"] = "{\"email_ids\": [{\"is_primary\": 1}]}" }));
            Assert.Equal("Contact Email has no field 'email' - email_id? (email_ids).", await Refused("document_create", new() { ["doctype"] = "Contact", ["fields"] = "{\"email_ids\": [{\"email\": \"ada@kafe.example\"}]}" }));
            Assert.Equal("first_name is a value, not a list or an object (fields).", await Refused("document_create", new() { ["doctype"] = "Contact", ["fields"] = "{\"first_name\": [\"Ada\"]}" }));
            Assert.Equal("ToDo has no field 'nosuch' (filters).", await Refused("documents", new() { ["doctype"] = "ToDo", ["filters"] = "[[\"nosuch\", \"=\", \"x\"]]" }));
            Assert.Equal("'~' is no operator of Frappe's - =, !=, >, <, >=, <=, like, not like, in, not in, is, between (filters).", await Refused("documents", new() { ["doctype"] = "ToDo", ["filters"] = "[[\"priority\", \"~\", \"x\"]]" })); // ERPNext: a 500
            Assert.Equal("Each filter is a field, an operator and a value - [\"status\", \"=\", \"Open\"] (filters).", await Refused("documents", new() { ["doctype"] = "ToDo", ["filters"] = "[[\"priority\", \"=\"]]" }));
            Assert.Equal("The filters are JSON - [[\"status\", \"=\", \"Open\"]] or {\"status\": \"Open\"} (filters).", await Refused("documents", new() { ["doctype"] = "ToDo", ["filters"] = "\"Open\"" }));
            Assert.Equal("ToDo has no field 'prio' - priority? (fields).", await Refused("documents", new() { ["doctype"] = "ToDo", ["fields"] = "name, prio" }));
            Assert.Equal("'modified sideways' is not a field and asc or desc - modified desc (order_by).", await Refused("documents", new() { ["doctype"] = "ToDo", ["order_by"] = "modified sideways" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("documents", new() { ["doctype"] = "ToDo", ["max"] = "0" }));
            Assert.Equal("Give ERPNext's address - https://erp.kafe.example.", await Refused("documents", new() { ["doctype"] = "ToDo" }, ""));
            Assert.Equal("The ERPNext connection has no API secret.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ErpNextConnector(), Contexts.Extract(new StubEgress(new Frappe()), Address, "documents", new Dictionary<string, string> { ["doctype"] = "ToDo" }, new Dictionary<string, string>(), new Dictionary<string, string> { ["access-key"] = "a1" })))).Message);
        }

        [Fact]
        public async Task A_document_is_made_with_its_rows_as_json_changed_submitted_read_and_deleted()
        {
            var frappe = new Frappe();
            await Run(frappe, "document_create", new() { ["doctype"] = "Contact", ["query"] = "{\"first_name\": \"Ada\", \"unsubscribed\": \"false\", \"email_ids\": \"[{\\\"email_id\\\": \\\"ada@kafe.example\\\", \\\"is_primary\\\": 1}]\"}" }, Address + "/api");
            Assert.Equal(new[] { ("GET", At("/api/method/frappe.desk.form.load.getdoctype?doctype=Contact")), ("POST", At("/api/resource/Contact")) }, frappe.Requests.Select(sent => (sent.Method, sent.Url))); // the API's own address given: the site's
            Assert.Equal(("token a1b2c3d4e5f6a7b:s3cr3t-kafe", "{\"first_name\":\"Ada\",\"unsubscribed\":0,\"email_ids\":[{\"email_id\":\"ada@kafe.example\",\"is_primary\":1}]}"), (frappe.Requests[1].Authorization, frappe.Requests[1].Body)); // the rows as JSON, n8n's text a 500

            var todo = new Frappe();
            await Run(todo, "document_update", new() { ["doctype"] = "ToDo", ["name"] = "Kafé / 1", ["fields"] = "{\"status\": \"Closed\", \"date\": \"2026-11-01\", \"docstatus\": \"1\"}" });
            await Run(todo, "document", new() { ["doctype"] = "ToDo", ["name"] = "Kafé / 1" });
            Assert.Equal(new[] { ("PUT", At("/api/resource/ToDo/Kaf%C3%A9%20%2F%201"), "{\"status\":\"Closed\",\"date\":\"2026-11-01\",\"docstatus\":1}"), ("GET", At("/api/resource/ToDo/Kaf%C3%A9%20%2F%201"), (string?)null) },
                todo.Requests.Where(sent => sent.Url.Contains("/api/resource/", StringComparison.Ordinal)).Select(sent => (sent.Method, sent.Url, sent.Body)));
            Assert.Single(todo.Requests, sent => sent.Url.Contains("getdoctype", StringComparison.Ordinal)); // read once a run, not for a read

            var deleted = new Frappe();
            Assert.Equal(true, (bool?)Assert.Single(await Run(deleted, "document_delete", new() { ["doctype"] = "Sales Order", ["name"] = "SAL-ORD-2026-00001" })).Fields["deleted"]);
            Assert.Equal(("DELETE", At("/api/resource/Sales%20Order/SAL-ORD-2026-00001")), (deleted.Requests[0].Method, deleted.Requests[0].Url));
        }

        [Fact]
        public async Task A_list_goes_page_by_page_with_its_fields_filters_and_order()
        {
            var page = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"name\":\"t{i}\"}}")) + "]}";
            var frappe = new Frappe(sent => sent.Url.Contains("/api/resource/ToDo?", StringComparison.Ordinal) ? (HttpStatusCode.OK, sent.Url.Contains("limit_start=0", StringComparison.Ordinal) ? page : "{\"data\":[{\"name\":\"last\"}]}") : null);
            Assert.Equal(501, (await Run(frappe, "documents", new() { ["doctype"] = "ToDo", ["max"] = "1000" })).Count);
            Assert.Equal(new[] { At("/api/resource/ToDo?limit_start=0&limit_page_length=500"), At("/api/resource/ToDo?limit_start=500&limit_page_length=500") }, frappe.Requests.Where(sent => sent.Url.Contains("/api/resource/", StringComparison.Ordinal)).Select(sent => sent.Url));
            Assert.Equal(3, (await Run(new Frappe(sent => sent.Url.Contains("/api/resource/ToDo?", StringComparison.Ordinal) ? (HttpStatusCode.OK, page) : null), "documents", new() { ["doctype"] = "ToDo", ["max"] = "3" })).Count);

            var filtered = new Frappe(sent => sent.Url.Contains("/api/resource/ToDo?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"data\":[]}") : null);
            await Run(filtered, "documents", new() { ["doctype"] = "ToDo", ["fields"] = "name, priority", ["query"] = "[[\"priority\", \"isNot\", \"Low\"], [\"ToDo\", \"description\", \"like\", \"%Ada%\"]]", ["order_by"] = "modified desc" });
            var url = Uri.UnescapeDataString(Assert.Single(filtered.Requests, sent => sent.Url.Contains("/api/resource/", StringComparison.Ordinal)).Url);
            Assert.Equal(At("/api/resource/ToDo?fields=[\"name\",\"priority\"]&filters=[[\"ToDo\",\"priority\",\"!=\",\"Low\"],[\"ToDo\",\"description\",\"like\",\"%Ada%\"]]&order_by=modified desc&limit_start=0&limit_page_length=500"), url); // n8n's isNot as Frappe's !=
            var pairs = new Frappe(sent => sent.Url.Contains("/api/resource/ToDo?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"data\":[]}") : null);
            await Run(pairs, "documents", new() { ["doctype"] = "ToDo", ["fields"] = "*", ["filters"] = "{\"status\": \"Open\"}" });
            Assert.Contains("fields=[\"*\"]&filters=[[\"ToDo\",\"status\",\"=\",\"Open\"]]", Uri.UnescapeDataString(pairs.Requests[^1].Url), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Frappes_messages_are_said_and_an_answer_that_is_not_its_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "document", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Frappe(sent => sent.Url.Contains("getdoctype", StringComparison.Ordinal) ? null : (status, body)), operation, parameters ?? new() { ["doctype"] = "ToDo", ["name"] = "v5" }))).Message;

            Assert.Equal("ERPNext answered HTTP 404: ToDo nosuch not found.", await Failed(HttpStatusCode.NotFound, "{\"exc_type\":\"DoesNotExistError\",\"_server_messages\":\"[\\\"{\\\\\\\"message\\\\\\\":\\\\\\\"ToDo nosuch not found\\\\\\\",\\\\\\\"indicator\\\\\\\":\\\\\\\"red\\\\\\\"}\\\"]\"}"));
            Assert.Equal("ERPNext answered HTTP 417: Could not find Row #1: Item Code: KAFFE-1.", await Failed((HttpStatusCode)417, "{\"exception\":\"frappe.exceptions.LinkValidationError: Could not find Row #1: Item Code: KAFFE-1\",\"exc_type\":\"LinkValidationError\"}"));
            Assert.Equal("ERPNext answered HTTP 403: Insufficient Permission for ToDo.", await Failed(HttpStatusCode.Forbidden, "{\"_server_messages\":\"[\\\"{\\\\\\\"message\\\\\\\":\\\\\\\"Insufficient Permission for <b>ToDo</b>\\\\\\\"}\\\"]\"}")); // n8n said "DocType unavailable"
            Assert.Equal("ERPNext answered HTTP 401. - check the API key and secret.", await Failed(HttpStatusCode.Unauthorized, "{\"exc_type\":\"AuthenticationError\"}"));
            Assert.Equal("ERPNext answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>ERPNext</html>"));
            Assert.Equal("ERPNext answered without the document - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"message\":\"ok\"}"));
            Assert.Equal("ERPNext answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"data\":{\"name\":\"x\"}}", "documents", new() { ["doctype"] = "ToDo" }));
            Assert.Equal("ERPNext answered HTTP 404: DocType NoSuch not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Frappe(_ => (HttpStatusCode.NotFound, "{\"exc_type\":\"DoesNotExistError\",\"_server_messages\":\"[\\\"{\\\\\\\"message\\\\\\\":\\\\\\\"DocType NoSuch not found\\\\\\\"}\\\"]\"}")), "documents", new() { ["doctype"] = "NoSuch" }))).Message);
        }

        [Fact]
        public async Task A_test_names_the_keys_user()
        {
            var connector = new ErpNextConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Frappe(_ => (HttpStatusCode.OK, "{\"message\":\"Administrator\"}"))), Address, null, Credentials));
            Assert.Equal((true, "ERPNext answers: the key is Administrator's."), (ok.Success, ok.Message));
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Frappe(_ => (HttpStatusCode.Unauthorized, "{\"exc_type\":\"AuthenticationError\"}"))), Address, null, Credentials));
            Assert.Equal((false, "ERPNext answered HTTP 401. - check the API key and secret."), (refused.Success, refused.Message));
        }
    }

    /// <summary>
    /// BL-229 - ERPNext LIVE, against this machine's throwaway ERPNext 16 (scripts/erpnext-test-server.ps1) - fail-not-skip without
    /// it (D46): a test naming the key's user and a wrong key refused; a contact made with its e-mail rows as JSON - n8n's text a
    /// 500 - read back with them; a ToDo made, found by a filter n8n's operators name, changed, a field ToDo has not got refused
    /// where ERPNext would drop it; both deleted, the second delete Frappe's own 404.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class ErpNextConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Keys => new() { ["access-key"] = ErpNextTestEndpoint.Key, ["secret-key"] = ErpNextTestEndpoint.Secret };

        private static HostConnectorEgress Egress => new(new[] { new Uri(ErpNextTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new ErpNextConnector(), Contexts.Extract(Egress, ErpNextTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Keys, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        [Fact]
        public async Task A_test_names_the_user_and_a_wrong_key_is_refused()
        {
            var tested = await new ErpNextConnector().TestConnectionAsync(Contexts.Test(Egress, ErpNextTestEndpoint.Endpoint, credentials: Keys, timeoutSeconds: 60));
            Assert.Equal((true, "ERPNext answers: the key is Administrator's."), (tested.Success, tested.Message));
            var wrong = await new ErpNextConnector().TestConnectionAsync(Contexts.Test(Egress, ErpNextTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["access-key"] = "0123456789abcde", ["secret-key"] = "wrong" }, timeoutSeconds: 60));
            Assert.False(wrong.Success);
            Assert.StartsWith("ERPNext answered HTTP 401", wrong.Message, StringComparison.Ordinal);
            Assert.EndsWith(" - check the API key and secret.", wrong.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_contact_with_its_rows_and_a_todo_are_made_found_changed_and_deleted()
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var contact = await One("document_create", new() { ["doctype"] = "Contact", ["fields"] = $"{{\"first_name\": \"Ada {marker}\", \"email_ids\": [{{\"email_id\": \"ada.{marker}@kafe.example\", \"is_primary\": true}}]}}" });
            var contactName = (string)contact["name"]!;
            var todo = await One("document_create", new() { ["doctype"] = "ToDo", ["fields"] = $"{{\"description\": \"Ring Ada {marker}\", \"priority\": \"High\", \"date\": \"2026-11-01\"}}" });
            var todoName = (string)todo["name"]!;
            output.WriteLine($"contact {contactName}, todo {todoName}");
            try
            {
                Assert.Contains($"ada.{marker}@kafe.example", (string?)(await One("document", new() { ["doctype"] = "Contact", ["name"] = contactName }))["email_ids"], StringComparison.Ordinal);
                var found = await All("documents", new() { ["doctype"] = "ToDo", ["fields"] = "name, description, priority", ["filters"] = $"[[\"description\", \"like\", \"%{marker}%\"], [\"priority\", \"isNot\", \"Low\"]]" });
                Assert.Equal((todoName, "High"), ((string?)Assert.Single(found).Fields["name"], (string?)found[0].Fields["priority"]));
                Assert.Equal("Closed", (string?)(await One("document_update", new() { ["doctype"] = "ToDo", ["name"] = todoName, ["fields"] = "{\"status\": \"Closed\"}" }))["status"]);
                Assert.Equal("ToDo has no field 'statuss' - status? (fields).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("document_update", new() { ["doctype"] = "ToDo", ["name"] = todoName, ["fields"] = "{\"statuss\": \"Open\"}" }))).Message);
                Assert.Equal("priority is one of \"High\", \"Medium\", \"Low\" (fields).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("document_update", new() { ["doctype"] = "ToDo", ["name"] = todoName, ["fields"] = "{\"priority\": \"Urgent\"}" }))).Message);
            }
            finally
            {
                Assert.Equal(true, (bool?)(await One("document_delete", new() { ["doctype"] = "ToDo", ["name"] = todoName }))["deleted"]);
                Assert.Equal(true, (bool?)(await One("document_delete", new() { ["doctype"] = "Contact", ["name"] = contactName }))["deleted"]);
            }

            Assert.StartsWith("ERPNext answered HTTP 404", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("document_delete", new() { ["doctype"] = "ToDo", ["name"] = todoName }))).Message, StringComparison.Ordinal);
        }
    }
}
