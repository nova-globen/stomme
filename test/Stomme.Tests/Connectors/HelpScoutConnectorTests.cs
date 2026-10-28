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
using Stomme.Modules.Connectors.HelpScout;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Help Scout (n8n's Help Scout node: a shared inbox's conversations and their threads, its customers and
    /// inboxes): the app's client credentials at Help Scout's token endpoint, the Mailbox API 2.0 with the token as a bearer;
    /// records read page by page to Help Scout's last page, no further than asked, by Help Scout's own query parameters; one
    /// got, created - got by the id Help Scout answers with, never the address it names - a thread added by its type's path,
    /// fields replaced one by one with the rest kept, a conversation deleted; what could go wrong refused before a token is
    /// asked for; Help Scout's message and the fields it names on a failure; a test counting the inboxes.
    /// </summary>
    public sealed class HelpScoutConnectorTests
    {
        private const string Api = "https://api.helpscout.net/v2";

        private const string TokenUrl = "https://api.helpscout.net/v2/oauth2/token";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "hs-app", ["client-secret"] = "hs-secret", ["token-url"] = TokenUrl };

        private sealed class HelpScout : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string, string?)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url == TokenUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"hs-token\",\"expires_in\":172800,\"token_type\":\"bearer\"}", Encoding.UTF8, "application/json") };
                }

                var (status, answer, created) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/hal+json") };
                if (created is not null)
                {
                    response.Headers.TryAddWithoutValidation("Resource-ID", created);
                    response.Headers.Location = new Uri("https://elsewhere.example.test/v2/conversations/" + created);
                }

                return response;
            }
        }

        private static Task<List<ConnectorRecord>> Run(HelpScout helpScout, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new HelpScoutConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(helpScout), "", operation, parameters, null, credentials ?? Credentials));

        private static string Conversations(int first, int count, int? pages, int number) =>
            "{\"_embedded\":{\"conversations\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"number\":" + (1000 + i) + ",\"subject\":\"Kassan " + i + "\",\"status\":\"active\",\"mailboxId\":123,\"tags\":[{\"id\":1,\"tag\":\"kassa\"}],\"primaryCustomer\":{\"id\":7,\"email\":\"ada@kafe.example.com\"},\"_links\":{\"self\":{\"href\":\"https://api.helpscout.net/v2/conversations/" + i + "\"}}}"))
            + "]}" + (pages is null ? "" : ",\"page\":{\"size\":50,\"totalElements\":" + (pages * 50) + ",\"totalPages\":" + pages + ",\"number\":" + number + "}") + ",\"_links\":{\"self\":{\"href\":\"x\"}}}";

        [Fact]
        public async Task Records_are_read_page_by_page_to_help_scouts_last_page_no_further_than_asked()
        {
            var helpScout = new HelpScout { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=3", StringComparison.Ordinal) ? Conversations(101, 20, 3, 3) : url.Contains("page=2", StringComparison.Ordinal) ? Conversations(51, 50, 3, 2) : Conversations(1, 50, 3, 1), null) };

            var conversations = await Run(helpScout, "records", new() { ["resource"] = "conversations" });
            Assert.Equal(120, conversations.Count); // to Help Scout's last page
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=hs-app&client_secret=hs-secret"), (helpScout.Requests[0].Method.Method, helpScout.Requests[0].Url, helpScout.Requests[0].Body));
            Assert.Equal(new[] { $"{Api}/conversations?page=1", $"{Api}/conversations?page=2", $"{Api}/conversations?page=3" }, helpScout.Requests.Skip(1).Select(request => request.Url));
            Assert.All(helpScout.Requests.Skip(1), request => Assert.Equal("Bearer hs-token", request.Authorization));
            var first = conversations[0].Fields;
            Assert.Equal((1L, "Kassan 1", 123L, "[{\"id\":1,\"tag\":\"kassa\"}]", "{\"id\":7,\"email\":\"ada@kafe.example.com\"}"), ((long?)first["id"], (string?)first["subject"], (long?)first["mailboxId"], (string?)first["tags"], (string?)first["primaryCustomer"]));
            Assert.False(first.ContainsKey("_links")); // its links left out

            helpScout.Requests.Clear();
            Assert.Equal(60, (await Run(helpScout, null!, new() { ["resource"] = "conversations", ["max"] = "60", ["filters"] = "{\"status\": \"all\", \"mailbox\": 123, \"tag\": \"kassa & fika\", \"query\": \"(subject:\\\"Kassan\\\")\", \"embed\": \"threads\"}" })).Count); // no operation: the records - no further than asked
            Assert.Equal(new[] { $"{Api}/conversations?page=1&status=all&mailbox=123&tag=kassa%20%26%20fika&query=%28subject%3A%22Kassan%22%29&embed=threads", $"{Api}/conversations?page=2&status=all&mailbox=123&tag=kassa%20%26%20fika&query=%28subject%3A%22Kassan%22%29&embed=threads" }, helpScout.Requests.Skip(1).Select(request => request.Url));

            var threads = new HelpScout { Answer = (_, _) => (HttpStatusCode.OK, "{\"_embedded\":{\"threads\":[{\"id\":9,\"type\":\"customer\",\"body\":\"Hej\"}]},\"page\":{\"totalPages\":1,\"number\":1}}", null) };
            Assert.Equal("Hej", (string?)Assert.Single(await Run(threads, "records", new() { ["resource"] = "threads", ["conversation"] = "1042" })).Fields["body"]);
            Assert.Equal($"{Api}/conversations/1042/threads?page=1", threads.Requests[^1].Url);
            var properties = new HelpScout { Answer = (_, _) => (HttpStatusCode.OK, "{\"_embedded\":{\"customer-properties\":[{\"slug\":\"kassa\",\"name\":\"Kassa\"}]}}", null) };
            Assert.Equal("kassa", (string?)Assert.Single(await Run(properties, "records", new() { ["resource"] = "customer_properties" })).Fields["slug"]); // no pages named: one page
            Assert.Equal($"{Api}/customer-properties?page=1", properties.Requests[^1].Url);
            var endless = new HelpScout { Answer = (_, url) => (HttpStatusCode.OK, Conversations(1, 50, 100, 1), null) };
            Assert.Equal(1000, (await Run(endless, "records", new() { ["resource"] = "conversations" })).Count); // 1,000 at most when no max is given
            Assert.Equal($"{Api}/conversations?page=20", endless.Requests[^1].Url);
            var emptied = new HelpScout { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=2", StringComparison.Ordinal) ? Conversations(1, 0, 9, 2) : Conversations(1, 50, 9, 1), null) };
            Assert.Equal(50, (await Run(emptied, "records", new() { ["resource"] = "conversations" })).Count); // an empty page ends it
            Assert.Equal(3, emptied.Requests.Count);
        }

        [Fact]
        public async Task A_record_is_got_created_by_the_id_help_scout_answers_updated_field_by_field_and_deleted()
        {
            var helpScout = new HelpScout
            {
                Answer = (method, url) => method == HttpMethod.Delete || method == HttpMethod.Patch ? (HttpStatusCode.NoContent, "", null)
                    : method == HttpMethod.Post ? (HttpStatusCode.Created, "", url.Contains("/customers", StringComparison.Ordinal) ? "7" : url.EndsWith("/notes", StringComparison.Ordinal) ? "55" : "1043")
                    : url.Contains("/customers/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":7,\"firstName\":\"Ada\",\"organization\":\"Kafé AB\",\"_links\":{}}", null)
                    : (HttpStatusCode.OK, "{\"id\":1043,\"subject\":\"Kassan är nere\",\"status\":\"active\",\"_embedded\":{\"threads\":[]}}", null),
            };
            (string, string, string?) Last() => (helpScout.Requests[^1].Method.Method, helpScout.Requests[^1].Url, helpScout.Requests[^1].Body);

            var conversation = Assert.Single(await Run(helpScout, "record", new() { ["resource"] = "conversations", ["id"] = "1043" })).Fields;
            Assert.Equal(("GET", $"{Api}/conversations/1043", (string?)null), Last());
            Assert.Equal(("Kassan är nere", "{\"threads\":[]}"), ((string?)conversation["subject"], (string?)conversation["_embedded"]));

            helpScout.Requests.Clear();
            await Run(helpScout, "create_record", new() { ["resource"] = "conversations", ["values"] = "{\"subject\": \"Kassan är nere\", \"mailboxId\": 123, \"type\": \"email\", \"status\": \"active\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"threads\": [{\"type\": \"customer\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"text\": \"Hej\"}]}" });
            Assert.Equal(new[]
            {
                ("POST", $"{Api}/conversations", (string?)"{\"subject\":\"Kassan är nere\",\"mailboxId\":123,\"type\":\"email\",\"status\":\"active\",\"customer\":{\"email\":\"ada@kafe.example.com\"},\"threads\":[{\"type\":\"customer\",\"customer\":{\"email\":\"ada@kafe.example.com\"},\"text\":\"Hej\"}]}"),
                ("GET", $"{Api}/conversations/1043", null), // got by its id - not the address the answer named
            }, helpScout.Requests.Skip(1).Select(request => (request.Method.Method, request.Url, request.Body)));
            var customer = Assert.Single(await Run(helpScout, "create_record", new() { ["resource"] = "customers", ["values"] = "{\"firstName\": \"Ada\", \"emails\": [{\"type\": \"work\", \"value\": \"ada@kafe.example.com\"}]}" })).Fields;
            Assert.Equal(("GET", $"{Api}/customers/7", (string?)null), Last());
            Assert.Equal(("Ada", false), ((string?)customer["firstName"], customer.ContainsKey("_links")));

            var thread = Assert.Single(await Run(helpScout, "create_record", new() { ["resource"] = "threads", ["conversation"] = "1043", ["values"] = "{\"type\": \"note\", \"text\": \"Ringde kunden\"}" })).Fields;
            Assert.Equal(("POST", $"{Api}/conversations/1043/notes", (string?)"{\"text\":\"Ringde kunden\"}"), Last()); // its type its path
            Assert.Equal(((object?)55L, (object?)1043L, (object?)true), (thread["id"], thread["conversation"], thread["created"]));
            await Run(helpScout, "create_record", new() { ["resource"] = "threads", ["conversation"] = "1043", ["values"] = "{\"type\": \"reply\", \"text\": \"Vi tittar på det\", \"customer\": {\"id\": 7}}" });
            Assert.Equal(("POST", $"{Api}/conversations/1043/reply", (string?)"{\"text\":\"Vi tittar på det\",\"customer\":{\"id\":7}}"), Last());

            helpScout.Requests.Clear();
            var updated = Assert.Single(await Run(helpScout, "update_record", new() { ["resource"] = "customers", ["id"] = "7", ["values"] = "{\"firstName\": \"Ada\", \"organization\": \"Kafé AB\"}" })).Fields;
            Assert.Equal(new[]
            {
                ("PATCH", $"{Api}/customers/7", (string?)"[{\"op\":\"replace\",\"path\":\"/firstName\",\"value\":\"Ada\"},{\"op\":\"replace\",\"path\":\"/organization\",\"value\":\"Kafé AB\"}]"),
                ("GET", $"{Api}/customers/7", null),
            }, helpScout.Requests.Skip(1).Select(request => (request.Method.Method, request.Url, request.Body)));
            Assert.Equal("Kafé AB", (string?)updated["organization"]);
            await Run(helpScout, "update_record", new() { ["resource"] = "conversations", ["id"] = "1043", ["values"] = "{\"status\": \"closed\", \"primaryCustomer.id\": 7}" });
            Assert.Equal("[{\"op\":\"replace\",\"path\":\"/status\",\"value\":\"closed\"},{\"op\":\"replace\",\"path\":\"/primaryCustomer.id\",\"value\":7}]", helpScout.Requests[^2].Body);

            var deleted = Assert.Single(await Run(helpScout, "Delete_Record", new() { ["resource"] = "conversations", ["id"] = "1043" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/conversations/1043", (string?)null), Last());
            Assert.Equal(((object?)1043L, (object?)true), (deleted["id"], deleted["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_help_scouts_words_on_a_failure()
        {
            var helpScout = new HelpScout();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(helpScout, operation, parameters, credentials))).Message;

            Assert.Equal("Help Scout has no operation 'tickets' - records, record, create_record, update_record or delete_record.", await Refused("tickets", new()));
            Assert.Equal("Give the resource - conversations, customers, mailboxes, threads or customer_properties (resource).", await Refused("records", new()));
            Assert.Equal("'Conversations' is not one of Help Scout's resources - conversations, customers, mailboxes, threads or customer_properties (resource).", await Refused("records", new() { ["resource"] = "Conversations" }));
            Assert.Equal("Help Scout's threads cannot be got one by one here - conversations, customers or mailboxes (resource).", await Refused("record", new() { ["resource"] = "threads", ["id"] = "1" }));
            Assert.Equal("Help Scout's mailboxes cannot be created here - conversations, customers or threads (resource).", await Refused("create_record", new() { ["resource"] = "mailboxes", ["values"] = "{\"name\": \"x\"}" }));
            Assert.Equal("Help Scout's threads cannot be updated here - conversations or customers (resource).", await Refused("update_record", new() { ["resource"] = "threads", ["id"] = "1", ["values"] = "{\"text\": \"x\"}" }));
            Assert.Equal("Help Scout's customers cannot be deleted here - conversations (resource).", await Refused("delete_record", new() { ["resource"] = "customers", ["id"] = "7" }));
            Assert.Equal("Give the conversation's id (conversation).", await Refused("records", new() { ["resource"] = "threads" }));
            Assert.Equal("'1042/x' is not the conversation's id - a number (conversation).", await Refused("records", new() { ["resource"] = "threads", ["conversation"] = "1042/x" }));
            Assert.Equal("Give the record's id (id).", await Refused("record", new() { ["resource"] = "conversations" }));
            Assert.Equal("'#1042' is not the record's id - a number (id).", await Refused("delete_record", new() { ["resource"] = "conversations", ["id"] = "#1042" }));
            Assert.Equal("'../1042' is not the record's id - a number (id).", await Refused("record", new() { ["resource"] = "customers", ["id"] = "../1042" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("records", new() { ["resource"] = "conversations", ["max"] = "0" }));
            Assert.Equal("The filters are not a JSON object of Help Scout's query parameters - {\"status\": \"all\"} (filters).", await Refused("records", new() { ["resource"] = "conversations", ["filters"] = "status=all" }));
            Assert.Equal("'page' is set by the step - give max instead (filters).", await Refused("records", new() { ["resource"] = "conversations", ["filters"] = "{\"page\": 2}" }));
            Assert.Equal("'tag&x' is not one of Help Scout's query parameters (filters).", await Refused("records", new() { ["resource"] = "conversations", ["filters"] = "{\"tag&x\": \"kassa\"}" }));
            foreach (var value in new[] { "[1]", "null", "\"\"", "\"kassa\\u0001\"" })
            {
                Assert.Equal("The filter 'tag' is not a text, a number or true or false (filters).", await Refused("records", new() { ["resource"] = "conversations", ["filters"] = "{\"tag\": " + value + "}" }));
            }

            Assert.Equal("Give the values - a JSON object of fields (values).", await Refused("create_record", new() { ["resource"] = "conversations" }));
            const string NotAnObject = "The values are not a JSON object of fields - {\"subject\": \"Kassan är nere\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["resource"] = "conversations", ["values"] = "subject = Kassan" }));
            Assert.Equal(NotAnObject, await Refused("update_record", new() { ["resource"] = "customers", ["id"] = "7", ["values"] = "{}" }));
            Assert.Equal("'first name' is not a field's name - firstName, subject, primaryCustomer.id (values).", await Refused("update_record", new() { ["resource"] = "customers", ["id"] = "7", ["values"] = "{\"first name\": \"Ada\"}" }));
            Assert.Equal("'primaryCustomer/id' is not a field's name - firstName, subject, primaryCustomer.id (values).", await Refused("update_record", new() { ["resource"] = "conversations", ["id"] = "7", ["values"] = "{\"primaryCustomer/id\": 7}" }));
            foreach (var values in new[] { "{\"text\": \"x\"}", "{\"type\": \"letter\", \"text\": \"x\"}", "{\"type\": 1, \"text\": \"x\"}" })
            {
                Assert.Equal("Give the thread's type among the values - reply, customer, note, chat or phone (values).", await Refused("create_record", new() { ["resource"] = "threads", ["conversation"] = "1043", ["values"] = values }));
            }

            Assert.Equal("Give Help Scout's token endpoint - https://api.helpscout.net/v2/oauth2/token.", await Refused("records", new() { ["resource"] = "conversations" }, new() { ["client-id"] = "hs-app", ["client-secret"] = "hs-secret" }));
            Assert.Empty(helpScout.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "record", string? created = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new HelpScout { Answer = (_, _) => (status, answer, created) }, operation, new() { ["resource"] = "conversations", ["id"] = "1043", ["values"] = "{\"subject\": \"x\"}" }))).Message;
            Assert.Equal("HTTP 400 Bad request - subject: may not be empty; mailboxId: must be a valid inbox", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"Bad request\",\"logRef\":\"x\",\"_embedded\":{\"errors\":[{\"path\":\"subject\",\"message\":\"may not be empty\",\"source\":\"JSON\"},{\"path\":\"mailboxId\",\"message\":\"must be a valid inbox\"}]}}", "create_record"));
            Assert.Equal("HTTP 404 Resource not found", await Failed(HttpStatusCode.NotFound, "{\"message\":\"Resource not found\"}"));
            Assert.Equal("HTTP 401.", await Failed(HttpStatusCode.Unauthorized, ""));
            Assert.Equal("HTTP 400 " + new string('e', 300), await Failed(HttpStatusCode.BadRequest, "{\"message\":\"" + new string('e', 400) + "\"}", "create_record")); // a long one cut
            Assert.Equal("Help Scout answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("Help Scout answered with no conversation.", await Failed(HttpStatusCode.OK, ""));
            Assert.Equal("Help Scout created the conversation but named no id for it.", await Failed(HttpStatusCode.Created, "", "create_record"));
            Assert.Equal("Help Scout created the conversation but named no id for it.", await Failed(HttpStatusCode.Created, "", "create_record", created: "../customers"));
        }

        [Fact]
        public async Task A_test_counts_the_inboxes()
        {
            Task<ConnectionTestResult> Test(HelpScout helpScout) =>
                new HelpScoutConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(helpScout), "", null, Credentials));
            var two = new HelpScout { Answer = (_, _) => (HttpStatusCode.OK, "{\"_embedded\":{\"mailboxes\":[{\"id\":1},{\"id\":2}]},\"page\":{\"totalElements\":2}}", null) };
            Assert.Equal((true, "Help Scout: signed in - 2 inboxes."), ((await Test(two)).Success, (await Test(two)).Message));
            Assert.Equal($"{Api}/mailboxes", two.Requests[1].Url);
            Assert.Equal("Help Scout: signed in - 1 inbox.", (await Test(new HelpScout { Answer = (_, _) => (HttpStatusCode.OK, "{\"page\":{\"totalElements\":1}}", null) })).Message);
            Assert.Equal("Help Scout: signed in.", (await Test(new HelpScout { Answer = (_, _) => (HttpStatusCode.OK, "{}", null) })).Message);
            var refused = await Test(new HelpScout { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"message\":\"Not authorized\"}", null) });
            Assert.Equal((false, "HTTP 403 Not authorized"), (refused.Success, refused.Message));
            var connector = new HelpScoutConnector();
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "helpscout", 5), (connector.AuthType, connector.ConnectorType, connector.Operations.Count));
        }
    }
}
