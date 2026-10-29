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
using Stomme.Modules.Connectors.MailerLite;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// MailerLite (n8n's MailerLite node: a subscriber created, got, read and updated): every call MailerLite's API
    /// with the token as a bearer token, asking for JSON; subscribers read by status page by page by MailerLite's cursor, the
    /// size fixed and no bigger than asked; a subscriber got by id or e-mail, created with its fields, groups, status, times
    /// and addresses, updated, deleted and forgotten, an e-mail looked up to its id where MailerLite asks for one; groups read
    /// by name and sort page by page, created, renamed and deleted, a group's subscribers, a subscriber added to and taken out
    /// of a group; the fields; what could go wrong refused before anything is sent; MailerLite's word and each field's on a
    /// failure; a test naming how many groups the account has.
    /// </summary>
    public sealed class MailerLiteConnectorTests
    {
        private const string Api = "https://connect.mailerlite.com/api";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "ml-token-1" };

        private sealed class MailerLite : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Accept, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(MailerLite mailerLite, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MailerLiteConnector(), Contexts.Extract(new StubEgress(mailerLite), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Subscribers(int first, int count, string? next) =>
            "{\"data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (1000 + i) + "\",\"email\":\"s" + i + "@kafe.example.test\",\"status\":\"active\",\"opens_count\":" + i + ",\"fields\":{\"name\":\"Kafé " + i + "\"},\"groups\":[]}")) + "],\"links\":{},\"meta\":{\"per_page\":100,\"next_cursor\":" + (next is null ? "null" : "\"" + next + "\"") + "}}";

        [Fact]
        public async Task Subscribers_are_read_page_by_page_got_created_updated_deleted_and_forgotten()
        {
            var mailerLite = new MailerLite
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("cursor=c2", StringComparison.Ordinal) => (HttpStatusCode.OK, Subscribers(101, 50, "c3")),
                    ("GET", var u) when u.Contains("/subscribers?", StringComparison.Ordinal) => (HttpStatusCode.OK, Subscribers(1, 100, "c2")),
                    ("GET", _) => (HttpStatusCode.OK, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\",\"status\":\"active\",\"fields\":{\"name\":\"Ada\"},\"groups\":[{\"id\":\"7\",\"name\":\"Fika\"}]}}"),
                    ("POST", var u) when u.EndsWith("/forget", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"message\":\"Subscriber data will be completely deleted and forgotten within 30 days.\",\"data\":{\"id\":\"31\"}}"),
                    ("POST", _) => (HttpStatusCode.Created, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\",\"status\":\"active\",\"fields\":{\"name\":\"Ada\",\"company\":\"Kafé\"}}}"),
                    ("PUT", _) => (HttpStatusCode.OK, "{\"data\":{\"id\":\"31\",\"status\":\"unsubscribed\"}}"),
                    _ => (HttpStatusCode.NoContent, ""),
                },
            };
            (string, string, string?) Last() => (mailerLite.Requests[^1].Method.Method, mailerLite.Requests[^1].Url, mailerLite.Requests[^1].Body);

            var read = await Run(mailerLite, MailerLiteConnector.SubscribersOperation, new() { ["status"] = "Active", ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/subscribers?filter%5Bstatus%5D=active&limit=100", $"{Api}/subscribers?filter%5Bstatus%5D=active&limit=100&cursor=c2" }, mailerLite.Requests.Select(request => request.Url)); // by MailerLite's cursor
            Assert.All(mailerLite.Requests, request => Assert.Equal(("Bearer ml-token-1", "application/json"), (request.Authorization, request.Accept)));
            Assert.Equal(("1001", "s1@kafe.example.test", 1L, "{\"name\":\"Kafé 1\"}", "[]"), ((string?)read[0].Fields["id"], (string?)read[0].Fields["email"], (long?)read[0].Fields["opens_count"], (string?)read[0].Fields["fields"], (string?)read[0].Fields["groups"]));
            Assert.Equal("1120", (string?)read[119].Fields["id"]);
            mailerLite.Requests.Clear();
            Assert.Equal(30, (await Run(mailerLite, MailerLiteConnector.SubscribersOperation, new() { ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/subscribers?limit=30", Assert.Single(mailerLite.Requests).Url); // a page as small as asked, every status
            mailerLite.Requests.Clear();
            Assert.Equal(150, (await Run(mailerLite, MailerLiteConnector.SubscribersOperation, new())).Count); // a short page the end
            Assert.Equal(2, mailerLite.Requests.Count);
            Assert.Single(await Run(mailerLite, MailerLiteConnector.SubscribersOperation, new() { ["status"] = "JUNK", ["max"] = "1" }));
            Assert.Equal($"{Api}/subscribers?filter%5Bstatus%5D=junk&limit=1", mailerLite.Requests[^1].Url); // each of MailerLite's statuses
            Assert.Single(await Run(mailerLite, null!, new() { ["max"] = "1" }));
            Assert.Equal($"{Api}/subscribers?limit=1", mailerLite.Requests[^1].Url); // no operation: the subscribers

            var got = Assert.Single(await Run(mailerLite, MailerLiteConnector.SubscriberOperation, new() { ["subscriber"] = "ada@kafe.example.test" }, endpoint: "https://connect.mailerlite.com")).Fields;
            Assert.Equal(("GET", $"{Api}/subscribers/ada%40kafe.example.test", (string?)null), Last()); // by e-mail, escaped; /api added once
            Assert.Equal(("31", "[{\"id\":\"7\",\"name\":\"Fika\"}]"), ((string?)got["id"], (string?)got["groups"]));
            await Run(mailerLite, MailerLiteConnector.SubscriberOperation, new() { ["subscriber"] = "31" }, endpoint: Api + "/");
            Assert.Equal($"{Api}/subscribers/31", Last().Item2);

            var created = Assert.Single(await Run(mailerLite, MailerLiteConnector.CreateSubscriberOperation, new()
            {
                ["email"] = "ada@kafe.example.test", ["fields"] = "{\"name\": \"Ada\", \"company\": \"Kafé\", \"visits\": 3, \"note\": null}", ["groups"] = "7, 8",
                ["status"] = "ACTIVE", ["subscribed_at"] = "2026-10-13T12:00:00+02:00", ["opted_in_at"] = "2026-10-13 10:05:00", ["unsubscribed_at"] = "2026-10-14",
                ["ip_address"] = "192.0.2.10", ["optin_ip"] = "2001:db8::1",
            })).Fields;
            Assert.Equal(("POST", $"{Api}/subscribers", (string?)"{\"email\":\"ada@kafe.example.test\",\"fields\":{\"name\":\"Ada\",\"company\":\"Kafé\",\"visits\":3,\"note\":null},\"groups\":[\"7\",\"8\"],\"status\":\"active\",\"subscribed_at\":\"2026-10-13 10:00:00\",\"opted_in_at\":\"2026-10-13 10:05:00\",\"unsubscribed_at\":\"2026-10-14 00:00:00\",\"ip_address\":\"192.0.2.10\",\"optin_ip\":\"2001:db8::1\"}"), Last()); // a time as MailerLite reads it, in UTC
            Assert.Equal(("31", "{\"name\":\"Ada\",\"company\":\"Kafé\"}"), ((string?)created["id"], (string?)created["fields"]));
            await Run(mailerLite, MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "bo@kafe.example.test" });
            Assert.Equal(("POST", $"{Api}/subscribers", (string?)"{\"email\":\"bo@kafe.example.test\"}"), Last());

            mailerLite.Requests.Clear();
            var updated = Assert.Single(await Run(mailerLite, MailerLiteConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "ada@kafe.example.test", ["status"] = "unsubscribed", ["fields"] = "{\"name\": \"Ada L\"}" })).Fields;
            Assert.Equal(new[] { ("GET", $"{Api}/subscribers/ada%40kafe.example.test", (string?)null), ("PUT", $"{Api}/subscribers/31", "{\"fields\":{\"name\":\"Ada L\"},\"status\":\"unsubscribed\"}") }, mailerLite.Requests.Select(request => (request.Method.Method, request.Url, request.Body))); // the e-mail looked up to its id
            Assert.Equal("unsubscribed", (string?)updated["status"]);
            mailerLite.Requests.Clear();
            await Run(mailerLite, MailerLiteConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31", ["groups"] = "9" });
            Assert.Equal(("PUT", $"{Api}/subscribers/31", (string?)"{\"groups\":[\"9\"]}"), Assert.Single(mailerLite.Requests.Select(request => (request.Method.Method, request.Url, request.Body)))); // an id used as it is

            var deleted = Assert.Single(await Run(mailerLite, MailerLiteConnector.DeleteSubscriberOperation, new() { ["subscriber"] = "31" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/subscribers/31", (string?)null), Last());
            Assert.Equal(("31", true), ((string?)deleted["id"], (bool?)deleted["deleted"]));
            mailerLite.Requests.Clear();
            var forgotten = Assert.Single(await Run(mailerLite, MailerLiteConnector.ForgetSubscriberOperation, new() { ["subscriber"] = "ada@kafe.example.test" })).Fields;
            Assert.Equal(new[] { ("GET", $"{Api}/subscribers/ada%40kafe.example.test"), ("POST", $"{Api}/subscribers/31/forget") }, mailerLite.Requests.Select(request => (request.Method.Method, request.Url)));
            Assert.Equal(("31", true, "Subscriber data will be completely deleted and forgotten within 30 days."), ((string?)forgotten["id"], (bool?)forgotten["forgotten"], (string?)forgotten["message"]));
        }

        [Fact]
        public async Task Groups_are_read_created_renamed_deleted_and_filled_and_the_fields_read()
        {
            static string Page(int first, int count, int last) =>
                "{\"data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (700 + i) + "\",\"name\":\"Fika " + i + "\",\"active_count\":" + i + "}")) + "],\"meta\":{\"current_page\":1,\"last_page\":" + last + ",\"total\":250}}";
            var mailerLite = new MailerLite
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("filter%5Bname%5D=short", StringComparison.Ordinal) => (HttpStatusCode.OK, Page(1, 30, 5)),
                    ("GET", var u) when u.Contains("/groups?", StringComparison.Ordinal) && u.Contains("page=1", StringComparison.Ordinal) => (HttpStatusCode.OK, Page(1, 100, 3)),
                    ("GET", var u) when u.Contains("/groups?", StringComparison.Ordinal) && u.Contains("page=2", StringComparison.Ordinal) => (HttpStatusCode.OK, Page(101, 100, 2)),
                    ("GET", var u) when u.Contains("/groups/7/subscribers", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"31\",\"email\":\"ada@kafe.example.test\"}],\"meta\":{\"next_cursor\":null}}"),
                    ("GET", var u) when u.Contains("/fields", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"1\",\"name\":\"Name\",\"key\":\"name\",\"type\":\"text\",\"is_default\":true},{\"id\":\"2\",\"name\":\"Visits\",\"key\":\"visits\",\"type\":\"number\",\"is_default\":false}],\"meta\":{\"last_page\":1}}"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\"}}"),
                    ("POST", var u) when u.Contains("/subscribers/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{\"id\":\"7\",\"name\":\"Fika\",\"active_count\":12}}"),
                    ("POST", _) or ("PUT", _) => (HttpStatusCode.OK, "{\"data\":{\"id\":\"7\",\"name\":\"Fika & bröd\",\"active_count\":0}}"),
                    _ => (HttpStatusCode.NoContent, ""),
                },
            };
            (string, string, string?) Last() => (mailerLite.Requests[^1].Method.Method, mailerLite.Requests[^1].Url, mailerLite.Requests[^1].Body);

            var groups = await Run(mailerLite, MailerLiteConnector.GroupsOperation, new() { ["name"] = "Fika & bröd", ["sort"] = "-created_at" });
            Assert.Equal(200, groups.Count); // the last page the end
            Assert.Equal(new[] { $"{Api}/groups?filter%5Bname%5D=Fika%20%26%20br%C3%B6d&sort=-created_at&limit=100&page=1", $"{Api}/groups?filter%5Bname%5D=Fika%20%26%20br%C3%B6d&sort=-created_at&limit=100&page=2" }, mailerLite.Requests.Select(request => request.Url));
            Assert.Equal(("701", "Fika 1", 1L), ((string?)groups[0].Fields["id"], (string?)groups[0].Fields["name"], (long?)groups[0].Fields["active_count"]));
            mailerLite.Requests.Clear();
            Assert.Equal(150, (await Run(mailerLite, MailerLiteConnector.GroupsOperation, new() { ["max"] = "150" })).Count);
            Assert.Equal(new[] { $"{Api}/groups?limit=100&page=1", $"{Api}/groups?limit=100&page=2" }, mailerLite.Requests.Select(request => request.Url)); // no more than asked
            mailerLite.Requests.Clear();
            Assert.Equal(5, (await Run(mailerLite, MailerLiteConnector.GroupsOperation, new() { ["max"] = "5" })).Count);
            Assert.Equal($"{Api}/groups?limit=5&page=1", Assert.Single(mailerLite.Requests).Url);
            mailerLite.Requests.Clear();
            Assert.Equal(30, (await Run(mailerLite, MailerLiteConnector.GroupsOperation, new() { ["name"] = "short" })).Count);
            Assert.Single(mailerLite.Requests); // a short page the end, though MailerLite counts more pages

            var made = Assert.Single(await Run(mailerLite, MailerLiteConnector.CreateGroupOperation, new() { ["name"] = "Fika & bröd" })).Fields;
            Assert.Equal(("POST", $"{Api}/groups", (string?)"{\"name\":\"Fika & bröd\"}"), Last());
            Assert.Equal(("7", "Fika & bröd"), ((string?)made["id"], (string?)made["name"]));
            await Run(mailerLite, MailerLiteConnector.UpdateGroupOperation, new() { ["group"] = "7", ["name"] = "Fika" });
            Assert.Equal(("PUT", $"{Api}/groups/7", (string?)"{\"name\":\"Fika\"}"), Last());
            var gone = Assert.Single(await Run(mailerLite, MailerLiteConnector.DeleteGroupOperation, new() { ["group"] = "7" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/groups/7", (string?)null), Last());
            Assert.Equal(("7", true), ((string?)gone["id"], (bool?)gone["deleted"]));

            Assert.Equal("ada@kafe.example.test", (string?)Assert.Single(await Run(mailerLite, MailerLiteConnector.GroupSubscribersOperation, new() { ["group"] = "7", ["status"] = "unconfirmed" })).Fields["email"]);
            Assert.Equal($"{Api}/groups/7/subscribers?filter%5Bstatus%5D=unconfirmed&limit=100", Last().Item2);

            mailerLite.Requests.Clear();
            var added = Assert.Single(await Run(mailerLite, MailerLiteConnector.AddToGroupOperation, new() { ["subscriber"] = "ada@kafe.example.test", ["group"] = "7" })).Fields;
            Assert.Equal(new[] { ("GET", $"{Api}/subscribers/ada%40kafe.example.test"), ("POST", $"{Api}/subscribers/31/groups/7") }, mailerLite.Requests.Select(request => (request.Method.Method, request.Url)));
            Assert.Equal(("7", "Fika", 12L, "31"), ((string?)added["id"], (string?)added["name"], (long?)added["active_count"], (string?)added["subscriber"]));
            var removed = Assert.Single(await Run(mailerLite, MailerLiteConnector.RemoveFromGroupOperation, new() { ["subscriber"] = "31", ["group"] = "7" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/subscribers/31/groups/7", (string?)null), Last());
            Assert.Equal(("31", "7", true), ((string?)removed["subscriber"], (string?)removed["group"], (bool?)removed["removed"]));

            var fields = await Run(mailerLite, MailerLiteConnector.FieldsOperation, new());
            Assert.Equal(("GET", $"{Api}/fields?limit=100&page=1", (string?)null), Last());
            Assert.Equal(new[] { ("name", "text", true), ("visits", "number", false) }, fields.Select(field => ((string)field.Fields["key"]!, (string)field.Fields["type"]!, (bool)field.Fields["is_default"]!)));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mailerlites_word_on_a_failure()
        {
            var mailerLite = new MailerLite();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mailerLite, operation, parameters, credentials: credentials))).Message;

            Assert.StartsWith("MailerLite has no operation 'unsubscribe' - subscribers, subscriber, create_subscriber,", await Refused("unsubscribe", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MailerLiteConnector.GroupsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the subscriber - their id or e-mail.", await Refused(MailerLiteConnector.SubscriberOperation, new()));
            Assert.Equal("'ada' is not a subscriber's id or e-mail.", await Refused(MailerLiteConnector.DeleteSubscriberOperation, new() { ["subscriber"] = "ada" }));
            Assert.Equal("Give the group's id.", await Refused(MailerLiteConnector.GroupSubscribersOperation, new()));
            Assert.Equal("'Fika' is not a group's id.", await Refused(MailerLiteConnector.AddToGroupOperation, new() { ["subscriber"] = "31", ["group"] = "Fika" }));
            Assert.Equal("'gone' is not active, unsubscribed, unconfirmed, bounced or junk (status).", await Refused(MailerLiteConnector.SubscribersOperation, new() { ["status"] = "gone" }));
            Assert.Equal("Give their e-mail.", await Refused(MailerLiteConnector.CreateSubscriberOperation, new()));
            Assert.Equal("'ada' is not an e-mail (email).", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada" }));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe" }));
            Assert.Equal("'ada @kafe.example.test' is not a subscriber's id or e-mail.", await Refused(MailerLiteConnector.SubscriberOperation, new() { ["subscriber"] = "ada @kafe.example.test" }));
            Assert.Equal("The fields are not a JSON object - {\"name\": \"Ada\", \"company\": \"Kafé\"}.", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["fields"] = "[\"Ada\"]" }));
            Assert.Equal("'name' is not a text, a number or null (fields).", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["fields"] = "{\"name\": {\"first\": \"Ada\"}}" }));
            Assert.Equal("'Fika' is not a group's id (groups).", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["groups"] = "7, Fika" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-13 10:00:00 (opted_in_at).", await Refused(MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test", ["opted_in_at"] = "yesterday" }));
            Assert.Equal("'192.0.2' is not an IP address (optin_ip).", await Refused(MailerLiteConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31", ["optin_ip"] = "192.0.2" }));
            Assert.Equal("Give what to change - their fields, groups, status, times or addresses.", await Refused(MailerLiteConnector.UpdateSubscriberOperation, new() { ["subscriber"] = "31" }));
            Assert.Equal("Give the group's name.", await Refused(MailerLiteConnector.CreateGroupOperation, new()));
            Assert.Equal("The group's name is longer than 255 characters.", await Refused(MailerLiteConnector.UpdateGroupOperation, new() { ["group"] = "7", ["name"] = new string('n', 256) }));
            Assert.Equal("'-size' is not name, total, open_rate, click_rate or created_at, a leading - for descending (sort).", await Refused(MailerLiteConnector.GroupsOperation, new() { ["sort"] = "-size" }));
            Assert.Equal("The MailerLite connection has no API token.", await Refused(MailerLiteConnector.FieldsOperation, new(), credentials: new()));
            Assert.Empty(mailerLite.Requests); // nothing sent
            Assert.Single(await Run(mailerLite, MailerLiteConnector.UpdateGroupOperation, new() { ["group"] = "7", ["name"] = new string('n', 255) })); // 255 is a name

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new MailerLite { Answer = (_, _) => (status, answer) }, MailerLiteConnector.CreateSubscriberOperation, new() { ["email"] = "ada@kafe.example.test" }))).Message;
            Assert.Equal("HTTP 422: The given data was invalid. - email: The email must be a valid email address. It is a role address.; status: The selected status is invalid.", await Failed(HttpStatusCode.UnprocessableEntity, "{\"message\":\"The given data was invalid.\",\"errors\":{\"email\":[\"The email must be a valid email address.\",\"It is a role address.\"],\"status\":[\"The selected status is invalid.\"]}}"));
            Assert.Equal("HTTP 422: email: The email must be a valid email address.", await Failed(HttpStatusCode.UnprocessableEntity, "{\"errors\":{\"email\":\"The email must be a valid email address.\"}}"));
            Assert.Equal("HTTP 401: Unauthenticated.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("MailerLite answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_how_many_groups_the_account_has()
        {
            Task<ConnectionTestResult> Test(MailerLite mailerLite) =>
                new MailerLiteConnector().TestConnectionAsync(Contexts.Test(new StubEgress(mailerLite), "", null, Credentials));
            var account = new MailerLite { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"7\"}],\"meta\":{\"total\":3}}") };
            var tested = await Test(account);
            Assert.Equal((true, "MailerLite: the token is accepted - its account has 3 groups."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/groups?limit=1", "GET", "Bearer ml-token-1"), (account.Requests[0].Url, account.Requests[0].Method.Method, account.Requests[0].Authorization));
            Assert.Equal("MailerLite: the token is accepted - its account has 1 group.", (await Test(new MailerLite { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":[],\"meta\":{\"total\":\"1\"}}") })).Message);
            Assert.Equal("MailerLite: the token is accepted.", (await Test(new MailerLite())).Message);
            var refused = await Test(new MailerLite { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}") });
            Assert.Equal((false, "HTTP 401: Unauthenticated."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "mailerlite"), (new MailerLiteConnector().AuthType, new MailerLiteConnector().ConnectorType));
            Assert.Equal(14, new MailerLiteConnector().Operations.Count);
            Assert.Equal("API token (Integrations > MailerLite API)", Assert.Single(new MailerLiteConnector().CredentialLabels!).Value); // in MailerLite's words
        }
    }
}
