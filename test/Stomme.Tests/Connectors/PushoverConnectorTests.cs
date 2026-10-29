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
using Stomme.Modules.Connectors.Pushover;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Pushover (n8n's Pushover node: a message pushed to a user's devices): every call Pushover's API with the
    /// application's token in a POST's form or a GET's query; a message pushed with every field, an emergency one retried and
    /// expiring, a time sent as Pushover's seconds; a user validated; a receipt read and cancelled; the sounds listed; what
    /// could go wrong refused before anything is sent; Pushover's status 0 a failure with its errors, a 200 among them; a test
    /// naming how many messages are left.
    /// </summary>
    public sealed class PushoverConnectorTests
    {
        private const string Api = "https://api.pushover.net/1";

        private const string User = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";

        private const string Receipt = "rLqVuqTRh62UzxtmqiaLzQmVcPgiCy";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "azGDORePK8gMaC0QOYAMyEEuzJnyUi" };

        private sealed class Pushover : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"status\":1,\"request\":\"r1\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Pushover pushover, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new PushoverConnector(), Contexts.Extract(new StubEgress(pushover), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        [Fact]
        public async Task Messages_are_pushed_users_validated_receipts_kept_and_sounds_listed()
        {
            var pushover = new Pushover
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/sounds.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"sounds\":{\"pushover\":\"Pushover (default)\",\"bike\":\"Bike\"},\"status\":1}"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"status\":1,\"acknowledged\":1,\"acknowledged_at\":1760349600,\"expired\":0,\"called_back\":0}"),
                    ("POST", var u) when u.EndsWith("/validate.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"status\":1,\"group\":0,\"devices\":[\"iphone\",\"pixel\"],\"licenses\":[\"iOS\",\"Android\"]}"),
                    ("POST", var u) when u.EndsWith("/cancel.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"status\":1,\"request\":\"r3\"}"),
                    _ => (HttpStatusCode.OK, "{\"status\":1,\"request\":\"r2\",\"receipt\":\"" + Receipt + "\"}"),
                },
            };
            (string, string, string?) Last() => (pushover.Requests[^1].Method.Method, pushover.Requests[^1].Url, pushover.Requests[^1].Body);

            var pushed = Assert.Single(await Run(pushover, PushoverConnector.PushOperation, new()
            {
                ["user"] = User, ["message"] = "Fika at 3 - Kafé & <b>bröd</b>", ["title"] = "Fika", ["priority"] = "2", ["retry"] = "60", ["expire"] = "3600", ["url"] = "https://kafe.example.test/fika?at=3",
                ["url_title"] = "Fika list", ["device"] = "iphone", ["sound"] = "bike", ["html"] = "True", ["monospace"] = "false", ["timestamp"] = "2026-10-13T12:00:00+02:00", ["ttl"] = "3600",
            })).Fields;
            Assert.Equal(("POST", $"{Api}/messages.json", (string?)"token=azGDORePK8gMaC0QOYAMyEEuzJnyUi&user=uQiRzpo4DXghDmr9QzzfQu27cmVRsG&message=Fika+at+3+-+Kaf%C3%A9+%26+%3Cb%3Ebr%C3%B6d%3C%2Fb%3E&title=Fika&priority=2&retry=60&expire=3600&url=https%3A%2F%2Fkafe.example.test%2Ffika%3Fat%3D3&url_title=Fika+list&device=iphone&sound=bike&html=1&timestamp=1791885600&ttl=3600"), Last());
            Assert.Equal((1L, "r2", Receipt), ((long?)pushed["status"], (string?)pushed["request"], (string?)pushed["receipt"]));
            await Run(pushover, PushoverConnector.PushOperation, new() { ["user"] = User, ["message"] = "Hej", ["priority"] = "-2", ["monospace"] = "true" }, endpoint: Api + "/");
            Assert.Equal("token=azGDORePK8gMaC0QOYAMyEEuzJnyUi&user=uQiRzpo4DXghDmr9QzzfQu27cmVRsG&message=Hej&priority=-2&monospace=1", Last().Item3);
            await Run(pushover, null!, new() { ["user"] = User, ["message"] = "Hej" }); // no operation: a push
            Assert.Equal(($"{Api}/messages.json", "token=azGDORePK8gMaC0QOYAMyEEuzJnyUi&user=uQiRzpo4DXghDmr9QzzfQu27cmVRsG&message=Hej"), (Last().Item2, Last().Item3));

            var valid = Assert.Single(await Run(pushover, PushoverConnector.ValidateUserOperation, new() { ["user"] = User, ["device"] = "pixel" })).Fields;
            Assert.Equal(("POST", $"{Api}/users/validate.json", (string?)"token=azGDORePK8gMaC0QOYAMyEEuzJnyUi&user=uQiRzpo4DXghDmr9QzzfQu27cmVRsG&device=pixel"), Last());
            Assert.Equal(("[\"iphone\",\"pixel\"]", 0L), ((string?)valid["devices"], (long?)valid["group"]));
            var receipt = Assert.Single(await Run(pushover, PushoverConnector.ReceiptOperation, new() { ["receipt"] = Receipt })).Fields;
            Assert.Equal(("GET", $"{Api}/receipts/{Receipt}.json?token=azGDORePK8gMaC0QOYAMyEEuzJnyUi", (string?)null), Last());
            Assert.Equal((1L, 1760349600L), ((long?)receipt["acknowledged"], (long?)receipt["acknowledged_at"]));
            Assert.Equal("r3", (string?)Assert.Single(await Run(pushover, PushoverConnector.CancelReceiptOperation, new() { ["receipt"] = Receipt })).Fields["request"]);
            Assert.Equal(("POST", $"{Api}/receipts/{Receipt}/cancel.json", (string?)"token=azGDORePK8gMaC0QOYAMyEEuzJnyUi"), Last());
            var sounds = await Run(pushover, PushoverConnector.SoundsOperation, new());
            Assert.Equal(new[] { ("pushover", "Pushover (default)"), ("bike", "Bike") }, sounds.Select(sound => ((string)sound.Fields["sound"]!, (string)sound.Fields["name"]!)));
            Assert.Equal(("GET", $"{Api}/sounds.json?token=azGDORePK8gMaC0QOYAMyEEuzJnyUi", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_pushovers_words_on_a_failure()
        {
            var pushover = new Pushover();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pushover, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> Push(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["user"] = User, ["message"] = "Fika" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("Pushover has no operation 'glances' - push, validate_user, receipt,", await Refused("glances", new()));
            Assert.Equal("Give the user's or group's key.", await Refused(PushoverConnector.PushOperation, new() { ["message"] = "Fika" }));
            Assert.Equal("'ada' is not a user's or group's key - 30 letters and digits (user).", await Refused(PushoverConnector.ValidateUserOperation, new() { ["user"] = "ada" }));
            Assert.Equal("'" + User + "x' is not a user's or group's key - 30 letters and digits (user).", await Refused(PushoverConnector.PushOperation, Push(("user", User + "x"))));
            Assert.Equal("Give the message.", await Refused(PushoverConnector.PushOperation, new() { ["user"] = User }));
            Assert.Equal("'3' is not a whole number from -2 to 2 (priority).", await Refused(PushoverConnector.PushOperation, Push(("priority", "3"))));
            Assert.Equal("'-3' is not a whole number from -2 to 2 (priority).", await Refused(PushoverConnector.PushOperation, Push(("priority", "-3"))));
            Assert.Equal("'high' is not a whole number from -2 to 2 (priority).", await Refused(PushoverConnector.PushOperation, Push(("priority", "high"))));
            Assert.Equal("An emergency message needs its retry - 30 seconds at least.", await Refused(PushoverConnector.PushOperation, Push(("priority", "2"), ("expire", "60"))));
            Assert.Equal("An emergency message needs its expire - 10800 seconds at most.", await Refused(PushoverConnector.PushOperation, Push(("priority", "2"), ("retry", "60"))));
            Assert.Equal("'29' is not a whole number from 30 to 10800 (retry).", await Refused(PushoverConnector.PushOperation, Push(("priority", "2"), ("retry", "29"), ("expire", "60"))));
            Assert.Equal("'10801' is not a whole number from 1 to 10800 (expire).", await Refused(PushoverConnector.PushOperation, Push(("priority", "2"), ("retry", "30"), ("expire", "10801"))));
            Assert.Equal("Only an emergency message (priority 2) is retried and expires.", await Refused(PushoverConnector.PushOperation, Push(("priority", "1"), ("retry", "30"))));
            Assert.Equal("Only an emergency message (priority 2) is retried and expires.", await Refused(PushoverConnector.PushOperation, Push(("expire", "60"))));
            Assert.Equal("'kafe' is not an address (url).", await Refused(PushoverConnector.PushOperation, Push(("url", "kafe"))));
            Assert.Equal("'my phone' is not a device's name - letters, digits, '-' and '_' (device).", await Refused(PushoverConnector.PushOperation, Push(("device", "my phone"))));
            Assert.Equal("'phone,pad' is not a device's name - letters, digits, '-' and '_' (device).", await Refused(PushoverConnector.ValidateUserOperation, new() { ["user"] = User, ["device"] = "phone,pad" }));
            Assert.Equal("'Bike' is not a sound's name - pushover (sound).", await Refused(PushoverConnector.PushOperation, Push(("sound", "Bike"))));
            Assert.Equal("'yes' is not true or false (html).", await Refused(PushoverConnector.PushOperation, Push(("html", "yes"))));
            Assert.Equal("A message is HTML or monospace - not both.", await Refused(PushoverConnector.PushOperation, Push(("html", "true"), ("monospace", "true"))));
            Assert.Equal("'2026-10-13' is not a time - 2026-10-13T10:00:00Z (timestamp).", await Refused(PushoverConnector.PushOperation, Push(("timestamp", "2026-10-13"))));
            Assert.Equal("'nowT' is not a time - 2026-10-13T10:00:00Z (timestamp).", await Refused(PushoverConnector.PushOperation, Push(("timestamp", "nowT"))));
            Assert.Equal("'0' is not a whole number from 1 to 2147483647 (ttl).", await Refused(PushoverConnector.PushOperation, Push(("ttl", "0"))));
            Assert.Equal("Give the receipt.", await Refused(PushoverConnector.ReceiptOperation, new()));
            Assert.Equal("'r1/../x' is not a receipt - 30 letters and digits (receipt).", await Refused(PushoverConnector.CancelReceiptOperation, new() { ["receipt"] = "r1/../x" }));
            Assert.Equal("The endpoint 'api.pushover.net' is not an absolute http(s) URL.", await Refused(PushoverConnector.SoundsOperation, new(), endpoint: "api.pushover.net"));
            Assert.Equal("The Pushover connection has no API token.", await Refused(PushoverConnector.SoundsOperation, new(), credentials: new()));
            Assert.Empty(pushover.Requests); // nothing sent
            await Run(pushover, PushoverConnector.PushOperation, Push(("priority", "2"), ("retry", "10800"), ("expire", "1")));
            Assert.Contains("&priority=2&retry=10800&expire=1", pushover.Requests[^1].Body, StringComparison.Ordinal); // the bounds themselves

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Pushover { Answer = (_, _) => (status, answer) }, PushoverConnector.PushOperation, Push()))).Message;
            Assert.Equal("HTTP 400: user identifier is not a valid user, group, or subscribed user key; message cannot be blank", await Failed(HttpStatusCode.BadRequest, "{\"user\":\"invalid\",\"errors\":[\"user identifier is not a valid user, group, or subscribed user key\",\"message cannot be blank\"],\"status\":0,\"request\":\"r\"}"));
            Assert.Equal("Pushover refused it: application token is invalid", await Failed(HttpStatusCode.OK, "{\"token\":\"invalid\",\"errors\":[\"application token is invalid\"],\"status\":0}")); // status 0 with a 200
            Assert.Equal("Pushover refused it.", await Failed(HttpStatusCode.OK, "{\"request\":\"r\"}")); // no status 1
            Assert.Equal("HTTP 429: " + new string('e', 300), await Failed(HttpStatusCode.TooManyRequests, "{\"errors\":[\"" + new string('e', 400) + "\"],\"status\":0}")); // a long one cut
            Assert.Equal("Pushover answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_how_many_messages_are_left()
        {
            Task<ConnectionTestResult> Test(Pushover pushover) =>
                new PushoverConnector().TestConnectionAsync(Contexts.Test(new StubEgress(pushover), "", null, Credentials));
            var limits = new Pushover { Answer = (_, _) => (HttpStatusCode.OK, "{\"limit\":10000,\"remaining\":7496,\"reset\":1761955200,\"status\":1}") };
            var tested = await Test(limits);
            Assert.Equal((true, "Pushover: the token has 7,496 of 10,000 messages left this month."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/apps/limits.json?token=azGDORePK8gMaC0QOYAMyEEuzJnyUi", "GET"), (limits.Requests[0].Url, limits.Requests[0].Method.Method));
            Assert.Equal("Pushover: the token is an application's.", (await Test(new Pushover())).Message);
            var refused = await Test(new Pushover { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"token\":\"invalid\",\"errors\":[\"application token is invalid\"],\"status\":0}") });
            Assert.Equal((false, "HTTP 400: application token is invalid"), (refused.Success, refused.Message));
            var unreachable = await new PushoverConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Unreachable()), "", null, Credentials));
            Assert.Equal((false, "Pushover could not be reached."), (unreachable.Success, unreachable.Message)); // never the address, the token in its query
            Assert.Equal((AuthenticationType.ApiKey, "pushover"), (new PushoverConnector().AuthType, new PushoverConnector().ConnectorType));
            Assert.Equal(5, new PushoverConnector().Operations.Count);
            Assert.Equal(("api-key", "API token (Your Applications > the application)"), (Assert.Single(new PushoverConnector().CredentialLabels!).Key, Assert.Single(new PushoverConnector().CredentialLabels!).Value)); // in Pushover's words
        }

        private sealed class Unreachable : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("Connection refused (" + request.RequestUri + ")");
        }
    }
}
