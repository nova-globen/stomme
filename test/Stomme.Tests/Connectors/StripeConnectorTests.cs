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
using Stomme.Modules.Connectors.Stripe;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Stripe (n8n's Stripe node: customers listed, got, created, updated and deleted, charges listed, the balance got): a
    /// secret key as the bearer, a form as the body; the customers - those with an address - and the charges - a customer's - read
    /// page by page by starting_after up to the most asked for, each a plain record, its time in UTC; a customer made with its
    /// metadata as metadata[key], read, changed and deleted; the balance one record per currency; a customer id, an address, a
    /// line break, metadata that is not a JSON object of fit keys and an update of nothing refused before anything is sent;
    /// Stripe's refusal its message; a test saying whether the key is live.
    /// </summary>
    public sealed class StripeConnectorTests
    {
        private const string Api = "https://stripe.example.test/v1";

        private static readonly Dictionary<string, string> Key = new() { ["token"] = "sk_test_1" };

        private sealed class StripeApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string? ContentType)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body, request.Content?.Headers.ContentType?.MediaType));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(StripeApi stripe, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(stripe), Api, operation, parameters, credentials: Key);

        private static string CustomerJson(string id, string email) =>
            "{\"id\":\"" + id + "\",\"object\":\"customer\",\"email\":\"" + email + "\",\"name\":\"Ada Lovelace\",\"phone\":\"+46701740605\",\"description\":\"VIP\",\"balance\":-500,\"currency\":\"sek\",\"delinquent\":false,\"created\":1790848800,\"metadata\":{\"order\":\"1001\"}}";

        private static string ChargeJson(string id) =>
            "{\"id\":\"" + id + "\",\"object\":\"charge\",\"amount\":12000,\"amount_captured\":12000,\"amount_refunded\":0,\"currency\":\"sek\",\"status\":\"succeeded\",\"paid\":true,\"refunded\":false,\"customer\":\"cus_A1\",\"description\":\"Order 1001\",\"receipt_email\":\"ada@example.com\",\"payment_intent\":\"pi_1\",\"created\":1790848800}";

        [Fact]
        public async Task Customers_and_charges_are_read_page_by_page_by_starting_after_up_to_the_most_asked_for()
        {
            var stripe = new StripeApi
            {
                Answer = (_, url, _) => url.Contains("starting_after=cus_A2", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[" + CustomerJson("cus_A3", "c@example.com") + "," + CustomerJson("cus_A4", "d@example.com") + "],\"has_more\":true}")
                    : (HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[" + CustomerJson("cus_A1", "a@example.com") + "," + CustomerJson("cus_A2", "b@example.com") + "],\"has_more\":true}"),
            };
            var records = await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.CustomersOperation, new() { ["email"] = "ada+vip@example.com", ["max"] = "3" }));

            Assert.Equal(new[] { "cus_A1", "cus_A2", "cus_A3" }, records.Select(record => (string?)record.Fields["id"])); // the most asked for: no page more
            Assert.Equal(2, stripe.Requests.Count);
            Assert.Equal($"{Api}/customers?limit=3&email=ada%2Bvip%40example.com", stripe.Requests[0].Url);
            Assert.Equal($"{Api}/customers?limit=1&email=ada%2Bvip%40example.com&starting_after=cus_A2", stripe.Requests[1].Url); // after the page's last
            Assert.Equal("Bearer sk_test_1", stripe.Requests[0].Authorization);
            var first = records[0].Fields;
            Assert.Equal(("a@example.com", "Ada Lovelace", -500L, "sek", "2026-10-01T10:00:00Z", "{\"order\":\"1001\"}", false),
                ((string?)first["email"], (string?)first["name"], (long?)first["balance"], (string?)first["currency"], (string?)first["created"], (string?)first["metadata"], (bool?)first["delinquent"]));

            var charges = new StripeApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[" + ChargeJson("ch_1") + "],\"has_more\":false}") };
            var charge = Assert.Single(await Contexts.DrainAsync(new StripeConnector(), Context(charges, StripeConnector.ChargesOperation, new() { ["customer"] = "cus_A1" }))).Fields;
            Assert.Equal($"{Api}/charges?limit=100&customer=cus_A1", Assert.Single(charges.Requests).Url); // no more: no page after it
            Assert.Equal((12000L, "succeeded", true, "cus_A1", "pi_1"), ((long?)charge["amount"], (string?)charge["status"], (bool?)charge["paid"], (string?)charge["customer"], (string?)charge["payment_intent"]));
        }

        [Fact]
        public async Task A_customer_is_made_with_its_metadata_read_changed_and_deleted_and_the_balance_is_one_record_per_currency()
        {
            var stripe = new StripeApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"id\":\"cus_A1\",\"object\":\"customer\",\"deleted\":true}"),
                    ("GET", _) when url.EndsWith("/balance", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"object\":\"balance\",\"livemode\":false,\"available\":[{\"amount\":12000,\"currency\":\"sek\"},{\"amount\":500,\"currency\":\"eur\"}],\"pending\":[{\"amount\":3000,\"currency\":\"sek\"}]}"),
                    _ => (HttpStatusCode.OK, CustomerJson("cus_A1", "ada@example.com")),
                },
            };

            var made = await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.CreateCustomerOperation, new()
            {
                ["email"] = "ada@example.com", ["name"] = "Ada Lovelace", ["metadata"] = "{\"order\": \"1001\", \"tier\": 2}",
            }));
            Assert.Equal((HttpMethod.Post, $"{Api}/customers", "application/x-www-form-urlencoded"), (stripe.Requests[0].Method, stripe.Requests[0].Url, stripe.Requests[0].ContentType));
            Assert.Equal("email=ada%40example.com&name=Ada+Lovelace&metadata%5Border%5D=1001&metadata%5Btier%5D=2", stripe.Requests[0].Body);
            Assert.Equal("cus_A1", Assert.Single(made).Fields["id"]);

            await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.GetCustomerOperation, new() { ["customer"] = "cus_A1" }));
            Assert.Equal((HttpMethod.Get, $"{Api}/customers/cus_A1"), (stripe.Requests[1].Method, stripe.Requests[1].Url));

            await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.UpdateCustomerOperation, new() { ["customer"] = "cus_A1", ["phone"] = "+46701740605" }));
            Assert.Equal((HttpMethod.Post, $"{Api}/customers/cus_A1", "phone=%2B46701740605"), (stripe.Requests[2].Method, stripe.Requests[2].Url, stripe.Requests[2].Body));

            var removed = await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.DeleteCustomerOperation, new() { ["customer"] = "cus_A1" }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/customers/cus_A1"), (stripe.Requests[3].Method, stripe.Requests[3].Url));
            Assert.True((bool?)Assert.Single(removed).Fields["deleted"]);

            var balance = await Contexts.DrainAsync(new StripeConnector(), Context(stripe, StripeConnector.BalanceOperation, new()));
            Assert.Equal(new (string?, long, long)[] { ("eur", 500L, 0L), ("sek", 12000L, 3000L) }, balance.Select(record => ((string?)record.Fields["currency"], (long?)record.Fields["available"] ?? -1, (long?)record.Fields["pending"] ?? -1)));
        }

        [Fact]
        public async Task What_could_reach_the_path_or_mislead_stripe_is_refused_before_anything_is_sent_and_stripes_refusal_is_its_message()
        {
            var stripe = new StripeApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new StripeConnector(), Context(stripe, operation, parameters)))).Message;

            Assert.StartsWith("'cus_A1/../x' is not a Stripe customer's id", await Refused(StripeConnector.GetCustomerOperation, new() { ["customer"] = "cus_A1/../x" }));
            Assert.StartsWith("'ch_1' is not a Stripe customer's id", await Refused(StripeConnector.ChargesOperation, new() { ["customer"] = "ch_1" }));
            Assert.Equal("'ada at example' is not an address.", await Refused(StripeConnector.CreateCustomerOperation, new() { ["email"] = "ada at example" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(StripeConnector.CreateCustomerOperation, new() { ["name"] = "Ada\nBcc" }));
            Assert.Equal("The metadata is not JSON - {\"order\": \"1001\"}.", await Refused(StripeConnector.CreateCustomerOperation, new() { ["metadata"] = "order=1001" }));
            Assert.StartsWith("'my key' is not a metadata key", await Refused(StripeConnector.CreateCustomerOperation, new() { ["metadata"] = "{\"my key\": \"x\"}" }));
            Assert.StartsWith("Give something to change", await Refused(StripeConnector.UpdateCustomerOperation, new() { ["customer"] = "cus_A1" }));
            Assert.Empty(stripe.Requests);

            var refusing = new StripeApi { Answer = (_, _, _) => (HttpStatusCode.NotFound, "{\"error\":{\"code\":\"resource_missing\",\"message\":\"No such customer: 'cus_X'\",\"type\":\"invalid_request_error\"}}") };
            Assert.Equal("HTTP 404: No such customer: 'cus_X'", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new StripeConnector(), Context(refusing, StripeConnector.GetCustomerOperation, new() { ["customer"] = "cus_X" })))).Message);
        }

        [Fact]
        public async Task A_test_says_whether_the_key_is_live()
        {
            var live = new StripeApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"object\":\"balance\",\"livemode\":true,\"available\":[],\"pending\":[]}") };
            var tested = await new StripeConnector().TestConnectionAsync(Contexts.Test(new StubEgress(live), Api, credentials: Key));
            Assert.Equal((true, "Signed in with a LIVE key."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/balance", "Bearer sk_test_1"), (live.Requests[0].Url, live.Requests[0].Authorization));
            var test = new StripeApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"object\":\"balance\",\"livemode\":false}") };
            Assert.Equal("Signed in with a test key.", (await new StripeConnector().TestConnectionAsync(Contexts.Test(new StubEgress(test), Api, credentials: Key))).Message);
            var refused = await new StripeConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StripeApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid API Key provided: sk_test_***1\",\"type\":\"invalid_request_error\"}}") }), Api, credentials: Key));
            Assert.Equal((false, "HTTP 401: Invalid API Key provided: sk_test_***1"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "stripe", ConnectorCategory.Commerce), (new StripeConnector().AuthType, new StripeConnector().ConnectorType, Assert.Single(new StripeConnector().Categories)));
            Assert.Equal(new[] { "customers", "get_customer", "create_customer", "update_customer", "delete_customer", "charges", "balance" }, new StripeConnector().Operations.Select(operation => operation.Name));
        }
    }
}
