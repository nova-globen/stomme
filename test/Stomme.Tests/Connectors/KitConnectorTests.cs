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
using Stomme.Modules.Connectors.Kit;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Kit (n8n's ConvertKit node: subscribers through forms, sequences and tags, and the custom fields): every call
    /// Kit's API v4 with the key in X-Kit-Api-Key; subscribers read by status, e-mail, dates and sort page by page by Kit's
    /// cursor as long as Kit says there is a next page, no more than asked; a subscriber got, created with its first name,
    /// state and fields, updated and unsubscribed; forms, sequences and tags read, a subscriber added to a form with a
    /// referrer and to a sequence, tagged and untagged by e-mail, their subscribers read by status; a tag created; custom fields
    /// read, created, renamed and deleted; what could go wrong refused before anything is sent; Kit's words on a failure; a
    /// test naming the key's account.
    /// </summary>
    public sealed class KitConnectorTests
    {
        private const string Api = "https://api.kit.com/v4";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "kit_key_1" };

        private sealed class Kit : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("X-Kit-Api-Key", out var keys) ? keys.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Kit kit, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new KitConnector(), Contexts.Extract(new StubEgress(kit), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Subscribers(int first, int count, bool next, string cursor) =>
            "{\"subscribers\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + (1000 + i) + ",\"first_name\":\"Ada " + i + "\",\"email_address\":\"s" + i + "@kafe.example.test\",\"state\":\"active\",\"fields\":{\"last_name\":\"Lovelace\"}}")) + "],\"pagination\":{\"has_previous_page\":false,\"has_next_page\":" + (next ? "true" : "false") + ",\"start_cursor\":\"s\",\"end_cursor\":\"" + cursor + "\",\"per_page\":1000}}";

        private const string Subscriber = "{\"subscriber\":{\"id\":31,\"first_name\":\"Ada\",\"email_address\":\"ada@kafe.example.test\",\"state\":\"active\",\"fields\":{\"last_name\":\"Lovelace\",\"cafe\":\"Kafé\"}}}";

        [Fact]
        public async Task Subscribers_are_read_page_by_page_got_created_updated_and_unsubscribed()
        {
            var kit = new Kit
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("after=WzEwMDBd", StringComparison.Ordinal) => (HttpStatusCode.OK, Subscribers(1001, 500, false, "WzE1MDBd")),
                    ("GET", var u) when u.Contains("/subscribers?", StringComparison.Ordinal) => (HttpStatusCode.OK, Subscribers(1, 1000, true, "WzEwMDBd")),
                    ("POST", var u) when u.EndsWith("/unsubscribe", StringComparison.Ordinal) => (HttpStatusCode.NoContent, ""),
                    ("POST", _) => (HttpStatusCode.Created, Subscriber),
                    _ => (HttpStatusCode.OK, Subscriber),
                },
            };
            (string, string, string?) Last() => (kit.Requests[^1].Method.Method, kit.Requests[^1].Url, kit.Requests[^1].Body);

            var read = await Run(kit, KitConnector.SubscribersOperation, new() { ["max"] = "1200" });
            Assert.Equal(1200, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/subscribers?per_page=1000", $"{Api}/subscribers?per_page=1000&after=WzEwMDBd" }, kit.Requests.Select(request => request.Url)); // by Kit's cursor
            Assert.All(kit.Requests, request => Assert.Equal("kit_key_1", request.Key));
            Assert.Equal((1001L, "Ada 1", "{\"last_name\":\"Lovelace\"}"), ((long?)read[0].Fields["id"], (string?)read[0].Fields["first_name"], (string?)read[0].Fields["fields"]));
            kit.Requests.Clear();
            Assert.Equal(1500, (await Run(kit, KitConnector.SubscribersOperation, new() { ["max"] = "2000" })).Count); // Kit's no next page the end
            Assert.Equal(2, kit.Requests.Count);
            kit.Requests.Clear();
            Assert.Equal(10, (await Run(kit, KitConnector.SubscribersOperation, new()
            {
                ["status"] = "All", ["email"] = "ada+fika@kafe.example.test", ["created_after"] = "2026-10-01", ["created_before"] = "2026-10-13", ["updated_after"] = "2026-10-02", ["updated_before"] = "2026-10-12", ["sort"] = "-created_at", ["max"] = "10",
            })).Count);
            Assert.Equal($"{Api}/subscribers?status=all&email_address=ada%2Bfika%40kafe.example.test&created_after=2026-10-01&created_before=2026-10-13&updated_after=2026-10-02&updated_before=2026-10-12&sort_field=created_at&sort_order=desc&per_page=10", Assert.Single(kit.Requests).Url);
            await Run(kit, KitConnector.SubscribersOperation, new() { ["sort"] = "id", ["max"] = "1" });
            Assert.Equal($"{Api}/subscribers?sort_field=id&sort_order=asc&per_page=1", Last().Item2);

            var got = Assert.Single(await Run(kit, KitConnector.SubscriberOperation, new() { ["subscriber"] = "31" }, endpoint: Api + "/")).Fields;
            Assert.Equal(("GET", $"{Api}/subscribers/31", (string?)null), Last());
            Assert.Equal((31L, "{\"last_name\":\"Lovelace\",\"cafe\":\"Kafé\"}"), ((long?)got["id"], (string?)got["fields"]));
            await Run(kit, KitConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["first_name"] = "Ada", ["state"] = "Inactive", ["fields"] = "{\"last_name\": \"Lovelace\", \"cafe\": \"Kafé\", \"visits\": 3, \"vip\": true, \"note\": null}" });
            Assert.Equal(("POST", $"{Api}/subscribers", (string?)"{\"email_address\":\"ada@kafe.example.test\",\"first_name\":\"Ada\",\"state\":\"inactive\",\"fields\":{\"last_name\":\"Lovelace\",\"cafe\":\"Kafé\",\"visits\":\"3\",\"vip\":\"true\",\"note\":null}}"), Last()); // a field a text
            await Run(kit, KitConnector.CreateSubscriberOperation, new() { ["email"] = "bo@kafe.example.test" });
            Assert.Equal("{\"email_address\":\"bo@kafe.example.test\"}", Last().Item3);
            Assert.Equal("Ada", (string?)Assert.Single(await Run(kit, KitConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31", ["first_name"] = "Ada", ["email"] = "ada@cafe.example.test" })).Fields["first_name"]);
            Assert.Equal(("PUT", $"{Api}/subscribers/31", (string?)"{\"email_address\":\"ada@cafe.example.test\",\"first_name\":\"Ada\"}"), Last());
            var gone = Assert.Single(await Run(kit, KitConnector.UnsubscribeOperation, new() { ["subscriber"] = "31" })).Fields;
            Assert.Equal(("POST", $"{Api}/subscribers/31/unsubscribe", (string?)"{}"), Last());
            Assert.Equal((31L, true), ((long?)gone["id"], (bool?)gone["unsubscribed"]));
        }

        [Fact]
        public async Task Forms_sequences_tags_and_custom_fields_are_kept()
        {
            var kit = new Kit
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/forms?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"forms\":[{\"id\":5,\"name\":\"Fika list\",\"type\":\"embed\",\"archived\":false}],\"pagination\":{\"has_next_page\":false,\"end_cursor\":\"x\"}}"),
                    ("GET", var u) when u.Contains("/sequences?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"sequences\":[{\"id\":7,\"name\":\"Välkommen\",\"hold\":false}],\"pagination\":{\"has_next_page\":false}}"),
                    ("GET", var u) when u.Contains("/tags?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"tags\":[{\"id\":9,\"name\":\"fika\"}],\"pagination\":{\"has_next_page\":false}}"),
                    ("GET", var u) when u.Contains("/custom_fields?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"custom_fields\":[{\"id\":3,\"key\":\"last_name\",\"label\":\"Last name\"}],\"pagination\":{\"has_next_page\":false}}"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"subscribers\":[{\"id\":31,\"email_address\":\"ada@kafe.example.test\",\"added_at\":\"2026-10-13T10:00:00Z\"}],\"pagination\":{\"has_next_page\":true,\"end_cursor\":\"\"}}"),
                    ("POST", var u) when u.EndsWith("/tags", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"tag\":{\"id\":9,\"name\":\"fika & bröd\"}}"),
                    ("POST", var u) when u.EndsWith("/custom_fields", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"custom_field\":{\"id\":3,\"key\":\"cafe\",\"label\":\"Café\"}}"),
                    ("PUT", _) => (HttpStatusCode.OK, "{\"custom_field\":{\"id\":3,\"key\":\"cafe\",\"label\":\"Kafé\"}}"),
                    ("DELETE", _) => (HttpStatusCode.NoContent, ""),
                    _ => (HttpStatusCode.Created, "{\"subscriber\":{\"id\":31,\"email_address\":\"ada@kafe.example.test\",\"referrer\":\"https://kafe.example.test\"}}"),
                },
            };
            (string, string, string?) Last() => (kit.Requests[^1].Method.Method, kit.Requests[^1].Url, kit.Requests[^1].Body);

            var form = Assert.Single(await Run(kit, KitConnector.FormsOperation, new() { ["status"] = "Archived" })).Fields;
            Assert.Equal(("Fika list", false), ((string?)form["name"], (bool?)form["archived"]));
            Assert.Equal($"{Api}/forms?status=archived&per_page=1000", Last().Item2);
            var added = Assert.Single(await Run(kit, KitConnector.AddToFormOperation, new() { ["form"] = "5", ["email"] = "ada@kafe.example.test", ["referrer"] = "https://kafe.example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/forms/5/subscribers", (string?)"{\"email_address\":\"ada@kafe.example.test\",\"referrer\":\"https://kafe.example.test\"}"), Last());
            Assert.Equal((31L, "https://kafe.example.test"), ((long?)added["id"], (string?)added["referrer"]));
            Assert.Equal("2026-10-13T10:00:00Z", (string?)Assert.Single(await Run(kit, KitConnector.FormSubscribersOperation, new() { ["form"] = "5", ["status"] = "cancelled" })).Fields["added_at"]);
            Assert.Equal($"{Api}/forms/5/subscribers?status=cancelled&per_page=1000", Last().Item2); // an empty cursor the end

            Assert.Equal("Välkommen", (string?)Assert.Single(await Run(kit, KitConnector.SequencesOperation, new())).Fields["name"]);
            Assert.Equal($"{Api}/sequences?per_page=1000", Last().Item2);
            await Run(kit, KitConnector.AddToSequenceOperation, new() { ["sequence"] = "7", ["email"] = "ada@kafe.example.test", ["referrer"] = "x" });
            Assert.Equal(("POST", $"{Api}/sequences/7/subscribers", (string?)"{\"email_address\":\"ada@kafe.example.test\"}"), Last()); // a referrer a form's alone
            await Run(kit, KitConnector.SequenceSubscribersOperation, new() { ["sequence"] = "7" });
            Assert.Equal($"{Api}/sequences/7/subscribers?per_page=1000", Last().Item2);

            Assert.Equal("fika", (string?)Assert.Single(await Run(kit, KitConnector.TagsOperation, new() { ["max"] = "2" })).Fields["name"]);
            Assert.Equal($"{Api}/tags?per_page=2", Last().Item2);
            Assert.Equal(9L, (long?)Assert.Single(await Run(kit, KitConnector.CreateTagOperation, new() { ["name"] = "fika & bröd" })).Fields["id"]);
            Assert.Equal(("POST", $"{Api}/tags", (string?)"{\"name\":\"fika & bröd\"}"), Last());
            await Run(kit, KitConnector.TagSubscriberOperation, new() { ["tag"] = "9", ["email"] = "ada@kafe.example.test" });
            Assert.Equal(("POST", $"{Api}/tags/9/subscribers", (string?)"{\"email_address\":\"ada@kafe.example.test\"}"), Last());
            var untagged = Assert.Single(await Run(kit, KitConnector.UntagSubscriberOperation, new() { ["tag"] = "9", ["email"] = "ada+fika@kafe.example.test" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/tags/9/subscribers?email_address=ada%2Bfika%40kafe.example.test", (string?)null), Last());
            Assert.Equal((9L, "ada+fika@kafe.example.test", true), ((long?)untagged["tag"], (string?)untagged["email_address"], (bool?)untagged["untagged"]));
            await Run(kit, KitConnector.TagSubscribersOperation, new() { ["tag"] = "9", ["status"] = "bounced" });
            Assert.Equal($"{Api}/tags/9/subscribers?status=bounced&per_page=1000", Last().Item2);

            Assert.Equal("last_name", (string?)Assert.Single(await Run(kit, KitConnector.CustomFieldsOperation, new())).Fields["key"]);
            Assert.Equal($"{Api}/custom_fields?per_page=1000", Last().Item2);
            Assert.Equal("Café", (string?)Assert.Single(await Run(kit, KitConnector.CreateCustomFieldOperation, new() { ["label"] = "Café" })).Fields["label"]);
            Assert.Equal(("POST", $"{Api}/custom_fields", (string?)"{\"label\":\"Café\"}"), Last());
            Assert.Equal("Kafé", (string?)Assert.Single(await Run(kit, KitConnector.UpdateCustomFieldOperation, new() { ["custom_field"] = "3", ["label"] = "Kafé" })).Fields["label"]);
            Assert.Equal(("PUT", $"{Api}/custom_fields/3", (string?)"{\"label\":\"Kafé\"}"), Last());
            var deleted = Assert.Single(await Run(kit, KitConnector.DeleteCustomFieldOperation, new() { ["custom_field"] = "3" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/custom_fields/3", (string?)null), Last());
            Assert.Equal((3L, true), ((long?)deleted["id"], (bool?)deleted["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_kits_words_on_a_failure()
        {
            var kit = new Kit();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(kit, operation, parameters, credentials: credentials))).Message;

            Assert.StartsWith("Kit has no operation 'broadcast' - subscribers, subscriber, create_subscriber,", await Refused("broadcast", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(KitConnector.TagsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the subscriber's id.", await Refused(KitConnector.UnsubscribeOperation, new()));
            Assert.Equal("'ada' is not an id (subscriber).", await Refused(KitConnector.SubscriberOperation, new() { ["subscriber"] = "ada" }));
            Assert.Equal("'0' is not an id (form).", await Refused(KitConnector.AddToFormOperation, new() { ["form"] = "0", ["email"] = "ada@kafe.example.test" }));
            Assert.Equal("Give the sequence's id.", await Refused(KitConnector.SequenceSubscribersOperation, new()));
            Assert.Equal("Give the tag's id.", await Refused(KitConnector.UntagSubscriberOperation, new() { ["email"] = "ada@kafe.example.test" }));
            Assert.Equal("Give the custom field's id.", await Refused(KitConnector.DeleteCustomFieldOperation, new()));
            Assert.Equal("Give the subscriber's e-mail.", await Refused(KitConnector.TagSubscriberOperation, new() { ["tag"] = "9" }));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(KitConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe" }));
            Assert.Equal("'ada@kafe.example.test,bo@kafe.example.test' is not an e-mail (email).", await Refused(KitConnector.SubscribersOperation, new() { ["email"] = "ada@kafe.example.test,bo@kafe.example.test" }));
            Assert.Equal("'gone' is not active, inactive, bounced, complained or cancelled (state).", await Refused(KitConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["state"] = "gone" }));
            Assert.Equal("The fields are not a JSON object - {\"last_name\": \"Lovelace\"}.", await Refused(KitConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["fields"] = "last_name = Lovelace" }));
            Assert.Equal("'tags' is not a text, a number, true or false or null (fields).", await Refused(KitConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31", ["fields"] = "{\"tags\": [\"fika\"]}" }));
            Assert.Equal("Give what to change - their e-mail, first name or fields.", await Refused(KitConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31", ["state"] = "active" }));
            Assert.Equal("Give the tag's name.", await Refused(KitConnector.CreateTagOperation, new()));
            Assert.Equal("The custom field's label is longer than 255 characters.", await Refused(KitConnector.UpdateCustomFieldOperation, new() { ["custom_field"] = "3", ["label"] = new string('l', 256) }));
            Assert.Equal("'gone' is not active, inactive, bounced, complained, cancelled or all (status).", await Refused(KitConnector.TagSubscribersOperation, new() { ["tag"] = "9", ["status"] = "gone" }));
            Assert.Equal("'bounced' is not active, archived, trashed or all (status).", await Refused(KitConnector.FormsOperation, new() { ["status"] = "bounced" }));
            Assert.Equal("'2026-13-01' is not a date - 2026-10-01 (created_after).", await Refused(KitConnector.SubscribersOperation, new() { ["created_after"] = "2026-13-01" }));
            Assert.Equal("'yesterday' is not a date - 2026-10-01 (updated_before).", await Refused(KitConnector.SubscribersOperation, new() { ["updated_before"] = "yesterday" }));
            Assert.Equal("'-email' is not id, created_at or updated_at, a leading - for descending (sort).", await Refused(KitConnector.SubscribersOperation, new() { ["sort"] = "-email" }));
            Assert.Equal("The Kit connection has no API key.", await Refused(KitConnector.TagsOperation, new(), credentials: new()));
            Assert.Empty(kit.Requests); // nothing sent
            Assert.Single(await Run(kit, KitConnector.UpdateCustomFieldOperation, new() { ["custom_field"] = "3", ["label"] = new string('l', 255) })); // 255 is a label

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Kit { Answer = (_, _) => (status, answer) }, KitConnector.TagSubscriberOperation, new() { ["tag"] = "9", ["email"] = "ada@kafe.example.test" }))).Message;
            Assert.Equal("HTTP 404: Not Found; The subscriber must exist first", await Failed(HttpStatusCode.NotFound, "{\"errors\":[\"Not Found\",\"The subscriber must exist first\"]}"));
            Assert.Equal("HTTP 401: The access token is invalid", await Failed(HttpStatusCode.Unauthorized, "{\"errors\":[\"The access token is invalid\"]}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errors\":[\"" + new string('e', 400) + "\"]}")); // a long one cut
            Assert.Equal("Kit answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("HTTP 422.", await Failed(HttpStatusCode.UnprocessableEntity, "{\"errors\":[]}"));
        }

        [Fact]
        public async Task A_test_names_the_keys_account()
        {
            Task<ConnectionTestResult> Test(Kit kit) =>
                new KitConnector().TestConnectionAsync(Contexts.Test(new StubEgress(kit), "", null, Credentials));
            var account = new Kit { Answer = (_, _) => (HttpStatusCode.OK, "{\"user\":{\"email\":\"ada@kafe.example.test\"},\"account\":{\"name\":\"Kafé\",\"plan_type\":\"creator\"}}") };
            var tested = await Test(account);
            Assert.Equal((true, "Kit: the key is Kafé's (ada@kafe.example.test)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/account", "GET", "kit_key_1"), (account.Requests[0].Url, account.Requests[0].Method.Method, account.Requests[0].Key));
            Assert.Equal("Kit: the key is an account's.", (await Test(new Kit())).Message);
            var refused = await Test(new Kit { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errors\":[\"The access token is invalid\"]}") });
            Assert.Equal((false, "HTTP 401: The access token is invalid"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "kit"), (new KitConnector().AuthType, new KitConnector().ConnectorType));
            Assert.Equal(20, new KitConnector().Operations.Count);
            Assert.Equal(("api-key", "V4 API key (Settings > Developer)"), (Assert.Single(new KitConnector().CredentialLabels!).Key, Assert.Single(new KitConnector().CredentialLabels!).Value)); // in Kit's words
            var none = new Kit { Answer = (_, _) => (HttpStatusCode.OK, "{\"subscribers\":[],\"pagination\":{\"has_next_page\":true,\"end_cursor\":\"WzBd\"}}") };
            Assert.Empty(await Run(none, null!, new())); // an empty page the end
            Assert.Equal($"{Api}/subscribers?per_page=1000", Assert.Single(none.Requests).Url); // no operation: the subscribers
        }
    }
}
