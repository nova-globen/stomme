#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.InvoiceNinja;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Invoice Ninja (n8n's Invoice Ninja node) without a server: every id, date, amount, percentage, choice, line and
    /// contact held to Invoice Ninja's shape before anything is sent; the token in Invoice Ninja's header and in no address; an
    /// invoice made under version 5's names - its date as date, not n8n's invoice_date, n8n's line names taken - marked sent and
    /// part paid in its query; a country, a currency and a payment type looked up by code or name in Invoice Ninja's own lists;
    /// a payment held to its invoice's balance; a list page by page by number, never by the next address Invoice Ninja gives,
    /// its filters under Invoice Ninja's names; an e-mail by /emails, not n8n's route; a task's time as its log; Invoice Ninja's
    /// messages; a test naming the company.
    /// </summary>
    public sealed class InvoiceNinjaConnectorTests
    {
        private const string Address = "https://ninja.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "kafe-token-1" };

        private sealed record Sent(string Method, string Url, string? Token, string? Requested, string? Body);

        private sealed class Ninja : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Ninja(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                static string? Header(HttpRequestHeaders headers, string name) => headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, Header(request.Headers, "X-API-TOKEN"), Header(request.Headers, "X-Requested-With"), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Ninja ninja, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new InvoiceNinjaConnector(), Contexts.Extract(new StubEgress(ninja), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api/v1" + route;

        private static string Data(string item) => "{\"data\":" + item + "}";

        private static string Page(int current, int pages, params string[] items) =>
            "{\"data\":[" + string.Join(",", items) + "],\"meta\":{\"pagination\":{\"current_page\":" + current + ",\"total_pages\":" + pages + ",\"links\":{\"next\":\"https://elsewhere.example/api/v1/clients?page=" + (current + 1) + "\"}}}}";

        private static string Json(string body) => JsonNode.Parse(body)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        private const string Statics = "{\"countries\":[{\"id\":\"752\",\"iso_3166_2\":\"SE\",\"iso_3166_3\":\"SWE\",\"name\":\"Sweden\"},{\"id\":\"578\",\"iso_3166_2\":\"NO\",\"iso_3166_3\":\"NOR\",\"name\":\"Norway\"}],"
            + "\"currencies\":[{\"id\":\"7\",\"code\":\"SEK\",\"name\":\"Swedish Krona\"}],\"payment_types\":[{\"id\":\"1\",\"name\":\"Bank Transfer\"},{\"id\":\"32\",\"name\":\"Swish\"}]}";

        [Fact]
        public void It_declares_itself_as_n8ns_invoice_ninja_node()
        {
            IConnector connector = new InvoiceNinjaConnector();
            Assert.Equal(("invoiceninja", "Invoice Ninja", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            var names = ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name).ToList();
            Assert.Equal(31, names.Count);
            Assert.Equal(new[] { "clients", "client", "client_create", "client_delete" }, names.Take(4));
            Assert.Equal(new[] { "invoice_email", "quote_email", "bank_transaction_match" }, names.TakeLast(3));
            Assert.DoesNotContain(names, name => name.EndsWith("_update", StringComparison.Ordinal)); // n8n's node changes nothing
        }

        [Fact]
        public async Task Every_parameter_is_held_to_invoice_ninjas_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var ninja = new Ninja(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ninja, operation, parameters, endpoint));
                Assert.Empty(ninja.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("Invoice Ninja has no operation 'client_update' - for each of client, invoice, quote, payment, expense, task, bank_transaction", await Refused("client_update", new()));
            Assert.Equal("Give the client's name, or a contact's name or e-mail (name).", await Refused("client_create", new() { ["city"] = "Lund" }));
            Assert.Equal("Give the client - The client's id. (client).", await Refused("invoice_create", new()));
            Assert.Equal("'Volej Rej' is not an id of Invoice Ninja's - letters and digits, VolejRejNm (client).", await Refused("invoice_create", new() { ["client"] = "Volej Rej" }));
            Assert.Equal("Give the invoice - its id, VolejRejNm (invoice).", await Refused("invoice", new()));
            Assert.Equal("'2026-10-21T08:00' is not a date - 2026-10-21 (date).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["date"] = "2026-10-21T08:00" }));
            Assert.Equal("'2026-02-30' is not a date - 2026-10-21 (due_date).", await Refused("quote_create", new() { ["client"] = "VolejRejNm", ["due_date"] = "2026-02-30" }));
            Assert.Equal("'150' is not a percentage - 0 to 100 (tax_rate1).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["tax_rate1"] = "150" }));
            Assert.Equal("'1,5' is not an amount - 99.50 (partial).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["partial"] = "1,5" }));
            Assert.Equal("'0' is not an amount above 0 - 99.50 (amount).", await Refused("payment_create", new() { ["invoice"] = "VolejRejNm", ["amount"] = "0" }));
            Assert.Equal("'-5' is not an amount above 0 - 99.50 (amount).", await Refused("expense_create", new() { ["amount"] = "-5" }));
            Assert.Equal("Give the amount - 360 - at most the invoice's balance. (amount).", await Refused("payment_create", new() { ["invoice"] = "VolejRejNm" }));
            Assert.Equal("Give the bank integration - The bank account's id - Invoice Ninja requires it. (bank_integration).", await Refused("bank_transaction_create", new() { ["amount"] = "1" })); // n8n has it optional
            Assert.Equal("'transfer' is not CREDIT or DEBIT (base_type).", await Refused("bank_transaction_create", new() { ["bank_integration"] = "VolejRejNm", ["amount"] = "1", ["base_type"] = "transfer" }));
            Assert.Equal("'kanske' is not true or false (mark_sent).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["mark_sent"] = "kanske" }));
            Assert.Equal("The items are a JSON list - [{\"notes\": \"Kaffe\", \"cost\": 120, \"quantity\": 3}] (items).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["query"] = "kaffe x 3" }));
            Assert.Equal("A line has no 'price' - product_key, notes, cost, quantity, tax_name1, tax_rate1, tax_name2, tax_rate2, discount (items).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["items"] = "[{\"price\": 1}]" }));
            Assert.Equal("A line's cost is a number (items).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["items"] = "[{\"cost\": \"mycket\"}]" }));
            Assert.Equal("Give 1 to 500 lines (items).", await Refused("invoice_create", new() { ["client"] = "VolejRejNm", ["items"] = "[]" }));
            Assert.Equal("A contact has no 'title' - first_name, last_name, email, phone (contacts).", await Refused("client_create", new() { ["query"] = "[{\"title\": \"Chef\"}]" }));
            Assert.Equal("Give the task's start with its end or hours (start).", await Refused("task_create", new() { ["hours"] = "2" }));
            Assert.Equal("Give the task's end or its hours, not both (hours).", await Refused("task_create", new() { ["start"] = "2026-10-21T08:00", ["end"] = "2026-10-21T10:00", ["hours"] = "2" }));
            Assert.Equal("'kl 8' is not a moment - 2026-10-21T08:00, or a day (start).", await Refused("task_create", new() { ["start"] = "kl 8" }));
            Assert.Equal("'open' is not active, archived or deleted, by commas (status).", await Refused("invoices", new() { ["status"] = "open" }));
            Assert.Equal("'late' is not all, paid, unpaid or overdue (client_status).", await Refused("invoices", new() { ["client_status"] = "late" }));
            Assert.Equal("'client;payments' is not names by commas - client, payments (include).", await Refused("invoice", new() { ["invoice"] = "VolejRejNm", ["include"] = "client;payments" }));
            Assert.Equal("A search is one line (search).", await Refused("clients", new() { ["search"] = "Kafé\nLund" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("clients", new() { ["max"] = "0" }));
            Assert.Equal("'igår' is not a moment - 2026-10-21T08:00, or a day (created_since).", await Refused("clients", new() { ["created_since"] = "igår" }));
            Assert.Equal("Give Invoice Ninja's address - https://invoicing.co, or your own.", await Refused("clients", new(), ""));
            Assert.Equal("'https://ninja.kafe.example/?x=1' is not Invoice Ninja's address - https://invoicing.co, with no query.", await Refused("clients", new(), "https://ninja.kafe.example/?x=1"));
            Assert.Equal("The Invoice Ninja connection has no API token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new InvoiceNinjaConnector(), Contexts.Extract(new StubEgress(new Ninja(_ => (HttpStatusCode.OK, "{}"))), Address, "clients", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task An_invoice_is_made_under_version_fives_names_marked_sent_and_part_paid()
        {
            var ninja = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"VolejRejNm\",\"number\":\"F-1001\",\"amount\":360,\"balance\":310}")));
            var made = Assert.Single(await Run(ninja, "invoice_create", new()
            {
                ["client"] = "Wpmbk5ezJn", ["date"] = "2026-10-01", ["due_date"] = "2026-10-31", ["number"] = "F-1001",
                ["items"] = "[{\"service\": \"kaffe\", \"description\": \"Bryggkaffe\", \"cost\": 120, \"hours\": 3}]", ["mark_sent"] = "true", ["paid"] = "50",
            }, Address + "/api/v1")).Fields;
            var sent = Assert.Single(ninja.Requests);
            Assert.Equal(("POST", Api("/invoices?mark_sent=true&amount_paid=50"), "kafe-token-1", "XMLHttpRequest"), (sent.Method, sent.Url, sent.Token, sent.Requested)); // the API's own address given: Invoice Ninja's
            Assert.Equal(
                Json("{\"client_id\":\"Wpmbk5ezJn\",\"date\":\"2026-10-01\",\"due_date\":\"2026-10-31\",\"number\":\"F-1001\",\"line_items\":[{\"product_key\":\"kaffe\",\"notes\":\"Bryggkaffe\",\"cost\":120,\"quantity\":3}]}"),
                Json(sent.Body!)); // date, not n8n's invoice_date, which Invoice Ninja ignores; n8n's line names as Invoice Ninja's
            Assert.Equal(("F-1001", 310L), ((string?)made["number"], (long?)made["balance"]));

            var quote = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"Opnel5aKBz\"}")));
            await Run(quote, "quote_create", new() { ["client"] = "Wpmbk5ezJn", ["date"] = "2026-10-02", ["public_notes"] = "Gäller i 30 dagar", ["discount"] = "10" });
            Assert.Equal((Api("/quotes"), Json("{\"client_id\":\"Wpmbk5ezJn\",\"date\":\"2026-10-02\",\"public_notes\":\"Gäller i 30 dagar\",\"discount\":10}")), (Assert.Single(quote.Requests).Url, Json(quote.Requests[0].Body!)));
        }

        [Fact]
        public async Task A_country_currency_and_payment_type_are_looked_up_in_invoice_ninjas_own_lists()
        {
            var ninja = new Ninja(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/statics", StringComparison.Ordinal) ? Statics : Data("{\"id\":\"VolejRejNm\"}")));
            await Run(ninja, "client_create", new() { ["name"] = "Kafé Lund", ["country"] = "se", ["shipping_country"] = "Norway", ["contact_first_name"] = "Ada", ["contact_email"] = "ada@kafe.example", ["work_phone"] = "040-628 04 03" });
            Assert.Equal(new[] { ("GET", Api("/statics")), ("POST", Api("/clients")) }, ninja.Requests.Select(sent => (sent.Method, sent.Url))); // the lists read once
            Assert.Equal(Json("{\"name\":\"Kafé Lund\",\"phone\":\"040-628 04 03\",\"country_id\":\"752\",\"shipping_country_id\":\"578\",\"contacts\":[{\"first_name\":\"Ada\",\"email\":\"ada@kafe.example\"}]}"), Json(ninja.Requests[1].Body!));

            var expense = new Ninja(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/statics", StringComparison.Ordinal) ? Statics : Data("{\"id\":\"VolejRejNm\"}")));
            await Run(expense, "expense_create", new() { ["amount"] = "99.50", ["date"] = "2026-10-03", ["currency"] = "SEK", ["payment_type"] = "swish", ["billable"] = "true" });
            Assert.Equal(Json("{\"amount\":99.50,\"date\":\"2026-10-03\",\"should_be_invoiced\":true,\"currency_id\":7,\"payment_type_id\":32}"), Json(expense.Requests[^1].Body!)); // date, not n8n's expense_date

            var unknown = new Ninja(_ => (HttpStatusCode.OK, Statics));
            Assert.Equal("Invoice Ninja has no payment type 'Kort' (type).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "payment_create", new() { ["invoice"] = "VolejRejNm", ["amount"] = "10", ["type"] = "Kort" }))).Message); // Invoice Ninja keeps one it has not got
            Assert.Equal("Invoice Ninja has no country 'Atlantis' (country).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Ninja(_ => (HttpStatusCode.OK, Statics)), "client_create", new() { ["name"] = "X", ["country"] = "Atlantis" }))).Message);
            Assert.DoesNotContain(unknown.Requests, sent => sent.Method == "POST");
        }

        [Fact]
        public async Task A_payment_is_held_to_its_invoices_balance_and_booked_on_its_client()
        {
            Ninja Invoice(decimal balance) => new(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/statics", StringComparison.Ordinal) ? Statics
                : sent.Method == "GET" ? Data("{\"id\":\"VolejRejNm\",\"number\":\"F-1001\",\"client_id\":\"Wpmbk5ezJn\",\"balance\":" + balance.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}") : Data("{\"id\":\"Opnel5aKBz\",\"amount\":360}")));
            var ninja = Invoice(360);
            await Run(ninja, "payment_create", new() { ["invoice"] = "VolejRejNm", ["amount"] = "360", ["type"] = "Bank Transfer", ["transaction_reference"] = "T1" });
            Assert.Equal(new[] { ("GET", Api("/invoices/VolejRejNm")), ("GET", Api("/statics")), ("POST", Api("/payments")) }.OrderBy(item => item.Item2), ninja.Requests.Select(sent => (sent.Method, sent.Url)).OrderBy(item => item.Item2));
            Assert.Equal(Json("{\"amount\":360,\"type_id\":1,\"transaction_reference\":\"T1\",\"client_id\":\"Wpmbk5ezJn\",\"invoices\":[{\"invoice_id\":\"VolejRejNm\",\"amount\":360}]}"), Json(ninja.Requests[^1].Body!));

            var paid = Invoice(0);
            Assert.Equal("The invoice F-1001 is paid - Invoice Ninja would book the payment and change nothing (invoice).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(paid, "payment_create", new() { ["invoice"] = "VolejRejNm", ["amount"] = "10" }))).Message);
            var short_ = Invoice(100);
            Assert.Equal("The invoice F-1001's balance is 100, less than 150 (amount).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(short_, "payment_create", new() { ["invoice"] = "VolejRejNm", ["amount"] = "150" }))).Message);
            Assert.DoesNotContain(paid.Requests.Concat(short_.Requests), sent => sent.Method == "POST");
        }

        [Fact]
        public async Task Lists_go_page_by_page_by_number_with_invoice_ninjas_own_filters()
        {
            var ninja = new Ninja(sent => (HttpStatusCode.OK, sent.Url.EndsWith("&page=1", StringComparison.Ordinal) ? Page(1, 3, "{\"id\":\"a\"}", "{\"id\":\"b\"}") : sent.Url.EndsWith("&page=2", StringComparison.Ordinal) ? Page(2, 3, "{\"id\":\"c\"}", "{\"id\":\"d\"}") : Page(3, 3, "{\"id\":\"e\"}")));
            var clients = await Run(ninja, "clients", new() { ["max"] = "3" });
            Assert.Equal(new[] { "a", "b", "c" }, clients.Select(item => (string?)item.Fields["id"]));
            Assert.Equal(new[] { Api("/clients?per_page=100&page=1"), Api("/clients?per_page=100&page=2") }, ninja.Requests.Select(sent => sent.Url)); // never Invoice Ninja's next address
            var all = new Ninja(sent => (HttpStatusCode.OK, sent.Url.EndsWith("&page=1", StringComparison.Ordinal) ? Page(1, 2, "{\"id\":\"a\"}") : Page(2, 2, "{\"id\":\"b\"}")));
            Assert.Equal(2, (await Run(all, "tasks", new())).Count);
            Assert.Equal(2, all.Requests.Count); // the last page is the last asked for

            var filtered = new Ninja(_ => (HttpStatusCode.OK, Page(1, 1)));
            await Run(filtered, "invoices", new() { ["search"] = "Kafé & Co", ["status"] = "Deleted, archived", ["created_since"] = "2026-10-01", ["number"] = "F 1001", ["client_status"] = "paid", ["include"] = "client, payments" });
            Assert.Equal(Api("/invoices?filter=Kaf%C3%A9%20%26%20Co&status=deleted,archived&created_at=1790812800&number=F%201001&client_status=paid&include=client,payments&per_page=100&page=1"), Assert.Single(filtered.Requests).Url); // n8n's "is deleted" filters nothing: the status does

            var one = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"VolejRejNm\",\"client\":{\"name\":\"Kafé Lund\"}}")));
            Assert.Equal("{\"name\":\"Kafé Lund\"}", (string?)Assert.Single(await Run(one, "invoice", new() { ["invoice"] = "VolejRejNm", ["include"] = "client" })).Fields["client"]);
            Assert.Equal(Api("/invoices/VolejRejNm?include=client"), one.Requests[0].Url);
        }

        [Fact]
        public async Task An_email_a_match_a_task_a_transaction_and_a_delete_go_as_invoice_ninja_takes_them()
        {
            var ninja = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"VolejRejNm\",\"status_id\":2}")));
            await Run(ninja, "invoice_email", new() { ["invoice"] = "VolejRejNm" });
            await Run(ninja, "quote_email", new() { ["quote"] = "Opnel5aKBz" });
            Assert.Equal(new[] { ("POST", Api("/emails"), "{\"entity\":\"invoice\",\"entity_id\":\"VolejRejNm\",\"template\":\"email_template_invoice\"}"), ("POST", Api("/emails"), "{\"entity\":\"quote\",\"entity_id\":\"Opnel5aKBz\",\"template\":\"email_template_quote\"}") },
                ninja.Requests.Select(sent => (sent.Method, sent.Url, sent.Body))); // n8n's GET /invoices/{id}/email answers 400

            var match = new Ninja(_ => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"VolejRejNm\",\"status_id\":3,\"payment_id\":\"Opnel5aKBz\"}]}"));
            Assert.Equal(3L, (long?)Assert.Single(await Run(match, "bank_transaction_match", new() { ["bank_transaction"] = "VolejRejNm", ["payment"] = "Opnel5aKBz" })).Fields["status_id"]);
            Assert.Equal((Api("/bank_transactions/match"), "{\"transactions\":[{\"id\":\"VolejRejNm\",\"payment_id\":\"Opnel5aKBz\"}]}"), (match.Requests[0].Url, match.Requests[0].Body));

            var task = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"VolejRejNm\"}")));
            await Run(task, "task_create", new() { ["query"] = "Rostning", ["start"] = "2026-10-21T08:00", ["hours"] = "1.5" });
            await Run(task, "task_create", new() { ["description"] = "Leverans", ["start"] = "2026-10-21T10:00+02:00", ["end"] = "2026-10-21T11:00+02:00" });
            await Run(task, "task_create", new() { ["description"] = "Pågår", ["start"] = "2026-10-21T12:00" });
            Assert.Equal(new[] { "{\"description\":\"Rostning\",\"time_log\":\"[[1792569600,1792575000]]\"}", "{\"description\":\"Leverans\",\"time_log\":\"[[1792569600,1792573200]]\"}", "{\"description\":\"Pågår\",\"time_log\":\"[[1792584000,0]]\"}" }, task.Requests.Select(sent => Json(sent.Body!)));
            Assert.Equal("The task's end is before its start (end).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(task, "task_create", new() { ["start"] = "2026-10-21T10:00", ["end"] = "2026-10-21T09:00" }))).Message);

            var transaction = new Ninja(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/statics", StringComparison.Ordinal) ? Statics : Data("{\"id\":\"VolejRejNm\"}")));
            await Run(transaction, "bank_transaction_create", new() { ["bank_integration"] = "VolejRejNm", ["amount"] = "360", ["date"] = "2026-10-04", ["currency"] = "7", ["description"] = "Insättning" });
            Assert.Equal(Json("{\"bank_integration_id\":\"VolejRejNm\",\"amount\":360,\"date\":\"2026-10-04\",\"currency_id\":7,\"description\":\"Insättning\",\"base_type\":\"CREDIT\"}"), Json(transaction.Requests[^1].Body!));
            await Run(transaction, "bank_transaction_create", new() { ["bank_integration"] = "VolejRejNm", ["amount"] = "20", ["base_type"] = "withdrawal" });
            Assert.Contains("\"base_type\":\"DEBIT\"", transaction.Requests[^1].Body!, StringComparison.Ordinal);

            var deleted = new Ninja(_ => (HttpStatusCode.OK, Data("{\"id\":\"VolejRejNm\",\"is_deleted\":true}")));
            Assert.Equal(true, (bool?)Assert.Single(await Run(deleted, "client_delete", new() { ["client"] = "VolejRejNm" })).Fields["is_deleted"]); // Invoice Ninja marks it deleted
            Assert.Equal(("DELETE", Api("/clients/VolejRejNm")), (deleted.Requests[0].Method, deleted.Requests[0].Url));
            Assert.Single(deleted.Requests); // its answer says it: nothing read again
            var payment = new Ninja(sent => (HttpStatusCode.OK, Data(sent.Method == "DELETE" ? "{\"id\":\"Opnel5aKBz\",\"is_deleted\":false,\"status_id\":4}" : "{\"id\":\"Opnel5aKBz\",\"is_deleted\":true,\"status_id\":2}")));
            Assert.Equal(true, (bool?)Assert.Single(await Run(payment, "payment_delete", new() { ["payment"] = "Opnel5aKBz" })).Fields["is_deleted"]); // a payment's delete answers with it as it was
            Assert.Equal(new[] { ("DELETE", Api("/payments/Opnel5aKBz")), ("GET", Api("/payments/Opnel5aKBz")) }, payment.Requests.Select(sent => (sent.Method, sent.Url)));
        }

        [Fact]
        public async Task Invoice_ninjas_messages_are_said_and_an_answer_that_is_not_its_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "client", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Ninja(_ => (status, body)), operation, parameters ?? new() { ["client"] = "VolejRejNm" }))).Message;

            Assert.Equal("Invoice Ninja answered HTTP 400: it has no such client.", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"No query results for model [App\\\\Models\\\\Client].\"}"));
            Assert.Equal("Invoice Ninja answered HTTP 400: it has no such bank transaction.", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"No query results for model [App\\\\Models\\\\BankTransaction] 9.\"}", "bank_transaction", new() { ["bank_transaction"] = "VolejRejNm" }));
            Assert.Equal("Invoice Ninja answered HTTP 422: client_id: The client id field is required.; amount: The amount must be a number.", await Failed((HttpStatusCode)422, "{\"message\":\"The given data was invalid.\",\"errors\":{\"client_id\":[\"The client id field is required.\"],\"amount\":[\"The amount must be a number.\"]}}"));
            Assert.Equal("Invoice Ninja answered HTTP 400: The requested action email is not available.", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"The requested action email is not available.\"}"));
            Assert.Equal("Invoice Ninja answered HTTP 403: Invalid token. - check the API token.", await Failed(HttpStatusCode.Forbidden, "{\"message\":\"Invalid token\"}"));
            Assert.Equal("Invoice Ninja answered HTTP 403: This action is unauthorized.", await Failed(HttpStatusCode.Forbidden, "{\"message\":\"This action is unauthorized.\"}"));
            Assert.Equal("Invoice Ninja answered HTTP 502.", await Failed(HttpStatusCode.BadGateway, ""));
            Assert.Equal("Invoice Ninja answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Invoice Ninja</html>"));
            Assert.Equal("Invoice Ninja answered without the item - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"id\":\"VolejRejNm\"}"));
            Assert.Equal("Invoice Ninja answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"data\":{\"id\":\"VolejRejNm\"}}", "clients", new()));
        }

        [Fact]
        public async Task A_test_names_the_company_the_token_reaches()
        {
            var connector = new InvoiceNinjaConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Ninja(_ => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"VolejRejNm\",\"settings\":{\"name\":\"Kafé Lund AB\"}}]}"))), Address, null, Credentials));
            Assert.Equal((true, "Invoice Ninja answers: the token reaches the company Kafé Lund AB."), (ok.Success, ok.Message));
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Ninja(_ => (HttpStatusCode.Forbidden, "{\"message\":\"Invalid token\"}"))), Address, null, Credentials));
            Assert.Equal((false, "Invoice Ninja answered HTTP 403: Invalid token. - check the API token."), (refused.Success, refused.Message));
        }
    }

    /// <summary>
    /// BL-229 - Invoice Ninja LIVE, against this machine's throwaway Invoice Ninja 5.13 (scripts/invoiceninja-test-server.ps1) -
    /// fail-not-skip without it (D46): a test naming the company and a wrong token refused; a client made with its country by
    /// code and a contact; an invoice of two lines dated as given and marked sent, read with its client, found by its number; a
    /// payment over its balance refused, the balance paid by type name and the invoice then paid; a quote and the invoice
    /// e-mailed (Invoice Ninja's mail goes to its log); an expense in SEK by Swish, dated as given; a task of an hour and a half;
    /// a bank transaction matched to the payment; each deleted again and found among the deleted.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class InvoiceNinjaConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Token => new() { ["api-key"] = InvoiceNinjaTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { new Uri(InvoiceNinjaTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new InvoiceNinjaConnector(), Contexts.Extract(Egress, InvoiceNinjaTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Token, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        // A bank account to book transactions on: n8n's node makes none, so the test makes one over Invoice Ninja's API.
        private static async Task<string> BankAccountAsync()
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, InvoiceNinjaTestEndpoint.Endpoint + "/api/v1/bank_integrations") { Content = new StringContent("{\"bank_account_name\":\"Kassa\",\"provider_name\":\"Kassa\"}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-API-TOKEN", InvoiceNinjaTestEndpoint.Token);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await client.SendAsync(request);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!["id"]!.GetValue<string>();
        }

        [Fact]
        public async Task A_test_names_the_company_and_a_wrong_token_is_refused()
        {
            var tested = await new InvoiceNinjaConnector().TestConnectionAsync(Contexts.Test(Egress, InvoiceNinjaTestEndpoint.Endpoint, credentials: Token, timeoutSeconds: 60));
            Assert.True(tested.Success, tested.Message);
            Assert.StartsWith("Invoice Ninja answers: the token reaches the company ", tested.Message, StringComparison.Ordinal);
            var wrong = await new InvoiceNinjaConnector().TestConnectionAsync(Contexts.Test(Egress, InvoiceNinjaTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = "not-a-token" }, timeoutSeconds: 60));
            Assert.Equal((false, "Invoice Ninja answered HTTP 403: Invalid token. - check the API token."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_client_is_invoiced_paid_matched_and_everything_of_it_deleted()
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var client = await One("client_create", new() { ["name"] = "Kafé Lund " + marker, ["country"] = "SE", ["contact_first_name"] = "Ada", ["contact_email"] = $"ada.{marker}@kafe.example" });
            var clientId = (string)client["id"]!;
            output.WriteLine($"client {clientId}");
            var made = new List<(string Operation, string Key, string Id)>();
            try
            {
                Assert.Equal("752", (string?)client["country_id"]);
                Assert.Contains($"ada.{marker}@kafe.example", (string?)client["contacts"], StringComparison.Ordinal);

                var invoice = await One("invoice_create", new() { ["client"] = clientId, ["date"] = "2026-10-01", ["number"] = "F-" + marker, ["mark_sent"] = "true", ["items"] = "[{\"product_key\": \"kaffe\", \"notes\": \"Bryggkaffe\", \"cost\": 120, \"quantity\": 2}, {\"notes\": \"Bulle\", \"cost\": 30, \"quantity\": 4}]" });
                var invoiceId = (string)invoice["id"]!;
                made.Add(("invoice_delete", "invoice", invoiceId));
                Assert.Equal(("2026-10-01", 360L, 2L), ((string?)invoice["date"], Convert.ToInt64(invoice["amount"], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(invoice["status_id"], System.Globalization.CultureInfo.InvariantCulture))); // dated as given, not today
                Assert.Contains("Kafé Lund " + marker, (string?)(await One("invoice", new() { ["invoice"] = invoiceId, ["include"] = "client" }))["client"], StringComparison.Ordinal);
                Assert.Equal(invoiceId, (string?)Assert.Single(await All("invoices", new() { ["number"] = "F-" + marker })).Fields["id"]);

                Assert.Equal($"The invoice F-{marker}'s balance is 360, less than 400 (amount).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("payment_create", new() { ["invoice"] = invoiceId, ["amount"] = "400" }))).Message);
                var payment = await One("payment_create", new() { ["invoice"] = invoiceId, ["amount"] = "360", ["type"] = "Bank Transfer", ["transaction_reference"] = "T-" + marker });
                made.Add(("payment_delete", "payment", (string)payment["id"]!));
                Assert.Equal("1", Convert.ToString(payment["type_id"], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(0L, Convert.ToInt64((await One("invoice", new() { ["invoice"] = invoiceId }))["balance"], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Contains(await All("invoices", new() { ["client_status"] = "paid", ["max"] = "500" }), item => (string?)item.Fields["id"] == invoiceId);
                Assert.StartsWith($"The invoice F-{marker} is paid", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("payment_create", new() { ["invoice"] = invoiceId, ["amount"] = "10" }))).Message); // Invoice Ninja would have booked it

                var quote = await One("quote_create", new() { ["client"] = clientId, ["date"] = "2026-10-02", ["items"] = "[{\"notes\": \"Catering\", \"cost\": 900, \"quantity\": 1}]" });
                made.Add(("quote_delete", "quote", (string)quote["id"]!));
                Assert.Equal("2026-10-02", (string?)quote["date"]);
                Assert.Equal((string)quote["id"]!, (string?)(await One("quote_email", new() { ["quote"] = (string)quote["id"]! }))["id"]);
                Assert.Equal(invoiceId, (string?)(await One("invoice_email", new() { ["invoice"] = invoiceId }))["id"]);

                var expense = await One("expense_create", new() { ["amount"] = "99.50", ["date"] = "2026-10-03", ["client"] = clientId, ["currency"] = "SEK", ["payment_type"] = "Swish", ["public_notes"] = "Mjölk " + marker });
                made.Add(("expense_delete", "expense", (string)expense["id"]!));
                Assert.Equal(("2026-10-03", "7"), ((string?)expense["date"], Convert.ToString(expense["currency_id"], System.Globalization.CultureInfo.InvariantCulture)));

                var task = await One("task_create", new() { ["client"] = clientId, ["description"] = "Rostning " + marker, ["start"] = "2026-10-21T08:00", ["hours"] = "1.5" });
                made.Add(("task_delete", "task", (string)task["id"]!));
                Assert.StartsWith("[[1792569600,1792575000", (string?)task["time_log"], StringComparison.Ordinal);

                var transaction = await One("bank_transaction_create", new() { ["bank_integration"] = await BankAccountAsync(), ["amount"] = "360", ["date"] = "2026-10-04", ["description"] = "Insättning " + marker });
                made.Add(("bank_transaction_delete", "bank_transaction", (string)transaction["id"]!));
                var matched = Assert.Single(await All("bank_transaction_match", new() { ["bank_transaction"] = (string)transaction["id"]!, ["payment"] = (string)payment["id"]! })).Fields;
                Assert.Equal(((string)payment["id"]!, 3L), ((string?)matched["payment_id"], Convert.ToInt64(matched["status_id"], System.Globalization.CultureInfo.InvariantCulture)));
            }
            finally
            {
                foreach (var (operation, key, id) in Enumerable.Reverse(made))
                {
                    Assert.Equal(true, (bool?)(await One(operation, new() { [key] = id }))["is_deleted"]);
                }

                Assert.Equal(true, (bool?)(await One("client_delete", new() { ["client"] = clientId }))["is_deleted"]);
            }

            Assert.Contains(await All("clients", new() { ["status"] = "deleted", ["search"] = marker }), item => (string?)item.Fields["id"] == clientId);
            Assert.Equal("Invoice Ninja answered HTTP 400: it has no such client.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("client", new() { ["client"] = "zzzzzzzzzz" }))).Message);
        }
    }
}
