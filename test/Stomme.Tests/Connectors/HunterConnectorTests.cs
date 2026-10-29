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
using Stomme.Modules.Connectors.Hunter;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Hunter (n8n's Hunter node) without a server: every domain, company, name, address, choice and number held to
    /// Hunter's shape before anything is sent; the key in the X-API-KEY header, never in the address; a domain's addresses - a
    /// site's address taken for its domain - each a record, or the domain's one record with them all, filtered by type,
    /// seniority and department, page by page by offset up to the most; a person's address by their names or full name; an
    /// address verified, asked again while Hunter is still verifying and said when it never finishes; the account; Hunter's
    /// error id and details; a test reading the plan and the searches left.
    /// </summary>
    public sealed class HunterConnectorTests
    {
        private const string Api = "https://api.hunter.io/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "kafe-hunter-key" };

        private sealed record Sent(string Url, string? Key);

        private sealed class Hunter(Func<Sent, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("X-API-KEY", out var keys) ? keys.Single() : null);
                Requests.Add(sent);
                var (status, body) = answer(sent);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private static Task<List<ConnectorRecord>> Run(Hunter hunter, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new HunterConnector(TimeSpan.Zero), Contexts.Extract(new StubEgress(hunter), "", operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Email(int i) => $"{{\"value\":\"person{i}@kafe.example\",\"type\":\"personal\",\"confidence\":9{i % 10},\"first_name\":\"Person\",\"last_name\":\"No{i}\",\"position\":null,\"sources\":[{{\"domain\":\"kafe.example\"}}],\"verification\":{{\"status\":\"valid\"}}}}";

        private static string Page(int from, int count, int results) =>
            $"{{\"data\":{{\"domain\":\"kafe.example\",\"disposable\":false,\"webmail\":false,\"accept_all\":true,\"pattern\":\"{{first}}\",\"organization\":\"Kafé Kassan\",\"emails\":[{string.Join(",", Enumerable.Range(from, count).Select(Email))}]}},\"meta\":{{\"results\":{results},\"limit\":100,\"offset\":{from}}}}}";

        [Fact]
        public void It_declares_itself_as_n8ns_hunter_node()
        {
            IConnector connector = new HunterConnector();
            Assert.Equal(("hunter", "Hunter", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "domain_search", "email_finder", "email_verifier", "account" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var hunter = new Hunter(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(hunter, operation, parameters));
                Assert.Empty(hunter.Requests);
                return refusal.Message;
            }

            Assert.Equal("Hunter has no operation 'email_count' - domain_search, email_finder, email_verifier or account.", await Refused("email_count", new()));
            Assert.Equal("Give the domain - kafe.example (domain) - or the company's name (company).", await Refused("domain_search", new()));
            Assert.Equal("'kafe' is not a domain - kafe.example (domain).", await Refused("domain_search", new() { ["domain"] = "kafe" }));
            Assert.Equal("'sometimes' is not true or false (only_emails).", await Refused("domain_search", new() { ["domain"] = "kafe.example", ["only_emails"] = "sometimes" }));
            Assert.Equal("'role' is not personal or generic (type).", await Refused("domain_search", new() { ["domain"] = "kafe.example", ["type"] = "role" }));
            Assert.Equal("'intern' is not one of junior, senior, executive (seniority).", await Refused("domain_search", new() { ["domain"] = "kafe.example", ["seniority"] = "senior, intern" }));
            Assert.Equal("'baristas' is not one of executive, it, finance, management, sales, legal, support, hr, marketing, communication, education, design, health, operations (department).", await Refused("domain_search", new() { ["domain"] = "kafe.example", ["department"] = "baristas" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("domain_search", new() { ["domain"] = "kafe.example", ["max"] = "0" }));
            Assert.Equal("Give the person's first and last name (first_name, last_name) - or their full name (full_name).", await Refused("email_finder", new() { ["domain"] = "kafe.example" }));
            Assert.Equal("Give the person's last name too (last_name).", await Refused("email_finder", new() { ["domain"] = "kafe.example", ["first_name"] = "Ada" }));
            Assert.Equal("Give the address to verify - ada@kafe.example (email).", await Refused("email_verifier", new()));
            Assert.Equal("'ada.kafe.example' is not an email address (email).", await Refused("email_verifier", new() { ["email"] = "ada.kafe.example" }));
        }

        [Fact]
        public async Task A_domains_addresses_are_each_a_record_page_by_page_or_the_domains_one_record()
        {
            var hunter = new Hunter(sent =>
            {
                var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(sent.Url).Query)["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
                var limit = int.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(sent.Url).Query)["limit"]!, System.Globalization.CultureInfo.InvariantCulture);
                return (HttpStatusCode.OK, Page(offset, Math.Max(0, Math.Min(limit, 230 - offset)), 230));
            });
            var all = await Run(hunter, "domain_search", new() { ["domain"] = "https://www.Kafe.example/om-oss", ["type"] = "Personal", ["seniority"] = "senior, executive, senior", ["department"] = "IT,sales", ["max"] = "1000" });
            Assert.Equal(230, all.Count);
            Assert.Equal(new[]
            {
                Api + "/domain-search?domain=www.kafe.example&type=personal&seniority=senior%2Cexecutive&department=it%2Csales&limit=100&offset=0",
                Api + "/domain-search?domain=www.kafe.example&type=personal&seniority=senior%2Cexecutive&department=it%2Csales&limit=100&offset=100",
                Api + "/domain-search?domain=www.kafe.example&type=personal&seniority=senior%2Cexecutive&department=it%2Csales&limit=100&offset=200",
            }, hunter.Requests.Select(sent => sent.Url));
            Assert.All(hunter.Requests, sent => Assert.Equal("kafe-hunter-key", sent.Key)); // in the header ...
            Assert.All(hunter.Requests, sent => Assert.DoesNotContain("kafe-hunter-key", sent.Url, StringComparison.Ordinal)); // ... never in the address
            var first = all[0].Fields;
            Assert.Equal(("person0@kafe.example", 90L, null, "[{\"domain\":\"kafe.example\"}]", "{\"status\":\"valid\"}"), ((string?)first["value"], (long?)first["confidence"], first["position"], (string?)first["sources"], (string?)first["verification"]));

            hunter.Requests.Clear();
            Assert.Equal(7, (await Run(hunter, "domain_search", new() { ["company"] = "Kafé Kassan", ["max"] = "7" })).Count);
            Assert.Equal(Api + "/domain-search?company=Kaf%C3%A9%20Kassan&limit=7&offset=0", hunter.Requests.Single().Url); // a page no larger than what is wanted
            hunter.Requests.Clear();
            Assert.Equal(100, (await Run(hunter, "domain_search", new() { ["domain"] = "kafe.example" })).Count); // n8n's 100 by default
            Assert.Single(hunter.Requests);

            hunter.Requests.Clear();
            var domain = Assert.Single(await Run(hunter, "domain_search", new() { ["domain"] = "kafe.example", ["only_emails"] = "false", ["max"] = "150" })).Fields;
            Assert.Equal(("kafe.example", "Kafé Kassan", true, false), ((string?)domain["domain"], (string?)domain["organization"], (bool?)domain["accept_all"], (bool?)domain["webmail"]));
            Assert.Equal(150, JsonNode.Parse((string)domain["emails"]!)!.AsArray().Count); // both pages' addresses under the domain's one record
            Assert.Equal(new[] { "limit=100&offset=0", "limit=50&offset=100" }, hunter.Requests.Select(sent => sent.Url[(sent.Url.IndexOf("limit", StringComparison.Ordinal))..]));

            var generous = new Hunter(_ => (HttpStatusCode.OK, Page(0, 10, 230))); // more than the page asked for
            Assert.Equal(3, (await Run(generous, "domain_search", new() { ["domain"] = "kafe.example", ["max"] = "3" })).Count);

            var none = new Hunter(_ => (HttpStatusCode.OK, "{\"data\":{\"domain\":\"tyst.example\",\"emails\":[]},\"meta\":{\"results\":0}}"));
            Assert.Empty(await Run(none, "domain_search", new() { ["domain"] = "tyst.example" }));
            Assert.Single(none.Requests);
        }

        [Fact]
        public async Task A_persons_address_is_found_an_address_verified_and_the_account_read()
        {
            var verifying = 0;
            var hunter = new Hunter(sent => new Uri(sent.Url).AbsolutePath switch
            {
                "/v2/email-finder" => (HttpStatusCode.OK, "{\"data\":{\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"email\":\"ada@kafe.example\",\"score\":97,\"accept_all\":false,\"verification\":{\"status\":\"valid\"}},\"meta\":{}}"),
                "/v2/email-verifier" when ++verifying < 3 => (HttpStatusCode.Accepted, "{\"data\":{\"status\":\"unknown\"}}"),
                "/v2/email-verifier" => (HttpStatusCode.OK, "{\"data\":{\"status\":\"valid\",\"score\":100,\"email\":\"ada@kafe.example\",\"disposable\":false,\"webmail\":false,\"mx_records\":true}}"),
                _ => (HttpStatusCode.OK, "{\"data\":{\"email\":\"owner@kafe.example\",\"plan_name\":\"Starter\",\"requests\":{\"searches\":{\"used\":40,\"available\":500},\"verifications\":{\"used\":12,\"available\":1000}}}}"),
            });
            var found = Assert.Single(await Run(hunter, "email_finder", new() { ["domain"] = "kafe.example", ["first_name"] = "Ada", ["last_name"] = "Lovelace" })).Fields;
            Assert.Equal(("ada@kafe.example", 97L, "{\"status\":\"valid\"}"), ((string?)found["email"], (long?)found["score"], (string?)found["verification"]));
            Assert.Equal(Api + "/email-finder?domain=kafe.example&first_name=Ada&last_name=Lovelace", hunter.Requests.Last().Url);
            await Run(hunter, "email_finder", new() { ["company"] = "Kafé Kassan", ["full_name"] = "Ada Lovelace" });
            Assert.Equal(Api + "/email-finder?company=Kaf%C3%A9%20Kassan&full_name=Ada%20Lovelace", hunter.Requests.Last().Url);

            hunter.Requests.Clear();
            var verified = Assert.Single(await Run(hunter, "email_verifier", new() { ["email"] = "ada@kafe.example" })).Fields;
            Assert.Equal(("valid", 100L, true), ((string?)verified["status"], (long?)verified["score"], (bool?)verified["mx_records"]));
            Assert.Equal(3, hunter.Requests.Count); // asked again twice while Hunter was still verifying
            Assert.All(hunter.Requests, sent => Assert.Equal(Api + "/email-verifier?email=ada%40kafe.example", sent.Url));

            var forever = new Hunter(_ => (HttpStatusCode.Accepted, "{\"data\":{}}"));
            Assert.Equal("Hunter was still verifying ada@kafe.example after 5 asks - try again later.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(forever, "email_verifier", new() { ["email"] = "ada@kafe.example" }))).Message);
            Assert.Equal(5, forever.Requests.Count);

            var account = Assert.Single(await Run(hunter, "account", new())).Fields;
            Assert.Equal(("Starter", "{\"searches\":{\"used\":40,\"available\":500},\"verifications\":{\"used\":12,\"available\":1000}}"), ((string?)account["plan_name"], (string?)account["requests"]));
            var tested = await new HunterConnector().TestConnectionAsync(Contexts.Test(new StubEgress(hunter), "", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Hunter answers - Starter plan, 460 searches left."), (tested.Success, tested.Message));
            hunter.Requests.Clear();
            var own = await new HunterConnector().TestConnectionAsync(Contexts.Test(new StubEgress(hunter), "https://api.hunter.io/v2", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, Api + "/account"), (own.Success, hunter.Requests.Single().Url)); // Hunter's own address with its path taken
            var odd = new Hunter(_ => (HttpStatusCode.OK, "[]"));
            Assert.Equal("Hunter answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(odd, "account", new()))).Message);
            var elsewhere = await new HunterConnector().TestConnectionAsync(Contexts.Test(new StubEgress(hunter), "https://api.hunter.io/v1", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "'https://api.hunter.io/v1' is not Hunter's address - its own, https://api.hunter.io."), (elsewhere.Success, elsewhere.Message));

            var refusing = new Hunter(_ => (HttpStatusCode.Unauthorized, "{\"errors\":[{\"id\":\"authentication_failed\",\"code\":401,\"details\":\"No user found for the API key supplied\"}]}"));
            Assert.Equal("Hunter answered HTTP 401 authentication_failed: No user found for the API key supplied - check the API key.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "account", new()))).Message);
            var spent = new Hunter(_ => (HttpStatusCode.Forbidden, "{\"errors\":[{\"id\":\"too_many_requests\",\"code\":403,\"details\":\"You have reached the rate limit.\"}]}")); // its rate; a 429, its plan's quota, is the retry policy's first
            Assert.Equal("Hunter answered HTTP 403 too_many_requests: You have reached the rate limit. - Hunter's rate or the plan's searches are spent; try again later.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(spent, "email_finder", new() { ["domain"] = "kafe.example", ["full_name"] = "Ada Lovelace" }))).Message);
        }
    }
}
