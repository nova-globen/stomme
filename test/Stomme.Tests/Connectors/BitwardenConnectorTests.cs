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
using Stomme.Modules.Connectors.Bitwarden;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Bitwarden (n8n's Bitwarden node: an organization's collections, events, groups and members): a token from the
    /// organization's client credentials at Bitwarden's identity endpoint - the US cloud's, the EU cloud's or a self-hosted
    /// server's - the public API with it as a bearer; lists read whole and no more than asked, the events page by page by
    /// Bitwarden's continuation token; an update read first and what is not given kept, an access list's settings with it;
    /// groups' and members' ids read and set; a member invited with a role; what could go wrong refused before anything is
    /// sent - not even the token asked for; Bitwarden's message and errors on a failure; a test naming the member count.
    /// </summary>
    public sealed class BitwardenConnectorTests
    {
        private const string Api = "https://api.bitwarden.com/public";

        private const string TokenUrl = "https://identity.bitwarden.com/connect/token";

        private const string Id1 = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

        private const string Id2 = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "organization.kafe-1", ["client-secret"] = "secret-1" };

        private sealed class Bitwarden : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[]}");

            public Func<string, (HttpStatusCode, string)> Token { get; set; } = _ => (HttpStatusCode.OK, "{\"access_token\":\"bw-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.EndsWith("/token", StringComparison.Ordinal) ? Token(url) : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Bitwarden bitwarden, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new BitwardenConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(bitwarden), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string List(string kind, int count, string? next = null) =>
            "{\"object\":\"list\",\"data\":[" + string.Join(",", Enumerable.Range(1, count).Select(i => "{\"object\":\"" + kind + "\",\"id\":\"" + kind + "-" + i + "\",\"name\":\"Kafé " + i + "\",\"type\":2,\"collections\":null}")) + "],\"continuationToken\":" + (next is null ? "null" : "\"" + next + "\"") + "}";

        private const string Group = "{\"object\":\"group\",\"id\":\"" + Id1 + "\",\"name\":\"Kassa\",\"accessAll\":true,\"externalId\":\"ext-7\",\"collections\":[{\"id\":\"" + Id2 + "\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}]}";

        private const string Member = "{\"object\":\"member\",\"id\":\"" + Id1 + "\",\"email\":\"ada@kafe.example.test\",\"type\":4,\"accessAll\":false,\"externalId\":null,\"permissions\":{\"accessEventLogs\":true},\"collections\":[{\"id\":\"" + Id2 + "\",\"readOnly\":false,\"hidePasswords\":false,\"manage\":true}]}";

        [Fact]
        public async Task Lists_are_read_no_more_than_asked_and_events_page_by_page_by_bitwardens_token()
        {
            var bitwarden = new Bitwarden
            {
                Answer = (_, url) => url.Contains("/events", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, url.Contains("continuationToken=t%2B1", StringComparison.Ordinal) ? List("event", 2) : List("event", 3, "t+1"))
                    : (HttpStatusCode.OK, List(url.Contains("/collections", StringComparison.Ordinal) ? "collection" : url.Contains("/groups", StringComparison.Ordinal) ? "group" : "member", 3, "more")), // only the events page
            };

            var members = await Run(bitwarden, BitwardenConnector.MembersOperation, new() { ["max"] = "2" });
            Assert.Equal(2, members.Count); // no more than asked
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=organization.kafe-1&client_secret=secret-1&scope=api.organization"), (bitwarden.Requests[0].Method.Method, bitwarden.Requests[0].Url, bitwarden.Requests[0].Body));
            Assert.Equal(("GET", $"{Api}/members", "Bearer bw-token"), (bitwarden.Requests[1].Method.Method, bitwarden.Requests[1].Url, bitwarden.Requests[1].Authorization));
            Assert.Equal(("member-1", "Kafé 1", 2L), ((string?)members[0].Fields["id"], (string?)members[0].Fields["name"], (long?)members[0].Fields["type"]));
            Assert.Null(members[0].Fields["collections"]);
            Assert.False(members[0].Fields.ContainsKey("object"));
            Assert.Equal(3, (await Run(bitwarden, null!, new())).Count); // no operation: the members
            Assert.Equal($"{Api}/members", bitwarden.Requests[^1].Url);
            Assert.Equal(3, (await Run(bitwarden, BitwardenConnector.CollectionsOperation, new())).Count);
            Assert.Equal($"{Api}/collections", bitwarden.Requests[^1].Url);
            Assert.Equal(3, (await Run(bitwarden, BitwardenConnector.GroupsOperation, new())).Count);
            Assert.Equal($"{Api}/groups", bitwarden.Requests[^1].Url);

            bitwarden.Requests.Clear();
            var events = await Run(bitwarden, BitwardenConnector.EventsOperation, new() { ["start"] = "2026-10-01T02:00:00+02:00", ["end"] = "2026-10-14T00:00:00Z", ["member"] = Id1.ToUpperInvariant(), ["item"] = Id2 });
            Assert.Equal(5, events.Count); // both pages, no token the end
            Assert.Equal(new[] { $"{Api}/events?start=2026-10-01T00%3A00%3A00Z&end=2026-10-14T00%3A00%3A00Z&actingUserId={Id1}&itemId={Id2}", $"{Api}/events?start=2026-10-01T00%3A00%3A00Z&end=2026-10-14T00%3A00%3A00Z&actingUserId={Id1}&itemId={Id2}&continuationToken=t%2B1" }, bitwarden.Requests.Skip(1).Select(request => request.Url));
            Assert.Equal(4, (await Run(bitwarden, BitwardenConnector.EventsOperation, new() { ["max"] = "4" })).Count);
            var again = new Bitwarden { Answer = (_, _) => (HttpStatusCode.OK, List("event", 1, "same")) };
            Assert.Equal(2, (await Run(again, BitwardenConnector.EventsOperation, new())).Count); // a token given twice the end
            var endless = new Bitwarden { Answer = (_, _) => (HttpStatusCode.OK, List("event", 100, Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, BitwardenConnector.EventsOperation, new())).Count); // 1,000 at most when no max is given
            var empty = new Bitwarden { Answer = (_, _) => (HttpStatusCode.OK, List("event", 0, "more")) };
            Assert.Empty(await Run(empty, BitwardenConnector.EventsOperation, new()));
            Assert.Equal(2, empty.Requests.Count); // the token, one page: an empty page the end
        }

        [Fact]
        public async Task Groups_members_and_collections_are_kept_what_is_not_given_with_them()
        {
            var bitwarden = new Bitwarden
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.EndsWith("/member-ids", StringComparison.Ordinal) || u.EndsWith("/group-ids", StringComparison.Ordinal) => (HttpStatusCode.OK, $"[\"{Id1}\",\"{Id2}\"]"),
                    ("PUT", var u) when u.EndsWith("-ids", StringComparison.Ordinal) => (HttpStatusCode.OK, ""),
                    ("DELETE", _) => (HttpStatusCode.OK, ""),
                    (_, var u) when u.Contains("/members", StringComparison.Ordinal) => (HttpStatusCode.OK, Member),
                    (_, var u) when u.Contains("/collections", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"object\":\"collection\",\"id\":\"" + Id1 + "\",\"externalId\":\"kassa\",\"groups\":[{\"id\":\"" + Id2 + "\",\"readOnly\":true,\"hidePasswords\":false,\"manage\":false}]}"),
                    _ => (HttpStatusCode.OK, Group),
                },
            };
            (string, string, string?) Last() => (bitwarden.Requests[^1].Method.Method, bitwarden.Requests[^1].Url, bitwarden.Requests[^1].Body);
            (string, string, string?) Before() => (bitwarden.Requests[^2].Method.Method, bitwarden.Requests[^2].Url, bitwarden.Requests[^2].Body);

            var made = Assert.Single(await Run(bitwarden, BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Fika & bröd", ["access_all"] = "False", ["collections"] = $"{Id1}, {Id2}:read-only", ["external_id"] = "ext-7" })).Fields;
            Assert.Equal(("POST", $"{Api}/groups", (string?)$"{{\"name\":\"Fika & bröd\",\"accessAll\":false,\"externalId\":\"ext-7\",\"collections\":[{{\"id\":\"{Id1}\",\"readOnly\":false}},{{\"id\":\"{Id2}\",\"readOnly\":true}}]}}"), Last());
            Assert.Equal((Id1, "Kassa", "[{\"id\":\"" + Id2 + "\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}]"), ((string?)made["id"], (string?)made["name"], (string?)made["collections"]));
            await Run(bitwarden, BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Kassa" });
            Assert.Equal("{\"name\":\"Kassa\",\"accessAll\":false,\"externalId\":null,\"collections\":[]}", Last().Item3);
            await Run(bitwarden, BitwardenConnector.UpdateGroupOperation, new() { ["id"] = Id1, ["name"] = "Kassan" });
            Assert.Equal(("GET", $"{Api}/groups/{Id1}", (string?)null), Before()); // read first
            Assert.Equal(("PUT", $"{Api}/groups/{Id1}", (string?)$"{{\"name\":\"Kassan\",\"accessAll\":true,\"externalId\":\"ext-7\",\"collections\":[{{\"id\":\"{Id2}\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}}]}}"), Last()); // the rest kept, its access whole
            await Run(bitwarden, BitwardenConnector.UpdateGroupOperation, new() { ["id"] = Id1, ["access_all"] = "true", ["collections"] = Id1, ["external_id"] = "ext-8" });
            Assert.Equal($"{{\"name\":\"Kassa\",\"accessAll\":true,\"externalId\":\"ext-8\",\"collections\":[{{\"id\":\"{Id1}\",\"readOnly\":false}}]}}", Last().Item3);
            await Run(bitwarden, BitwardenConnector.UpdateGroupOperation, new() { ["id"] = Id1, ["external_id"] = "ext-9" });
            Assert.Equal($"{{\"name\":\"Kassa\",\"accessAll\":true,\"externalId\":\"ext-9\",\"collections\":[{{\"id\":\"{Id2}\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}}]}}", Last().Item3); // an external id alone is something to change
            Assert.Equal("Kassa", (string?)Assert.Single(await Run(bitwarden, BitwardenConnector.GroupOperation, new() { ["id"] = Id1 })).Fields["name"]);
            Assert.Equal(("GET", $"{Api}/groups/{Id1}", (string?)null), Last());
            Assert.Equal(new[] { Id1, Id2 }, (await Run(bitwarden, BitwardenConnector.GroupMembersOperation, new() { ["id"] = Id1 })).Select(record => (string?)record.Fields["member"]));
            Assert.Equal(("GET", $"{Api}/groups/{Id1}/member-ids", (string?)null), Last());
            var set = Assert.Single(await Run(bitwarden, BitwardenConnector.SetGroupMembersOperation, new() { ["id"] = Id1, ["members"] = $"{Id1},{Id2}" })).Fields;
            Assert.Equal(("PUT", $"{Api}/groups/{Id1}/member-ids", (string?)$"{{\"memberIds\":[\"{Id1}\",\"{Id2}\"]}}"), Last());
            Assert.Equal((Id1, $"[\"{Id1}\",\"{Id2}\"]", true), ((string?)set["id"], (string?)set["members"], (bool?)set["set"]));
            await Run(bitwarden, BitwardenConnector.SetGroupMembersOperation, new() { ["id"] = Id1, ["members"] = "" });
            Assert.Equal("{\"memberIds\":[]}", Last().Item3); // empty: none
            var deleted = Assert.Single(await Run(bitwarden, BitwardenConnector.DeleteGroupOperation, new() { ["id"] = Id1 })).Fields;
            Assert.Equal(("DELETE", $"{Api}/groups/{Id1}", (string?)null), Last());
            Assert.Equal((Id1, true), ((string?)deleted["id"], (bool?)deleted["deleted"]));

            await Run(bitwarden, BitwardenConnector.InviteMemberOperation, new() { ["email"] = "cy@kafe.example.test", ["role"] = "Admin", ["collections"] = $"{Id2}:Read-Only" });
            Assert.Equal(("POST", $"{Api}/members", (string?)$"{{\"type\":1,\"accessAll\":false,\"externalId\":null,\"collections\":[{{\"id\":\"{Id2}\",\"readOnly\":true}}],\"email\":\"cy@kafe.example.test\"}}"), Last());
            await Run(bitwarden, BitwardenConnector.InviteMemberOperation, new() { ["email"] = "dag@kafe.example.test", ["role"] = "manager" });
            Assert.StartsWith("{\"type\":3,", Last().Item3);
            await Run(bitwarden, BitwardenConnector.InviteMemberOperation, new() { ["email"] = "eva@kafe.example.test", ["role"] = "custom" });
            Assert.StartsWith("{\"type\":4,", Last().Item3); // each role its own number
            await Run(bitwarden, BitwardenConnector.InviteMemberOperation, new() { ["email"] = "bo@kafe.example.test" });
            Assert.Equal("{\"type\":2,\"accessAll\":false,\"externalId\":null,\"collections\":[],\"email\":\"bo@kafe.example.test\"}", Last().Item3); // a user by default
            await Run(bitwarden, BitwardenConnector.UpdateMemberOperation, new() { ["id"] = Id1, ["role"] = "3" });
            Assert.Equal(("GET", $"{Api}/members/{Id1}", (string?)null), Before());
            Assert.Equal(("PUT", $"{Api}/members/{Id1}", (string?)$"{{\"type\":3,\"accessAll\":false,\"externalId\":null,\"collections\":[{{\"id\":\"{Id2}\",\"readOnly\":false,\"hidePasswords\":false,\"manage\":true}}],\"permissions\":{{\"accessEventLogs\":true}}}}"), Last()); // its access and permissions kept
            await Run(bitwarden, BitwardenConnector.UpdateMemberOperation, new() { ["id"] = Id1, ["external_id"] = "ext-9" });
            Assert.Equal($"{{\"type\":4,\"accessAll\":false,\"externalId\":\"ext-9\",\"collections\":[{{\"id\":\"{Id2}\",\"readOnly\":false,\"hidePasswords\":false,\"manage\":true}}],\"permissions\":{{\"accessEventLogs\":true}}}}", Last().Item3);
            Assert.Equal("ada@kafe.example.test", (string?)Assert.Single(await Run(bitwarden, BitwardenConnector.MemberOperation, new() { ["id"] = Id1 })).Fields["email"]);
            Assert.Equal(new[] { Id1, Id2 }, (await Run(bitwarden, BitwardenConnector.MemberGroupsOperation, new() { ["id"] = Id1 })).Select(record => (string?)record.Fields["group"]));
            Assert.Equal(("GET", $"{Api}/members/{Id1}/group-ids", (string?)null), Last());
            Assert.Equal($"[\"{Id2}\"]", (string?)Assert.Single(await Run(bitwarden, BitwardenConnector.SetMemberGroupsOperation, new() { ["id"] = Id1, ["groups"] = Id2 })).Fields["groups"]);
            Assert.Equal(("PUT", $"{Api}/members/{Id1}/group-ids", (string?)$"{{\"groupIds\":[\"{Id2}\"]}}"), Last());
            Assert.True((bool?)Assert.Single(await Run(bitwarden, BitwardenConnector.DeleteMemberOperation, new() { ["id"] = Id1 })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/members/{Id1}", (string?)null), Last());

            Assert.Equal("kassa", (string?)Assert.Single(await Run(bitwarden, BitwardenConnector.CollectionOperation, new() { ["id"] = Id1 })).Fields["externalId"]);
            await Run(bitwarden, BitwardenConnector.UpdateCollectionOperation, new() { ["id"] = Id1, ["groups"] = $"{Id1}:read-only" });
            Assert.Equal(("PUT", $"{Api}/collections/{Id1}", (string?)$"{{\"externalId\":\"kassa\",\"groups\":[{{\"id\":\"{Id1}\",\"readOnly\":true}}]}}"), Last()); // its external id kept
            await Run(bitwarden, BitwardenConnector.UpdateCollectionOperation, new() { ["id"] = Id1, ["external_id"] = "fika" });
            Assert.Equal($"{{\"externalId\":\"fika\",\"groups\":[{{\"id\":\"{Id2}\",\"readOnly\":true,\"hidePasswords\":false,\"manage\":false}}]}}", Last().Item3); // its groups kept
            Assert.True((bool?)Assert.Single(await Run(bitwarden, BitwardenConnector.DeleteCollectionOperation, new() { ["id"] = Id1 })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/collections/{Id1}", (string?)null), Last());
            Assert.All(bitwarden.Requests.Where(request => !request.Url.EndsWith("/connect/token", StringComparison.Ordinal)), request => Assert.Equal("Bearer bw-token", request.Authorization));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_bitwardens_words_on_a_failure()
        {
            var bitwarden = new Bitwarden();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(bitwarden, operation, parameters, credentials: credentials))).Message;

            Assert.StartsWith("Bitwarden has no operation 'items' - collections, collection, update_collection,", await Refused("items", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(BitwardenConnector.MembersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the collection's id (id).", await Refused(BitwardenConnector.CollectionOperation, new()));
            Assert.Equal("Give the group's id (id).", await Refused(BitwardenConnector.DeleteGroupOperation, new()));
            Assert.Equal("Give the member's id (id).", await Refused(BitwardenConnector.MemberGroupsOperation, new()));
            Assert.Equal("'kassa' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (id).", await Refused(BitwardenConnector.DeleteCollectionOperation, new() { ["id"] = "kassa" }));
            Assert.Equal("'{" + Id1 + "}' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (id).", await Refused(BitwardenConnector.MemberOperation, new() { ["id"] = "{" + Id1 + "}" }));
            Assert.Equal("'2026-10-01' is not a moment with its offset - 2026-10-01T00:00:00Z (start).", await Refused(BitwardenConnector.EventsOperation, new() { ["start"] = "2026-10-01" }));
            Assert.Equal("'2026-10-01T00:00:00' is not a moment with its offset - 2026-10-01T00:00:00Z (end).", await Refused(BitwardenConnector.EventsOperation, new() { ["end"] = "2026-10-01T00:00:00" }));
            Assert.Equal("'ada' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (member).", await Refused(BitwardenConnector.EventsOperation, new() { ["member"] = "ada" }));
            Assert.Equal("'kassa' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (item).", await Refused(BitwardenConnector.EventsOperation, new() { ["item"] = "kassa" }));
            Assert.Equal("Give the group's name (name).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["access_all"] = "true" }));
            Assert.Equal("'" + new string('k', 101) + "' is not a group's name - 100 characters at most (name).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["name"] = new string('k', 101) }));
            Assert.Equal("Give what to change - its name, all collections, its collections or external id.", await Refused(BitwardenConnector.UpdateGroupOperation, new() { ["id"] = Id1 }));
            Assert.Equal("'kassa' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (collections).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Kassa", ["collections"] = $"{Id1}, kassa" }));
            Assert.Equal("'{" + Id1 + "}' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 (collections).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Kassa", ["collections"] = "{" + Id1 + "}" }));
            Assert.Equal("'yes' is not true or false (access_all).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Kassa", ["access_all"] = "yes" }));
            Assert.Equal("'fika\u0001' is not an external id - 300 characters at most, no control characters (external_id).", await Refused(BitwardenConnector.CreateGroupOperation, new() { ["name"] = "Kassa", ["external_id"] = "fika\u0001" }));
            Assert.Equal("Give the members - comma-separated ids; empty for none (members).", await Refused(BitwardenConnector.SetGroupMembersOperation, new() { ["id"] = Id1 }));
            Assert.Equal("A member has no read-only - give its id alone (members).", await Refused(BitwardenConnector.SetGroupMembersOperation, new() { ["id"] = Id1, ["members"] = $"{Id2}:read-only" }));
            Assert.Equal("Give the groups - comma-separated ids; empty for none (groups).", await Refused(BitwardenConnector.SetMemberGroupsOperation, new() { ["id"] = Id1 }));
            Assert.Equal("Give what to change - its groups or external id.", await Refused(BitwardenConnector.UpdateCollectionOperation, new() { ["id"] = Id1 }));
            Assert.Equal("Give the member's e-mail (email).", await Refused(BitwardenConnector.InviteMemberOperation, new() { ["role"] = "user" }));
            Assert.Equal("'cy@kafe' is not an e-mail (email).", await Refused(BitwardenConnector.InviteMemberOperation, new() { ["email"] = "cy@kafe" }));
            Assert.Equal("'cy,bo@kafe.example.test' is not an e-mail (email).", await Refused(BitwardenConnector.InviteMemberOperation, new() { ["email"] = "cy,bo@kafe.example.test" }));
            Assert.Equal("'boss' is not owner, admin, user, manager or custom (role).", await Refused(BitwardenConnector.InviteMemberOperation, new() { ["email"] = "cy@kafe.example.test", ["role"] = "boss" }));
            Assert.Equal("'5' is not owner, admin, user, manager or custom (role).", await Refused(BitwardenConnector.UpdateMemberOperation, new() { ["id"] = Id1, ["role"] = "5" }));
            Assert.Equal("Give what to change - their role, all collections, their collections or external id.", await Refused(BitwardenConnector.UpdateMemberOperation, new() { ["id"] = Id1 }));
            Assert.Equal("The client id is not an organization's - organization.… from Admin Console > Settings > Organization info > API key.", await Refused(BitwardenConnector.MembersOperation, new(), new() { ["client-id"] = "user.ada", ["client-secret"] = "secret-1" }));
            Assert.Equal("The Bitwarden connection has no client id.", await Refused(BitwardenConnector.MembersOperation, new(), new() { ["client-secret"] = "secret-1" }));
            Assert.Equal("The Bitwarden connection has no client secret.", await Refused(BitwardenConnector.MembersOperation, new(), new() { ["client-id"] = "organization.kafe-1" }));
            Assert.Empty(bitwarden.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Bitwarden { Answer = (_, _) => (status, answer) }, BitwardenConnector.MemberOperation, new() { ["id"] = Id1 }))).Message;
            Assert.Equal("HTTP 404: Resource not found.", await Failed(HttpStatusCode.NotFound, "{\"object\":\"error\",\"message\":\"Resource not found.\",\"errors\":null}"));
            Assert.Equal("HTTP 400: The request's model state is invalid.; The Email field is not a valid e-mail address.; Type is required.", await Failed(HttpStatusCode.BadRequest, "{\"object\":\"error\",\"message\":\"The request's model state is invalid.\",\"errors\":{\"Email\":[\"The Email field is not a valid e-mail address.\"],\"Type\":[\"Type is required.\"]}}")); // its errors beside
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Bitwarden answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            var refusedToken = new Bitwarden { Token = _ => (HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}") };
            Assert.Contains("invalid_client", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusedToken, BitwardenConnector.MembersOperation, new()))).Message);
            Assert.Single(refusedToken.Requests); // no token, no call
        }

        [Fact]
        public async Task A_test_names_the_member_count_on_each_cloud_and_a_server_of_ones_own()
        {
            Task<ConnectionTestResult> Test(Bitwarden bitwarden, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                new BitwardenConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(bitwarden), endpoint, null, credentials ?? Credentials));
            var us = new Bitwarden { Answer = (_, _) => (HttpStatusCode.OK, List("member", 3)) };
            var tested = await Test(us);
            Assert.Equal((true, "Bitwarden: the organization has 3 members."), (tested.Success, tested.Message));
            Assert.Equal(new[] { TokenUrl, $"{Api}/members" }, us.Requests.Select(request => request.Url));
            var eu = new Bitwarden { Answer = (_, _) => (HttpStatusCode.OK, List("member", 1)) };
            Assert.Equal("Bitwarden: the organization has 1 member.", (await Test(eu, "https://api.bitwarden.eu/")).Message);
            Assert.Equal(new[] { "https://identity.bitwarden.eu/connect/token", "https://api.bitwarden.eu/public/members" }, eu.Requests.Select(request => request.Url));
            var own = new Bitwarden();
            Assert.Equal("Bitwarden: the organization has 0 members.", (await Test(own, "https://vault.kafe.example.test")).Message);
            Assert.Equal(new[] { "https://vault.kafe.example.test/identity/connect/token", "https://vault.kafe.example.test/api/public/members" }, own.Requests.Select(request => request.Url));
            var ownApi = new Bitwarden();
            await Test(ownApi, "https://vault.kafe.example.test/api", new() { ["client-id"] = "organization.kafe-1", ["client-secret"] = "secret-1", ["token-url"] = "https://login.kafe.example.test/token" });
            Assert.Equal(new[] { "https://login.kafe.example.test/token", "https://vault.kafe.example.test/api/public/members" }, ownApi.Requests.Select(request => request.Url)); // its api path given once, a token endpoint of its own
            var refused = await Test(new Bitwarden { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Unauthorized.\"}") });
            Assert.Equal((false, "HTTP 401: Unauthorized."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "bitwarden"), (new BitwardenConnector().AuthType, new BitwardenConnector().ConnectorType));
            Assert.Equal(19, new BitwardenConnector().Operations.Count);
            Assert.Equal("Client id (Admin Console > Settings > Organization info > API key - organization.…)", new BitwardenConnector().CredentialLabels!["client-id"]); // in Bitwarden's words
        }
    }
}
