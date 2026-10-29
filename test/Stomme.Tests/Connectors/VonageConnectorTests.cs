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
using Stomme.Modules.Connectors.Vonage;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Vonage (n8n's Vonage node: an SMS sent) without a server: every number, name, text, link, length and choice held
    /// to Vonage's shape before anything is sent; the SMS form-encoded to Vonage's SMS API with the key and secret as Basic and
    /// never in the body; a text GSM-7 cannot carry sent as Unicode; n8n's time to live in minutes sent as Vonage's milliseconds;
    /// a record for each part sent, a refused part Vonage's status and words; a test reading the balance, sending nothing.
    /// </summary>
    public sealed class VonageConnectorTests
    {
        private const string Api = "https://rest.nexmo.com";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe-key", ["password"] = "kafe-secret" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Vonage : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Vonage(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static string Part(string id, string status = "0", string? error = null) =>
            $"{{\"to\":\"46701740605\",\"message-id\":\"{id}\",\"status\":\"{status}\"{(error is null ? string.Empty : $",\"error-text\":\"{error}\"")},\"remaining-balance\":\"9.87\",\"message-price\":\"0.0412\",\"network\":\"24001\"}}";

        private static string Answer(params string[] parts) => $"{{\"message-count\":\"{parts.Length}\",\"messages\":[{string.Join(",", parts)}]}}";

        private static Vonage Sending(params string[] parts) => new(_ => (HttpStatusCode.OK, Answer(parts.Length == 0 ? new[] { Part("0A0000001") } : parts)));

        private static Task<List<ConnectorRecord>> Run(Vonage vonage, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string operation = "send", string endpoint = "") =>
            Contexts.DrainAsync(new VonageConnector(), Contexts.Extract(new StubEgress(vonage), endpoint, operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        private static Dictionary<string, string> Sms(params (string Name, string Value)[] more)
        {
            var parameters = new Dictionary<string, string> { ["to"] = "46701740605", ["text"] = "Kaffet är klart.", ["from"] = "Kassan" };
            foreach (var (name, value) in more)
            {
                parameters[name] = value;
            }

            return parameters;
        }

        // The form Vonage was sent, decoded.
        private static Dictionary<string, string> Form(Sent sent) => sent.Body!.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

        private static string Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("kafe-key:kafe-secret"));

        [Fact]
        public void It_declares_itself_as_n8ns_vonage_node()
        {
            IConnector connector = new VonageConnector();
            Assert.Equal(("vonage", "Vonage", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "send" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_vonages_shape_before_anything_is_sent()
        {
            async Task<string> Refused(Dictionary<string, string> parameters, string operation = "send", string endpoint = "", Dictionary<string, string>? credentials = null)
            {
                var vonage = Sending();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new VonageConnector(), Contexts.Extract(new StubEgress(vonage), endpoint, operation, parameters, new Dictionary<string, string>(), credentials ?? Credentials)));
                Assert.Empty(vonage.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.Equal("Vonage has no operation 'call' - send.", await Refused(Sms(), "call"));
            Assert.Equal("Give the number to send to, in E.164 - 46701740605 (to).", await Refused(new() { ["text"] = "Hej", ["from"] = "Kassan" }));
            Assert.Equal("'070-123' is not a number in E.164 - 46701740605 (to).", await Refused(Sms(("to", "070-123"))));
            Assert.Equal("'Kassan' is not a number in E.164 - 46701740605 (to).", await Refused(Sms(("to", "Kassan"))));
            Assert.Equal("Give the text (text) - over several lines in the step's Query box.", await Refused(new() { ["to"] = "46701740605", ["from"] = "Kassan" }));
            Assert.Equal("A text is at most 1600 characters - this is 1601 (text).", await Refused(Sms(("text", new string('k', 1601)))));
            Assert.Equal("Give who it is from - a name of up to 11 letters and digits, or a number (from) - or set the connection's.", await Refused(new() { ["to"] = "46701740605", ["text"] = "Hej" }));
            Assert.Equal("'Kafé Kassan' is not a sender - a name of up to 11 letters and digits, or a number (from).", await Refused(Sms(("from", "Kafé Kassan"))));
            Assert.Equal("'Kassan Kafet' is not a sender - a name of up to 11 letters and digits, or a number (from).", await Refused(Sms(("from", "Kassan Kafet"))));
            Assert.Equal("'kafe.example/kvitto' is not a web address of at most 100 characters - https://kafe.example/sms/kvitto (callback).", await Refused(Sms(("callback", "kafe.example/kvitto"))));
            var longCallback = "https://kafe.example/" + new string('k', 80);
            Assert.Equal($"'{longCallback}' is not a web address of at most 100 characters - https://kafe.example/sms/kvitto (callback).", await Refused(Sms(("callback", longCallback))));
            Assert.Equal("A client reference is at most 100 characters - this is 101 (client_ref).", await Refused(Sms(("client_ref", new string('k', 101)))));
            Assert.Equal("'4' is not 0, 1, 2 or 3 (message_class).", await Refused(Sms(("message_class", "4"))));
            Assert.Equal("'256' is not 0 to 255 (protocol_id).", await Refused(Sms(("protocol_id", "256"))));
            Assert.Equal("'-1' is not 0 to 255 (protocol_id).", await Refused(Sms(("protocol_id", "-1"))));
            Assert.Equal("'ja' is not true or false (status_report).", await Refused(Sms(("status_report", "ja"))));
            Assert.Equal("'0' is not a number of minutes from 1 to 10080 - seven days (ttl).", await Refused(Sms(("ttl", "0"))));
            Assert.Equal("'10081' is not a number of minutes from 1 to 10080 - seven days (ttl).", await Refused(Sms(("ttl", "10081"))));
            Assert.Equal("'1.5' is not a number of minutes from 1 to 10080 - seven days (ttl).", await Refused(Sms(("ttl", "1.5"))));
            Assert.Equal("'https://rest.nexmo.com/sms' is not Vonage's address - its own, https://rest.nexmo.com.", await Refused(Sms(), endpoint: "https://rest.nexmo.com/sms"));
            Assert.Equal("The Vonage connection is missing 'password' - check the connection's credentials.", await Refused(Sms(), credentials: new() { ["username"] = "kafe-key" }));
        }

        [Fact]
        public async Task An_sms_is_form_encoded_with_the_key_and_secret_as_basic_and_never_in_the_body()
        {
            var vonage = Sending();
            var records = await Run(vonage, Sms(
                ("to", "+46 70-174 06 05"), ("account_ref", "kassa-2"), ("callback", "https://kafe.example/sms/kvitto"), ("client_ref", "order-4711"),
                ("message_class", "1"), ("protocol_id", "127"), ("status_report", "False"), ("ttl", "60")));

            var sent = Assert.Single(vonage.Requests);
            Assert.Equal(("POST", Api + "/sms/json", Basic), (sent.Method, sent.Url, sent.Authorization));
            Assert.Equal(new Dictionary<string, string>
            {
                ["from"] = "Kassan", ["to"] = "46701740605", ["text"] = "Kaffet är klart.", ["type"] = "text", ["account-ref"] = "kassa-2",
                ["callback"] = "https://kafe.example/sms/kvitto", ["client-ref"] = "order-4711", ["message-class"] = "1", ["protocol-id"] = "127",
                ["status-report-req"] = "false", ["ttl"] = "3600000", // n8n's minutes as Vonage's milliseconds
            }, Form(sent));
            Assert.DoesNotContain("kafe-secret", sent.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("kafe-key", sent.Body + sent.Url, StringComparison.Ordinal);

            var record = Assert.Single(records);
            Assert.Equal(("0A0000001", "0", "46701740605", "9.87", "0.0412", "24001"), ((string?)record.Fields["message-id"], (string?)record.Fields["status"], (string?)record.Fields["to"], (string?)record.Fields["remaining-balance"], (string?)record.Fields["message-price"], (string?)record.Fields["network"]));
        }

        [Fact]
        public async Task The_sender_is_a_number_or_a_name_from_the_step_or_the_connection()
        {
            var vonage = Sending();
            await Run(vonage, Sms(("from", "+46701740605")));
            await Run(vonage, new() { ["to"] = "46701740605", ["text"] = "Hej" }, new() { ["from"] = "Kafe 2" });
            await Run(vonage, Sms(("from", "Kassan")), new() { ["from"] = "Kafe 2" });
            Assert.Equal(new[] { "46701740605", "Kafe 2", "Kassan" }, vonage.Requests.Select(sent => Form(sent)["from"]));
        }

        [Fact]
        public async Task A_text_gsm7_cannot_carry_is_sent_as_unicode_and_the_query_box_wins()
        {
            var vonage = Sending();
            await Run(vonage, Sms(("text", "Smörgås och kaffe, 20 € [idag] ~ {Å}")));
            await Run(vonage, Sms(("text", "Kaffe ☕")));
            await Run(vonage, Sms(("text", "Två – nej, tre")));
            await Run(vonage, Sms(("query", "Hej!\nKaffet är klart.\n")));
            Assert.Equal(new[] { "text", "unicode", "unicode", "text" }, vonage.Requests.Select(sent => Form(sent)["type"]));
            Assert.Equal(("Kaffe ☕", "Hej!\nKaffet är klart.\n"), (Form(vonage.Requests[1])["text"], Form(vonage.Requests[3])["text"]));
        }

        [Fact]
        public async Task Each_part_sent_is_a_record_and_a_refused_one_vonages_status_and_words()
        {
            var both = await Run(Sending(Part("0A0000001"), Part("0A0000002")), Sms(("text", new string('k', 200))));
            Assert.Equal(new[] { "0A0000001", "0A0000002" }, both.Select(record => (string?)record.Fields["message-id"]));

            var quota = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Sending(Part("", "9", "Quota Exceeded - rejected")), Sms()));
            Assert.Equal("Vonage refused the message to 46701740605: Quota Exceeded - rejected (status 9).", quota.Message);
            var half = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Sending(Part("0A0000001"), Part("", "1", "Throughput Rate Exceeded")), Sms(("text", new string('k', 200)))));
            Assert.Equal("Vonage refused the message to 46701740605: Throughput Rate Exceeded (status 1) - 1 of its 2 parts were sent.", half.Message);
            var unsaid = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Sending(Part("", "6")), Sms()));
            Assert.Equal("Vonage refused the message to 46701740605: no reason given (status 6).", unsaid.Message);

            async Task<string> Failed(HttpStatusCode status, string body) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Vonage(_ => (status, body)), Sms()))).Message;

            Assert.Equal("Vonage answered HTTP 401: Unauthorized - check the API key and secret.", await Failed(HttpStatusCode.Unauthorized, "{\"type\":\"https://developer.vonage.com/api-errors#unauthorized\",\"title\":\"Unauthorized\",\"detail\":\"You did not provide correct credentials.\"}"));
            Assert.Equal("Vonage answered HTTP 500.", await Failed(HttpStatusCode.InternalServerError, "<html>fel</html>"));
            Assert.Equal("Vonage answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));
            Assert.Equal("Vonage answered with no message - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"message-count\":\"0\"}"));
            Assert.Equal("Vonage answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]"));
        }

        [Fact]
        public async Task A_test_reads_the_balance_and_sends_nothing()
        {
            var vonage = new Vonage(_ => (HttpStatusCode.OK, "{\"value\":10.28,\"autoReload\":false}"));
            var tested = await new VonageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(vonage), Api, new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Vonage answers - the account holds 10.28 EUR."), (tested.Success, tested.Message));
            var sent = Assert.Single(vonage.Requests);
            Assert.Equal(("GET", Api + "/account/get-balance", Basic, (string?)null), (sent.Method, sent.Url, sent.Authorization, sent.Body));

            async Task<ConnectionTestResult> Test(HttpStatusCode status, string body, string endpoint = Api) =>
                await new VonageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Vonage(_ => (status, body))), endpoint, new Dictionary<string, string>(), Credentials));

            var refused = await Test(HttpStatusCode.Unauthorized, "{\"title\":\"Unauthorized\"}");
            Assert.Equal((false, "Vonage answered HTTP 401: Unauthorized - check the API key and secret."), (refused.Success, refused.Message));
            var signIn = await Test(HttpStatusCode.OK, "<html><body>Sign in</body></html>");
            Assert.Equal((false, "Vonage answered with what is not JSON - check the address: a sign-in page or a proxy answers so."), (signIn.Success, signIn.Message));
            var empty = await Test(HttpStatusCode.OK, "{}");
            Assert.Equal((false, "Vonage answered without the account's balance - check the address: a sign-in page or a proxy answers so."), (empty.Success, empty.Message));
            var elsewhere = await Test(HttpStatusCode.OK, "{\"value\":1}", "https://rest.nexmo.com/account");
            Assert.Equal((false, "'https://rest.nexmo.com/account' is not Vonage's address - its own, https://rest.nexmo.com."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
