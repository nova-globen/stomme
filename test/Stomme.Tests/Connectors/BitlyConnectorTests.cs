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
using Stomme.Modules.Connectors.Bitly;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Bitly (n8n's Bitly node: a link shortened, got and updated): every call Bitly's API v4 - or the address given -
    /// with the token as a bearer token; an address shortened on a domain, in a group, with a title, tags and deep links; a link
    /// got and updated, given with or without its scheme; a group's links - the user's default group when none is given - page
    /// by page by Bitly's cursor, the size fixed and no bigger than asked, a short page or no next the end, searched, tagged,
    /// archived and created in a time; a link's clicks by unit or their total; the groups and the user; each record's fields as
    /// themselves or their JSON; what could go wrong refused before anything is sent; Bitly's word and description on a
    /// failure; a test naming the token's user and group.
    /// </summary>
    public sealed class BitlyConnectorTests
    {
        private const string Api = "https://api-ssl.bitly.com/v4";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "bitly-token-1" };

        private sealed class Bitly : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Bitly bitly, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new BitlyConnector(), Contexts.Extract(new StubEgress(bitly), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private const string Made = "{\"id\":\"bit.ly/3xYz9\",\"link\":\"https://bit.ly/3xYz9\",\"long_url\":\"https://example.test/café?x=1\",\"title\":\"Café\",\"archived\":false,\"tags\":[\"fika\"],\"created_at\":\"2026-10-13T06:00:00+0000\",\"deeplinks\":[],\"references\":{\"group\":\"https://api-ssl.bitly.com/v4/groups/Bg1\"}}";

        private static string Links(int from, int count, string after, string next) =>
            "{\"links\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"bit.ly/l" + i + "\",\"title\":\"Fika " + i + "\"}")) + "],\"pagination\":{\"search_after\":\"" + after + "\",\"next\":\"" + next + "\",\"size\":100,\"total\":150}}";

        [Fact]
        public async Task A_link_is_shortened_got_and_updated_and_its_clicks_read()
        {
            var bitly = new Bitly
            {
                Answer = (_, url) => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("/clicks/summary", StringComparison.Ordinal) => "{\"total_clicks\":42,\"units\":-1,\"unit\":\"day\"}",
                    _ when url.Contains("/clicks", StringComparison.Ordinal) => "{\"link_clicks\":[{\"clicks\":3,\"date\":\"2026-10-13T00:00:00+0000\"},{\"clicks\":5,\"date\":\"2026-10-12T00:00:00+0000\"}],\"units\":2,\"unit\":\"day\"}",
                    _ => Made,
                }),
            };
            (string, string, string?) Last() => (bitly.Requests[^1].Method.Method, bitly.Requests[^1].Url, bitly.Requests[^1].Body);

            var made = Assert.Single(await Run(bitly, BitlyConnector.CreateOperation, new() { ["long_url"] = "https://example.test/café?x=1", ["domain"] = "Bit.ly", ["group"] = "Bg1", ["title"] = "Café", ["tags"] = "fika, kaffe", ["deeplinks"] = "[{\"app_id\": \"com.example\", \"app_uri_path\": \"/item/1\"}]", ["archived"] = "true" })).Fields;
            Assert.Equal(("POST", $"{Api}/bitlinks", (string?)"{\"long_url\":\"https://example.test/café?x=1\",\"domain\":\"bit.ly\",\"group_guid\":\"Bg1\",\"title\":\"Café\",\"tags\":[\"fika\",\"kaffe\"],\"deeplinks\":[{\"app_id\":\"com.example\",\"app_uri_path\":\"/item/1\"}]}"), Last()); // nothing archived on a create
            Assert.Equal("Bearer bitly-token-1", bitly.Requests[^1].Authorization);
            Assert.Equal(("bit.ly/3xYz9", "https://bit.ly/3xYz9", false, "[\"fika\"]", "{\"group\":\"https://api-ssl.bitly.com/v4/groups/Bg1\"}"), ((string?)made["id"], (string?)made["link"], (bool?)made["archived"], (string?)made["tags"], (string?)made["references"]));
            await Run(bitly, BitlyConnector.CreateOperation, new() { ["long_url"] = "http://example.test" });
            Assert.Equal(("POST", $"{Api}/bitlinks", (string?)"{\"long_url\":\"http://example.test\"}"), Last());

            Assert.Equal("Café", (string?)Assert.Single(await Run(bitly, BitlyConnector.LinkOperation, new() { ["link"] = "https://bit.ly/3xYz9", ["title"] = "x" })).Fields["title"]);
            Assert.Equal(("GET", $"{Api}/bitlinks/bit.ly/3xYz9", (string?)null), Last()); // its scheme dropped
            await Run(bitly, BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3xYz9", ["title"] = "Kafé", ["tags"] = "fika", ["archived"] = "True", ["long_url"] = "https://example.test/ny", ["deeplinks"] = "[{\"app_id\": \"com.example\"}]", ["domain"] = "j.mp" });
            Assert.Equal(("PATCH", $"{Api}/bitlinks/bit.ly/3xYz9", (string?)"{\"title\":\"Kafé\",\"tags\":[\"fika\"],\"archived\":true,\"long_url\":\"https://example.test/ny\",\"deeplinks\":[{\"app_id\":\"com.example\"}]}"), Last()); // no domain on an update
            await Run(bitly, BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3xYz9", ["archived"] = "false" });
            Assert.Equal(("PATCH", $"{Api}/bitlinks/bit.ly/3xYz9", (string?)"{\"archived\":false}"), Last()); // brought back

            var days = await Run(bitly, BitlyConnector.ClicksOperation, new() { ["link"] = "bit.ly/3xYz9", ["unit"] = "Day", ["units"] = "2" });
            Assert.Equal($"{Api}/bitlinks/bit.ly/3xYz9/clicks?unit=day&units=2", Last().Item2);
            Assert.Equal(new[] { ("bit.ly/3xYz9", "2026-10-13T00:00:00+0000", 3L), ("bit.ly/3xYz9", "2026-10-12T00:00:00+0000", 5L) }, days.Select(day => ((string)day.Fields["link"]!, (string)day.Fields["date"]!, (long)day.Fields["clicks"]!)));
            var total = Assert.Single(await Run(bitly, BitlyConnector.ClicksOperation, new() { ["link"] = "bit.ly/3xYz9", ["total"] = "true" })).Fields;
            Assert.Equal($"{Api}/bitlinks/bit.ly/3xYz9/clicks/summary?unit=day&units=-1", Last().Item2); // a day the unit, every one when not said
            Assert.Equal(("bit.ly/3xYz9", 42L, "day"), ((string?)total["link"], (long?)total["clicks"], (string?)total["unit"]));
        }

        [Fact]
        public async Task A_groups_links_are_read_page_by_page_and_the_groups_and_user()
        {
            var bitly = new Bitly
            {
                Answer = (_, url) => (HttpStatusCode.OK, url switch
                {
                    _ when url.EndsWith("/user", StringComparison.Ordinal) => "{\"login\":\"ada\",\"name\":\"Ada\",\"default_group_guid\":\"Bdef1\",\"is_active\":true}",
                    _ when url.EndsWith("/groups", StringComparison.Ordinal) => "{\"groups\":[{\"guid\":\"Bg1\",\"name\":\"Kafé\",\"bsds\":[\"bit.ly\"]}]}",
                    _ when url.Contains("search_after=c2", StringComparison.Ordinal) => Links(101, 50, "c3", "https://api-ssl.bitly.com/v4/groups/Bg1/bitlinks?search_after=c3"),
                    _ => Links(1, 100, "c2/ä", "https://api-ssl.bitly.com/v4/groups/Bg1/bitlinks?search_after=c2"),
                }),
            };

            var links = await Run(bitly, BitlyConnector.LinksOperation, new() { ["group"] = "Bg1", ["query"] = "fika & bröd", ["tags"] = "fika, kaffe", ["archived"] = "Both", ["created_after"] = "2026-10-01T02:00:00+02:00", ["created_before"] = "2026-10-13" });
            Assert.Equal(150, links.Count); // a thousand at most by default
            const string Query = "query=fika%20%26%20br%C3%B6d&tags=fika&tags=kaffe&archived=both&created_after=1790812800&created_before=1791849600&size=100";
            Assert.Equal(new[] { $"{Api}/groups/Bg1/bitlinks?{Query}", $"{Api}/groups/Bg1/bitlinks?{Query}&search_after=c2%2F%C3%A4" }, bitly.Requests.Select(request => request.Url)); // Bitly's cursor, escaped; a short page the end
            Assert.Equal(("bit.ly/l1", "Fika 1"), ((string?)links[0].Fields["id"], (string?)links[0].Fields["title"]));

            bitly.Requests.Clear();
            Assert.Equal(30, (await Run(bitly, BitlyConnector.LinksOperation, new() { ["max"] = "30" }, endpoint: "https://bitly.proxy.example.test/v4/")).Count);
            Assert.Equal(new[] { "https://bitly.proxy.example.test/v4/user", "https://bitly.proxy.example.test/v4/groups/Bdef1/bitlinks?size=30" }, bitly.Requests.Select(request => request.Url)); // the user's default group; a page as small as asked
            bitly.Answer = (_, _) => (HttpStatusCode.OK, Links(1, 100, "c9", ""));
            Assert.Equal(100, (await Run(bitly, BitlyConnector.LinksOperation, new() { ["group"] = "Bg1" })).Count); // no next the end
            bitly.Answer = (_, _) => (HttpStatusCode.OK, Links(1, 100, "", "https://next"));
            Assert.Equal(100, (await Run(bitly, BitlyConnector.LinksOperation, new() { ["group"] = "Bg1" })).Count); // no cursor the end

            bitly.Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/user", StringComparison.Ordinal) ? "{\"login\":\"ada\",\"name\":\"Ada\",\"default_group_guid\":\"Bdef1\",\"is_active\":true}" : "{\"groups\":[{\"guid\":\"Bg1\",\"name\":\"Kafé\",\"bsds\":[\"bit.ly\"]}]}");
            var group = Assert.Single(await Run(bitly, BitlyConnector.GroupsOperation, new())).Fields;
            Assert.Equal(("Bg1", "Kafé", "[\"bit.ly\"]"), ((string?)group["guid"], (string?)group["name"], (string?)group["bsds"]));
            var user = Assert.Single(await Run(bitly, BitlyConnector.UserOperation, new())).Fields;
            Assert.Equal(("ada", "Bdef1", true), ((string?)user["login"], (string?)user["default_group_guid"], (bool?)user["is_active"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_bitlys_word_on_a_failure()
        {
            var bitly = new Bitly();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(bitly, operation, parameters, endpoint, credentials))).Message;

            Assert.Equal("Bitly has no operation 'qr' - create, link, update, links, clicks, groups or user.", await Refused("qr", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(BitlyConnector.LinksOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the link - bit.ly/3xYz9.", await Refused(BitlyConnector.LinkOperation, new()));
            Assert.Equal("'3xYz9' is not a short link - bit.ly/3xYz9.", await Refused(BitlyConnector.ClicksOperation, new() { ["link"] = "3xYz9" }));
            Assert.Equal("'bitly/3xYz9' is not a short link - bit.ly/3xYz9.", await Refused(BitlyConnector.LinkOperation, new() { ["link"] = "bitly/3xYz9" })); // a domain has its dot
            Assert.Equal("'bit.ly/3x/../y' is not a short link - bit.ly/3xYz9.", await Refused(BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3x/../y", ["title"] = "x" }));
            Assert.Equal("'B g1' is not a group's id.", await Refused(BitlyConnector.LinksOperation, new() { ["group"] = "B g1" }));
            Assert.Equal("Give the address to shorten.", await Refused(BitlyConnector.CreateOperation, new()));
            Assert.Equal("'example.test/a' is not an address - https://example.com/a/long/path.", await Refused(BitlyConnector.CreateOperation, new() { ["long_url"] = "example.test/a" }));
            Assert.Equal("'go example.test' is not a domain - bit.ly.", await Refused(BitlyConnector.CreateOperation, new() { ["long_url"] = "https://example.test", ["domain"] = "go example.test" }));
            Assert.Equal("The deep links are not a JSON list of objects - [{\"app_id\": \"com.example\", \"app_uri_path\": \"/item/1\"}].", await Refused(BitlyConnector.CreateOperation, new() { ["long_url"] = "https://example.test", ["deeplinks"] = "[\"x\"]" }));
            Assert.Equal("'yes' is not true or false (archived).", await Refused(BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3xYz9", ["archived"] = "yes" }));
            Assert.Equal("'ftp://example.test' is not an address - https://example.com/a/long/path.", await Refused(BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3xYz9", ["long_url"] = "ftp://example.test" }));
            Assert.Equal("Give what to change - a title, tags, archived, the address or deep links.", await Refused(BitlyConnector.UpdateOperation, new() { ["link"] = "bit.ly/3xYz9" }));
            Assert.Equal("'all' is not on, off or both (archived).", await Refused(BitlyConnector.LinksOperation, new() { ["group"] = "Bg1", ["archived"] = "all" }));
            Assert.Equal("'last week' is not a time - 2026-10-01T00:00:00Z (created_after).", await Refused(BitlyConnector.LinksOperation, new() { ["group"] = "Bg1", ["created_after"] = "last week" }));
            Assert.Equal("'year' is not minute, hour, day, week or month (unit).", await Refused(BitlyConnector.ClicksOperation, new() { ["link"] = "bit.ly/3xYz9", ["unit"] = "year" }));
            Assert.Equal("'0' is not a number above 0 (units).", await Refused(BitlyConnector.ClicksOperation, new() { ["link"] = "bit.ly/3xYz9", ["units"] = "0" }));
            Assert.Equal("The Bitly connection has no access token.", await Refused(BitlyConnector.UserOperation, new(), credentials: new()));
            Assert.Equal("The endpoint 'api-ssl.bitly.com' is not an absolute http(s) URL.", await Refused(BitlyConnector.UserOperation, new(), endpoint: "api-ssl.bitly.com"));
            Assert.Empty(bitly.Requests); // nothing sent

            var nobody = new Bitly { Answer = (_, _) => (HttpStatusCode.OK, "{\"login\":\"ada\"}") };
            Assert.Equal("The token's user has no default group - give the group.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(nobody, BitlyConnector.LinksOperation, new()))).Message);
            Assert.Single(nobody.Requests); // its links never asked for

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Bitly { Answer = (_, _) => (status, answer) }, BitlyConnector.UserOperation, new()))).Message;
            Assert.Equal("HTTP 400: INVALID_ARG_LONG_URL - The value provided is invalid.", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"INVALID_ARG_LONG_URL\",\"resource\":\"bitlinks\",\"description\":\"The value provided is invalid.\",\"errors\":[{\"field\":\"long_url\",\"error_code\":\"invalid\"}]}"));
            Assert.Equal("HTTP 403: FORBIDDEN", await Failed(HttpStatusCode.Forbidden, "{\"message\":\"FORBIDDEN\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Bitly answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_tokens_user_and_group()
        {
            Task<ConnectionTestResult> Test(Bitly bitly) =>
                new BitlyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(bitly), "", null, Credentials));
            var ada = new Bitly { Answer = (_, _) => (HttpStatusCode.OK, "{\"login\":\"ada\",\"name\":\"Ada\",\"default_group_guid\":\"Bdef1\"}") };
            var tested = await Test(ada);
            Assert.Equal((true, "Bitly: the token is Ada's, its group Bdef1."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/user", "Bearer bitly-token-1"), (ada.Requests[0].Url, ada.Requests[0].Authorization));
            Assert.Equal("Bitly: the token is ada's.", (await Test(new Bitly { Answer = (_, _) => (HttpStatusCode.OK, "{\"login\":\"ada\"}") })).Message);
            Assert.Equal("Bitly: the token is a user's.", (await Test(new Bitly { Answer = (_, _) => (HttpStatusCode.OK, "{}") })).Message);
            var refused = await Test(new Bitly { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"message\":\"FORBIDDEN\"}") });
            Assert.Equal((false, "HTTP 403: FORBIDDEN"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "bitly"), (new BitlyConnector().AuthType, new BitlyConnector().ConnectorType));
            Assert.Equal(7, new BitlyConnector().Operations.Count);
        }
    }
}
