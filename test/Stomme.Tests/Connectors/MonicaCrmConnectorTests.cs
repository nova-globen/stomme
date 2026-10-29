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
using Stomme.Modules.Connectors.MonicaCrm;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Monica (n8n's Monica CRM node) without a server: every id, date, choice and text held to Monica's shape before
    /// anything is sent - 31 February too; the token a bearer and in no address; a contact made with its gender by name and its
    /// birthdate as Monica's parts, Monica's required flags given; a change read and sent back whole - a note's contact, a
    /// contact's birthdate kept; a contact's fields at Monica's own route, not n8n's; a list page by page as Monica's meta says,
    /// up to max; a message added under its conversation's contact and changed whole; tags added by name and removed by name or
    /// number; a tag under a name Monica has refused; Monica's messages; a test naming the user.
    /// </summary>
    public sealed class MonicaCrmConnectorTests
    {
        private const string Address = "https://monica.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "eyJ0eXAiOiJKV1QifQ.kafe" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Monica : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Monica(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Monica monica, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new MonicaCrmConnector(), Contexts.Extract(new StubEgress(monica), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api" + route;

        private static string Data(string item) => "{\"data\":" + item + "}";

        private static string Page(int current, int last, params string[] items) => "{\"data\":[" + string.Join(",", items) + "],\"meta\":{\"current_page\":" + current + ",\"last_page\":" + last + "}}";

        private static string Json(string body) => JsonNode.Parse(body)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        private const string Ada = "{\"id\":1,\"first_name\":\"Ada\",\"last_name\":\"Lund\",\"nickname\":null,\"description\":null,\"is_partial\":false,\"is_dead\":false,\"gender\":\"Woman\",\"information\":{\"dates\":{\"birthdate\":{\"is_age_based\":false,\"is_year_unknown\":false,\"date\":\"1990-03-14T00:00:00Z\"},\"deceased_date\":{\"is_age_based\":null,\"is_year_unknown\":null,\"date\":null}}}}";

        [Fact]
        public void It_declares_itself_as_n8ns_monica_crm_node()
        {
            IConnector connector = new MonicaCrmConnector();
            Assert.Equal(("monicacrm", "Monica CRM", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            var names = ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name).ToList();
            Assert.Equal(53, names.Count);
            Assert.Equal(new[] { "contacts", "contact", "contact_create", "contact_update", "contact_delete" }, names.Take(5));
            Assert.Contains("contact_fields", names);
            Assert.DoesNotContain("conversations", names); // n8n's node lists none
            Assert.Equal(new[] { "message_create", "message_update", "contact_tags_add", "contact_tags_remove" }, names.TakeLast(4));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_monicas_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var monica = new Monica(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(monica, operation, parameters, endpoint));
                Assert.Empty(monica.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("Monica has no operation 'conversations' - for each of contact, activity, call, contact_field, conversation, journal_entry, note, reminder, tag, task", await Refused("conversations", new()));
            Assert.Equal("Give the first name - Ada. (first_name).", await Refused("contact_create", new()));
            Assert.Equal("'1990-02-31' is not a date - 2026-10-21 (birthdate).", await Refused("contact_create", new() { ["first_name"] = "Ada", ["birthdate"] = "1990-02-31" })); // Monica would store 3 March
            Assert.Equal("A first name is one line of at most 250 characters (first_name).", await Refused("contact_create", new() { ["first_name"] = "Ada\nLund" }));
            Assert.Equal("'kanske' is not true or false (is_partial).", await Refused("contact_create", new() { ["first_name"] = "Ada", ["is_partial"] = "kanske" }));
            Assert.Equal("Give the contact - its number, 42 (contact).", await Refused("note_create", new() { ["body"] = "x" }));
            Assert.Equal("'14 March 1990' is not a date - 2026-10-21 (birthdate).", await Refused("contact_create", new() { ["first_name"] = "Ada", ["birthdate"] = "14 March 1990" }));
            Assert.Equal("'ada' is not a contact's number - 42 (contact).", await Refused("note_create", new() { ["body"] = "x", ["contact"] = "ada" }));
            Assert.Equal("'0' is not a note's number - 42 (note).", await Refused("note", new() { ["note"] = "0" }));
            Assert.Equal("'daily' is not one_time, week, month or year (frequency_type).", await Refused("reminder_create", new() { ["contact"] = "1", ["title"] = "x", ["initial_date"] = "2026-11-01", ["frequency_type"] = "daily" }));
            Assert.Equal("'1, Ada' is not numbers by commas - 1, 4 (contacts).", await Refused("activity_create", new() { ["activity_type"] = "1", ["summary"] = "Fika", ["happened_at"] = "2026-10-20", ["contacts"] = "1, Ada" }));
            Assert.Equal("Give something to change.", await Refused("note_update", new() { ["note"] = "1" }));
            Assert.Equal("Give the tags - by commas, stamkund, kaffe (tags).", await Refused("contact_tags_add", new() { ["contact"] = "1" }));
            Assert.Equal("'kontakter' is not created_at, -created_at, updated_at or -updated_at (sort).", await Refused("contacts", new() { ["sort"] = "kontakter" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("tags", new() { ["max"] = "0" }));
            Assert.Equal("Give the contact - its number, 42 (contact).", await Refused("contact_fields", new()));
            Assert.Equal("Give Monica's address - https://monica.kafe.example, or https://app.monicahq.com.", await Refused("tags", new(), ""));
            Assert.Equal("'https://monica.kafe.example/?x=1' is not Monica's address - https://monica.kafe.example, with no query.", await Refused("tags", new(), "https://monica.kafe.example/?x=1"));
            Assert.Equal("The Monica connection has no API token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MonicaCrmConnector(), Contexts.Extract(new StubEgress(new Monica(_ => (HttpStatusCode.OK, "{}"))), Address, "tags", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_contact_is_made_with_its_gender_by_name_and_its_birthdate_as_monicas_parts()
        {
            var monica = new Monica(sent => (sent.Method == "GET" ? HttpStatusCode.OK : HttpStatusCode.Created, sent.Method == "GET" ? Page(1, 1, "{\"id\":1,\"name\":\"Man\"}", "{\"id\":2,\"name\":\"Woman\"}") : Data(Ada)));
            var made = Assert.Single(await Run(monica, "contact_create", new() { ["first_name"] = "Ada", ["last_name"] = "Lund", ["gender"] = "woman", ["birthdate"] = "1990-03-14" }, Address + "/api")).Fields;
            Assert.Equal(new[] { ("GET", Api("/genders?limit=100&page=1")), ("POST", Api("/contacts")) }, monica.Requests.Select(sent => (sent.Method, sent.Url))); // the API's own address given: Monica's
            Assert.All(monica.Requests, sent => Assert.Equal("Bearer eyJ0eXAiOiJKV1QifQ.kafe", sent.Authorization));
            Assert.Equal(
                Json("{\"is_birthdate_known\":true,\"is_deceased\":false,\"is_deceased_date_known\":false,\"first_name\":\"Ada\",\"last_name\":\"Lund\",\"gender_id\":2,\"birthdate_day\":14,\"birthdate_month\":3,\"birthdate_year\":1990,\"birthdate_is_age_based\":false}"),
                Json(monica.Requests[1].Body!)); // Monica's required flags given; the gender looked up
            Assert.Equal((1L, "Ada"), ((long?)made["id"], (string?)made["first_name"]));

            var unknown = new Monica(_ => (HttpStatusCode.OK, Page(1, 1, "{\"id\":1,\"name\":\"Man\"}")));
            Assert.Equal("Monica has no gender 'Kvinna' - it has Man (gender).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "contact_create", new() { ["first_name"] = "Ada", ["gender"] = "Kvinna" }))).Message);
            Assert.DoesNotContain(unknown.Requests, sent => sent.Method == "POST");
            var byNumber = new Monica(_ => (HttpStatusCode.Created, Data(Ada)));
            await Run(byNumber, "contact_create", new() { ["first_name"] = "Ada", ["gender"] = "2", ["deceased_date"] = "2026-01-02" });
            Assert.Equal(Json("{\"is_birthdate_known\":false,\"is_deceased\":true,\"is_deceased_date_known\":true,\"first_name\":\"Ada\",\"gender_id\":2,\"deceased_date_day\":2,\"deceased_date_month\":1,\"deceased_date_year\":2026,\"deceased_date_is_age_based\":false}"), Json(Assert.Single(byNumber.Requests).Body!));
        }

        [Fact]
        public async Task A_change_is_read_and_sent_back_whole()
        {
            var note = "{\"id\":7,\"body\":\"Gillar kardemumma\",\"is_favorited\":false,\"contact\":{\"id\":1,\"first_name\":\"Ada\"}}";
            var monica = new Monica(sent => (HttpStatusCode.OK, Data(note)));
            await Run(monica, "note_update", new() { ["note"] = "7", ["query"] = "Gillar kanel\noch kardemumma" });
            Assert.Equal(new[] { ("GET", Api("/notes/7")), ("PUT", Api("/notes/7")) }, monica.Requests.Select(sent => (sent.Method, sent.Url)));
            Assert.Equal(Json("{\"body\":\"Gillar kanel\\noch kardemumma\",\"is_favorited\":false,\"contact_id\":1}"), Json(monica.Requests[1].Body!)); // its contact sent back, as Monica requires

            var contact = new Monica(sent => (HttpStatusCode.OK, Data(Ada)));
            await Run(contact, "contact_update", new() { ["contact"] = "1", ["nickname"] = "Adis" });
            Assert.Equal(
                Json("{\"first_name\":\"Ada\",\"last_name\":\"Lund\",\"nickname\":\"Adis\",\"description\":null,\"is_partial\":false,\"is_deceased\":false,\"is_birthdate_known\":true,\"birthdate_day\":14,\"birthdate_month\":3,\"birthdate_year\":1990,\"birthdate_is_age_based\":false,\"is_deceased_date_known\":false}"),
                Json(contact.Requests[1].Body!)); // the birthdate kept

            var activity = new Monica(_ => (HttpStatusCode.OK, Data("{\"id\":3,\"summary\":\"Fika\",\"description\":null,\"happened_at\":\"2026-10-20\",\"activity_type\":{\"id\":1},\"attendees\":{\"total\":2,\"contacts\":[{\"id\":1},{\"id\":4}]}}")));
            await Run(activity, "activity_update", new() { ["activity"] = "3", ["summary"] = "Fika igen" });
            Assert.Equal(Json("{\"summary\":\"Fika igen\",\"description\":null,\"happened_at\":\"2026-10-20\",\"activity_type_id\":1,\"contacts\":[1,4]}"), Json(activity.Requests[1].Body!));
            var reminder = new Monica(_ => (HttpStatusCode.OK, Data("{\"id\":2,\"title\":\"Ringa\",\"description\":null,\"frequency_type\":\"week\",\"frequency_number\":2,\"initial_date\":\"2026-11-01T00:00:00Z\",\"contact\":{\"id\":1}}")));
            await Run(reminder, "reminder_update", new() { ["reminder"] = "2", ["frequency_type"] = "month" });
            Assert.Equal(Json("{\"title\":\"Ringa\",\"description\":null,\"frequency_type\":\"month\",\"frequency_number\":2,\"initial_date\":\"2026-11-01\",\"contact_id\":1}"), Json(reminder.Requests[1].Body!));
        }

        [Fact]
        public async Task Lists_go_page_by_page_and_a_contacts_fields_at_monicas_own_route()
        {
            var monica = new Monica(sent => (HttpStatusCode.OK, sent.Url.Contains("page=1", StringComparison.Ordinal) ? Page(1, 3, "{\"id\":1}", "{\"id\":2}") : sent.Url.Contains("page=2", StringComparison.Ordinal) ? Page(2, 3, "{\"id\":3}", "{\"id\":4}") : Page(3, 3, "{\"id\":5}")));
            var contacts = await Run(monica, "contacts", new() { ["query"] = "Lund", ["sort"] = "-created_at", ["max"] = "3" });
            Assert.Equal(new long?[] { 1, 2, 3 }, contacts.Select(item => (long?)item.Fields["id"]));
            Assert.Equal(new[] { Api("/contacts?query=Lund&sort=-created_at&limit=100&page=1"), Api("/contacts?query=Lund&sort=-created_at&limit=100&page=2") }, monica.Requests.Select(sent => sent.Url));
            var all = new Monica(sent => (HttpStatusCode.OK, sent.Url.Contains("page=1", StringComparison.Ordinal) ? Page(1, 2, "{\"id\":1}") : Page(2, 2, "{\"id\":2}")));
            Assert.Equal(2, (await Run(all, "journal_entries", new())).Count);
            Assert.Equal(2, all.Requests.Count); // the last page is the last asked for

            var fields = new Monica(_ => (HttpStatusCode.OK, Page(1, 1, "{\"id\":1,\"content\":\"ada@kafe.example\"}")));
            Assert.Equal("ada@kafe.example", (string?)Assert.Single(await Run(fields, "contact_fields", new() { ["contact"] = "1" })).Fields["content"]);
            Assert.Equal(Api("/contacts/1/contactfields?limit=100&page=1"), Assert.Single(fields.Requests).Url); // n8n's /contact/1/contactfields is no route of Monica's
        }

        [Fact]
        public async Task Messages_tags_and_deletes_go_as_monica_takes_them()
        {
            var conversation = "{\"id\":4,\"happened_at\":\"2026-10-21T00:00:00Z\",\"contact\":{\"id\":1},\"messages\":[{\"id\":9,\"content\":\"Hej Ada\",\"written_at\":\"2026-10-21T00:00:00Z\",\"written_by_me\":true}]}";
            var monica = new Monica(_ => (HttpStatusCode.OK, Data(conversation)));
            await Run(monica, "message_create", new() { ["conversation"] = "4", ["content"] = "Hej igen", ["written_at"] = "2026-10-22", ["written_by_me"] = "false" });
            Assert.Equal(("POST", Api("/conversations/4/messages")), (monica.Requests[1].Method, monica.Requests[1].Url));
            Assert.Equal(Json("{\"contact_id\":1,\"content\":\"Hej igen\",\"written_at\":\"2026-10-22\",\"written_by_me\":false}"), Json(monica.Requests[1].Body!)); // the conversation's contact
            var changed = new Monica(_ => (HttpStatusCode.OK, Data(conversation)));
            await Run(changed, "message_update", new() { ["conversation"] = "4", ["message"] = "9", ["content"] = "Hej, Ada" });
            Assert.Equal(("PUT", Api("/conversations/4/messages/9"), Json("{\"contact_id\":1,\"content\":\"Hej, Ada\",\"written_at\":\"2026-10-21\",\"written_by_me\":true}")), (changed.Requests[1].Method, changed.Requests[1].Url, Json(changed.Requests[1].Body!)));
            Assert.Equal("The conversation has no message 99 (message).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Monica(_ => (HttpStatusCode.OK, Data(conversation))), "message_update", new() { ["conversation"] = "4", ["message"] = "99", ["content"] = "x" }))).Message);

            var tags = new Monica(sent => (HttpStatusCode.OK, sent.Url.Contains("/tags", StringComparison.Ordinal) ? Page(1, 1, "{\"id\":2,\"name\":\"stamkund\"}", "{\"id\":3,\"name\":\"kaffe\"}") : Data(Ada)));
            await Run(tags, "contact_tags_add", new() { ["contact"] = "1", ["tags"] = "stamkund, kaffe" });
            Assert.Equal(("POST", Api("/contacts/1/setTags"), "{\"tags\":[\"stamkund\",\"kaffe\"]}"), (tags.Requests[0].Method, tags.Requests[0].Url, tags.Requests[0].Body));
            await Run(tags, "contact_tags_remove", new() { ["contact"] = "1", ["tags"] = "Kaffe, 7" });
            Assert.Equal(("POST", Api("/contacts/1/unsetTag"), "{\"tags\":[3,7]}"), (tags.Requests[^1].Method, tags.Requests[^1].Url, tags.Requests[^1].Body)); // a name looked up, a number as it is
            Assert.Equal("Monica has no tag 'te' (tags).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tags, "contact_tags_remove", new() { ["contact"] = "1", ["tags"] = "te" }))).Message);

            var taken = new Monica(_ => (HttpStatusCode.OK, Page(1, 1, "{\"id\":2,\"name\":\"Stamkund\"}")));
            Assert.Equal("Monica already has a tag 'Stamkund' (2); it would make a second one (name).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(taken, "tag_create", new() { ["name"] = "stamkund" }))).Message);
            Assert.DoesNotContain(taken.Requests, sent => sent.Method == "POST");

            var deleted = new Monica(_ => (HttpStatusCode.OK, "{\"deleted\":true,\"id\":5}"));
            Assert.Equal((5L, true, "call_delete"), ((long?)Assert.Single(await Run(deleted, "call_delete", new() { ["call"] = "5" })).Fields["id"], (bool?)(await Run(deleted, "call_delete", new() { ["call"] = "5" }))[0].Fields["deleted"], (string?)(await Run(deleted, "call_delete", new() { ["call"] = "5" }))[0].Fields["done"]));
            Assert.Equal(("DELETE", Api("/calls/5")), (deleted.Requests[0].Method, deleted.Requests[0].Url));
            var kept = new Monica(_ => (HttpStatusCode.OK, "{\"deleted\":false,\"id\":5}"));
            Assert.Equal(false, (bool?)Assert.Single(await Run(kept, "call_delete", new() { ["call"] = "5" })).Fields["deleted"]); // Monica's answer, not assumed
            var task = new Monica(_ => (HttpStatusCode.Created, Data("{\"id\":8,\"title\":\"Ring Ada\"}")));
            await Run(task, "task_create", new() { ["contact"] = "1", ["title"] = "Ring Ada" });
            Assert.Equal(Json("{\"completed\":false,\"contact_id\":1,\"title\":\"Ring Ada\"}"), Json(Assert.Single(task.Requests).Body!)); // Monica requires completed
            var search = new Monica(_ => (HttpStatusCode.OK, Page(1, 1)));
            await Run(search, "contacts", new() { ["query"] = "Ada & Co" });
            Assert.Equal(Api("/contacts?query=Ada%20%26%20Co&limit=100&page=1"), Assert.Single(search.Requests).Url); // a search cannot add to the query
            var field = new Monica(_ => (HttpStatusCode.OK, Data("{\"id\":1}")));
            await Run(field, "contact_field", new() { ["contact_field"] = "1" });
            Assert.Equal(Api("/contactfields/1"), field.Requests[0].Url);
        }

        [Fact]
        public async Task Monicas_messages_are_said_and_an_answer_that_is_not_monicas_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "note", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Monica(_ => (status, body)), operation, parameters ?? new() { ["note"] = "1" }))).Message;

            Assert.Equal("Monica answered HTTP 404: The resource has not been found.", await Failed(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"The resource has not been found\",\"error_code\":31}}"));
            Assert.Equal("Monica answered HTTP 422: The selected activity type id is invalid. The contacts field is required.", await Failed((HttpStatusCode)422, "{\"error\":{\"message\":[\"The selected activity type id is invalid.\",\"The contacts field is required.\"],\"error_code\":32}}"));
            Assert.Equal("Monica answered HTTP 401: Unauthenticated. - check the API token.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}"));
            Assert.Equal("Monica answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Monica</html>"));
            Assert.Equal("Monica answered without the item - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"id\":1}"));
            Assert.Equal("Monica answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"data\":{\"id\":1}}", "tags", new()));
        }

        [Fact]
        public async Task A_test_names_the_token_user()
        {
            var connector = new MonicaCrmConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Monica(_ => (HttpStatusCode.OK, Data("{\"id\":1,\"name\":\"Kafé Kassa\",\"email\":\"kafe@kafe.example\"}")))), Address, null, Credentials));
            Assert.Equal((true, "Monica answers: the token is Kafé Kassa's."), (ok.Success, ok.Message));
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Monica(_ => (HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}"))), Address, null, Credentials));
            Assert.Equal((false, "Monica answered HTTP 401: Unauthenticated. - check the API token."), (refused.Success, refused.Message));
        }
    }

    /// <summary>
    /// Monica LIVE, against this machine's throwaway Monica 4.1 (scripts/monica-test-server.ps1) - fail-not-skip without it
    /// It covers a test naming the token's user and a wrong token refused; a contact made with its gender by name and its birthdate,
    /// changed with the birthdate kept, its field read at Monica's own route, a note made and changed whole, tags added and
    /// removed by name, a tag Monica has refused, a task done, a reminder, an activity, a call, a conversation and its message, a
    /// journal entry - each deleted again, the contact last.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MonicaCrmConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Token => new() { ["api-key"] = MonicaCrmTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { new Uri(MonicaCrmTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new MonicaCrmConnector(), Contexts.Extract(Egress, MonicaCrmTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Token, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        [Fact]
        public async Task A_test_names_the_user_and_a_wrong_token_is_refused()
        {
            var tested = await new MonicaCrmConnector().TestConnectionAsync(Contexts.Test(Egress, MonicaCrmTestEndpoint.Endpoint, credentials: Token, timeoutSeconds: 60));
            Assert.Equal((true, "Monica answers: the token is Kafé Kassa's."), (tested.Success, tested.Message));
            var wrong = await new MonicaCrmConnector().TestConnectionAsync(Contexts.Test(Egress, MonicaCrmTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = "not-a-token" }, timeoutSeconds: 60));
            Assert.Equal((false, "Monica answered HTTP 401: Unauthenticated. - check the API token."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_contact_and_everything_of_it_is_made_changed_and_deleted()
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var contact = await One("contact_create", new() { ["first_name"] = "Ada", ["last_name"] = "Lund " + marker, ["gender"] = "Woman", ["birthdate"] = "1990-03-14" });
            var id = contact["id"]!.ToString()!;
            output.WriteLine($"contact {id}");
            try
            {
                Assert.Equal("Woman", (string?)contact["gender"]);
                var changed = await One("contact_update", new() { ["contact"] = id, ["nickname"] = "Adis" });
                Assert.Equal("Adis", (string?)changed["nickname"]);
                Assert.Contains("1990-03-14", (string?)changed["information"], StringComparison.Ordinal); // the birthdate kept through a change of the nickname
                Assert.Single(await All("contacts", new() { ["query"] = marker }));

                await One("contact_field_create", new() { ["contact"] = id, ["contact_field_type"] = "1", ["data"] = $"ada.{marker}@kafe.example" });
                Assert.Equal($"ada.{marker}@kafe.example", (string?)Assert.Single(await All("contact_fields", new() { ["contact"] = id })).Fields["content"]);

                var note = await One("note_create", new() { ["contact"] = id, ["body"] = "Gillar kardemumma" });
                Assert.Equal(("Gillar kanel", true), ((string?)(await One("note_update", new() { ["note"] = note["id"]!.ToString()!, ["body"] = "Gillar kanel", ["is_favorited"] = "true" }))["body"], (bool?)(await One("note", new() { ["note"] = note["id"]!.ToString()! }))["is_favorited"]));

                var tag = "kafe" + marker;
                await One("contact_tags_add", new() { ["contact"] = id, ["tags"] = tag });
                Assert.StartsWith($"Monica already has a tag '{tag}' (", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("tag_create", new() { ["name"] = tag }))).Message);
                var untagged = await One("contact_tags_remove", new() { ["contact"] = id, ["tags"] = tag });
                Assert.DoesNotContain(tag, (string?)untagged["tags"] ?? string.Empty, StringComparison.Ordinal);

                var task = await One("task_create", new() { ["contact"] = id, ["title"] = "Ring Ada" });
                Assert.Equal(true, (bool?)(await One("task_update", new() { ["task"] = task["id"]!.ToString()!, ["completed"] = "true" }))["completed"]);
                var reminder = await One("reminder_create", new() { ["contact"] = id, ["title"] = "Ringa Ada", ["initial_date"] = "2026-11-01", ["frequency_type"] = "week", ["frequency_number"] = "2" });
                Assert.Equal("month", (string?)(await One("reminder_update", new() { ["reminder"] = reminder["id"]!.ToString()!, ["frequency_type"] = "month" }))["frequency_type"]);
                var activity = await One("activity_create", new() { ["activity_type"] = "1", ["summary"] = "Fika", ["happened_at"] = "2026-10-20", ["contacts"] = id });
                Assert.Equal("Fika igen", (string?)(await One("activity_update", new() { ["activity"] = activity["id"]!.ToString()!, ["summary"] = "Fika igen" }))["summary"]);
                var call = await One("call_create", new() { ["contact"] = id, ["called_at"] = "2026-10-21", ["content"] = "Om beställningen" });
                var conversation = await One("conversation_create", new() { ["contact"] = id, ["contact_field_type"] = "1", ["happened_at"] = "2026-10-21" });
                var withMessage = await One("message_create", new() { ["conversation"] = conversation["id"]!.ToString()!, ["content"] = "Hej Ada", ["written_at"] = "2026-10-21", ["written_by_me"] = "true" });
                var message = JsonNode.Parse((string)withMessage["messages"]!)![0]!["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var edited = await One("message_update", new() { ["conversation"] = conversation["id"]!.ToString()!, ["message"] = message, ["content"] = "Hej, Ada" });
                Assert.Equal("Hej, Ada", JsonNode.Parse((string)edited["messages"]!)![0]!["content"]!.GetValue<string>());
                var entry = await One("journal_entry_create", new() { ["title"] = "Torsdag " + marker, ["post"] = "Lugn dag" });

                foreach (var (operation, key, value) in new[] { ("call_delete", "call", call["id"]), ("conversation_delete", "conversation", conversation["id"]), ("activity_delete", "activity", activity["id"]), ("reminder_delete", "reminder", reminder["id"]), ("task_delete", "task", task["id"]), ("note_delete", "note", note["id"]), ("journal_entry_delete", "journal_entry", entry["id"]) })
                {
                    Assert.Equal(true, (bool?)(await One(operation, new() { [key] = value!.ToString()! }))["deleted"]);
                }

                Assert.Equal("Monica answered HTTP 404: The resource has not been found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("note", new() { ["note"] = note["id"]!.ToString()! }))).Message);
            }
            finally
            {
                await One("contact_delete", new() { ["contact"] = id });
            }

            var tags = await All("tags", new() { ["max"] = "500" });
            foreach (var leftover in tags.Where(item => ((string?)item.Fields["name"])?.EndsWith(marker, StringComparison.Ordinal) == true))
            {
                await One("tag_delete", new() { ["tag"] = leftover.Fields["id"]!.ToString()! });
            }
        }
    }
}
