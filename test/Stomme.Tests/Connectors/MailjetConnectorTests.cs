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
using Stomme.Modules.Connectors.Mailjet;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Mailjet (n8n's Mailjet node: an e-mail sent with a body or a template, an SMS sent): the Send API v3.1 with the API
    /// key and secret key as HTTP Basic - each message's own word read, one refused a failure whatever the status - the
    /// templates page by page, a message's state; the SMS API v4 with its own token as a bearer, an SMS sent and the sent ones
    /// read; what could go wrong refused before anything is sent; Mailjet's errors on a failure; a test naming the senders.
    /// </summary>
    public sealed class MailjetConnectorTests
    {
        private const string Api = "https://api.mailjet.com";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "key-1", ["password"] = "secret-1" };

        private static readonly Dictionary<string, string> SmsCredentials = new() { ["token"] = "sms-token-1" };

        private sealed class Mailjet : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"Messages\":[{\"Status\":\"success\",\"To\":[{\"Email\":\"ada@example.com\",\"MessageUUID\":\"u-1\",\"MessageID\":576460752303423487,\"MessageHref\":\"https://api.mailjet.com/v3/REST/message/576460752303423487\"}]}]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static readonly string Basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("key-1:secret-1"));

        private static Task<List<ConnectorRecord>> Run(Mailjet mailjet, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MailjetConnector(), Contexts.Extract(new StubEgress(mailjet), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static Task<List<ConnectorRecord>> Sms(Mailjet mailjet, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MailjetSmsConnector(), Contexts.Extract(new StubEgress(mailjet), "", operation, parameters, credentials: credentials ?? SmsCredentials));

        [Fact]
        public async Task An_email_is_sent_with_a_body_or_a_template_each_messages_word_read()
        {
            var mailjet = new Mailjet();
            (string, string, string?) Last() => (mailjet.Requests[^1].Method.Method, mailjet.Requests[^1].Url, mailjet.Requests[^1].Body);

            var sent = Assert.Single(await Run(mailjet, MailjetConnector.SendEmailOperation, new()
            {
                ["from"] = "Kafé <kafe@example.com>", ["to"] = "Ada <ada@example.com>, lin@example.com", ["cc"] = "bo@example.com", ["bcc"] = "Cy <cy@example.com>", ["reply_to"] = "svar@example.com",
                ["subject"] = "Fika & bröd", ["text"] = "Hej!", ["html"] = "<p>Hej!</p>", ["variables"] = "{\"order\": \"1001\", \"rows\": 2}", ["track_opens"] = "Enabled", ["track_clicks"] = "disabled",
                ["priority"] = "2", ["campaign"] = "Höst", ["deduplicate"] = "true",
            })).Fields;
            Assert.Equal(("POST", $"{Api}/v3.1/send", (string?)"{\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\",\"Name\":\"Kafé\"},\"To\":[{\"Email\":\"ada@example.com\",\"Name\":\"Ada\"},{\"Email\":\"lin@example.com\"}],\"Cc\":[{\"Email\":\"bo@example.com\"}],\"Bcc\":[{\"Email\":\"cy@example.com\",\"Name\":\"Cy\"}],\"ReplyTo\":{\"Email\":\"svar@example.com\"},\"Subject\":\"Fika & bröd\",\"TextPart\":\"Hej!\",\"HTMLPart\":\"<p>Hej!</p>\",\"Variables\":{\"order\":\"1001\",\"rows\":2},\"TrackOpens\":\"enabled\",\"TrackClicks\":\"disabled\",\"Priority\":2,\"CustomCampaign\":\"Höst\",\"DeduplicateCampaign\":true}]}"), Last());
            Assert.Equal(Basic, mailjet.Requests[^1].Authorization);
            Assert.Equal(("success", 576460752303423487L, "u-1", "ada@example.com"), ((string?)sent["status"], (long?)sent["message_id"], (string?)sent["message_uuid"], (string?)sent["to"]));
            await Run(mailjet, null!, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["html"] = "<p>Fika</p>" }, endpoint: Api + "/v3.1/");
            Assert.Equal(($"{Api}/v3.1/send", (string?)"{\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\"},\"To\":[{\"Email\":\"ada@example.com\"}],\"Subject\":\"Fika\",\"HTMLPart\":\"<p>Fika</p>\"}]}"), (Last().Item2, Last().Item3)); // no operation: an e-mail - its API path given once

            await Run(mailjet, MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "4711", ["template_language"] = "True", ["variables"] = "{\"name\": \"Ada\"}" });
            Assert.Equal("{\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\"},\"To\":[{\"Email\":\"ada@example.com\"}],\"TemplateID\":4711,\"TemplateLanguage\":true,\"Variables\":{\"name\":\"Ada\"}}]}", Last().Item3); // its subject the template's own
            await Run(mailjet, MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "4711", ["subject"] = "Kvitto" });
            Assert.Equal("{\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\"},\"To\":[{\"Email\":\"ada@example.com\"}],\"Subject\":\"Kvitto\",\"TemplateID\":4711}]}", Last().Item3);
            await Run(mailjet, MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "4711", ["template_language"] = "false" });
            Assert.Contains("\"TemplateLanguage\":false", Last().Item3);

            var refused = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Messages\":[{\"Status\":\"error\",\"Errors\":[{\"ErrorIdentifier\":\"e-1\",\"ErrorCode\":\"send-0003\",\"StatusCode\":400,\"ErrorMessage\":\"At least \\\"HTMLPart\\\", \\\"TextPart\\\" or \\\"TemplateID\\\" must be provided.\",\"ErrorRelatedTo\":[\"HTMLPart\",\"TextPart\"]}]}]}") };
            Assert.Equal("Mailjet refused the message: At least \"HTMLPart\", \"TextPart\" or \"TemplateID\" must be provided. (HTMLPart, TextPart)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refused, MailjetConnector.SendEmailOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["text"] = "Hej" }))).Message); // a 200 that refused
            var silent = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Messages\":[{\"Status\":\"error\"}]}") };
            Assert.Equal("Mailjet refused the message: it gave no reason.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(silent, MailjetConnector.SendEmailOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["text"] = "Hej" }))).Message);
            var lengthy = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Messages\":[{\"Status\":\"error\",\"Errors\":[{\"ErrorMessage\":\"" + new string('e', 400) + "\"}]}]}") };
            Assert.Equal("Mailjet refused the message: " + new string('e', 300), (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(lengthy, MailjetConnector.SendEmailOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["text"] = "Hej" }))).Message); // a long one cut
            var none = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Messages\":[]}") };
            Assert.Equal("Mailjet answered with no message.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(none, MailjetConnector.SendEmailOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["text"] = "Hej" }))).Message);
        }

        [Fact]
        public async Task Templates_are_read_page_by_page_and_a_messages_state_got()
        {
            static string Templates(int first, int count, int total) =>
                "{\"Count\":" + count + ",\"Total\":" + total + ",\"Data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"ID\":" + (4700 + i) + ",\"Name\":\"Kvitto " + i + "\",\"Purposes\":[\"transactional\"]}")) + "]}";
            var mailjet = new Mailjet { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("Offset=100", StringComparison.Ordinal) ? Templates(101, 100, 230) : url.Contains("Offset=200", StringComparison.Ordinal) ? Templates(201, 30, 230) : Templates(1, 100, 230)) };
            var templates = await Run(mailjet, MailjetConnector.TemplatesOperation, new() { ["max"] = "150" });
            Assert.Equal(150, templates.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/v3/REST/template?Limit=100&Offset=0", $"{Api}/v3/REST/template?Limit=100&Offset=100" }, mailjet.Requests.Select(request => request.Url));
            Assert.Equal((4701L, "Kvitto 1", "[\"transactional\"]"), ((long?)templates[0].Fields["ID"], (string?)templates[0].Fields["Name"], (string?)templates[0].Fields["Purposes"]));
            mailjet.Requests.Clear();
            Assert.Equal(230, (await Run(mailjet, MailjetConnector.TemplatesOperation, new())).Count); // a short page the end
            Assert.Equal(3, mailjet.Requests.Count);
            var exact = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, Templates(1, 100, 200)) };
            Assert.Equal(200, (await Run(exact, MailjetConnector.TemplatesOperation, new())).Count); // full pages: its total the end
            Assert.Equal(2, exact.Requests.Count);
            var endless = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, Templates(1, 100, 100000)) };
            Assert.Equal(1000, (await Run(endless, MailjetConnector.TemplatesOperation, new())).Count); // 1,000 at most when no max is given
            var untotalled = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Data\":[{\"ID\":1},{\"ID\":2}]}") };
            Assert.Equal(2, (await Run(untotalled, MailjetConnector.TemplatesOperation, new())).Count); // no total: a short page the end
            Assert.Single(untotalled.Requests);
            await Run(mailjet, MailjetConnector.TemplatesOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/v3/REST/template?Limit=5&Offset=0", mailjet.Requests[^1].Url);

            var state = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Count\":1,\"Data\":[{\"ID\":576460752303423487,\"Status\":\"opened\",\"ArrivedAt\":\"2026-10-14T08:00:00Z\"}],\"Total\":1}") };
            var message = Assert.Single(await Run(state, MailjetConnector.MessageOperation, new() { ["id"] = "576460752303423487" })).Fields;
            Assert.Equal(("GET", $"{Api}/v3/REST/message/576460752303423487"), (state.Requests[^1].Method.Method, state.Requests[^1].Url));
            Assert.Equal("opened", (string?)message["Status"]);
            var missing = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Count\":0,\"Data\":[],\"Total\":0}") };
            Assert.Equal("Mailjet has no message with that id.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, MailjetConnector.MessageOperation, new() { ["id"] = "1" }))).Message);
        }

        [Fact]
        public async Task An_sms_is_sent_with_its_own_token_and_the_sent_ones_read()
        {
            var mailjet = new Mailjet { Answer = (method, url) => method == HttpMethod.Post
                ? (HttpStatusCode.OK, "{\"From\":\"Kafe\",\"To\":\"+46701740605\",\"Text\":\"Fika\",\"MessageId\":\"2034075536371630429\",\"SmsCount\":1,\"CreationTS\":1791878400,\"SentTS\":1791878401,\"Cost\":{\"Value\":0.0738,\"Currency\":\"EUR\"},\"Status\":{\"Code\":2,\"Name\":\"sent\",\"Description\":\"Message sent\"}}")
                : (HttpStatusCode.OK, "{\"Data\":[" + string.Join(",", Enumerable.Range(1, url.Contains("Offset=100", StringComparison.Ordinal) ? 20 : 100).Select(i => "{\"ID\":\"s" + i + "\",\"To\":\"+46701740605\",\"Status\":{\"Code\":3,\"Name\":\"delivered\"}}")) + "]}") };
            var sent = Assert.Single(await Sms(mailjet, MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "+46701740605", ["text"] = "Fika på café" })).Fields;
            Assert.Equal(("POST", $"{Api}/v4/sms-send", "Bearer sms-token-1", (string?)"{\"From\":\"Kafe\",\"To\":\"+46701740605\",\"Text\":\"Fika på café\"}"), (mailjet.Requests[^1].Method.Method, mailjet.Requests[^1].Url, mailjet.Requests[^1].Authorization, mailjet.Requests[^1].Body));
            Assert.Equal(("2034075536371630429", "+46701740605", "sent", 1L, "{\"Value\":0.0738,\"Currency\":\"EUR\"}"), ((string?)sent["id"], (string?)sent["to"], (string?)sent["state"], (long?)sent["sms_count"], (string?)sent["cost"]));
            await Sms(mailjet, null!, new() { ["from"] = "Kafe", ["to"] = "+46701740605", ["text"] = new string('x', 1600) }); // no operation: an SMS; 1,600 at most
            Assert.Equal($"{Api}/v4/sms-send", mailjet.Requests[^1].Url);

            mailjet.Requests.Clear();
            var read = await Sms(mailjet, MailjetSmsConnector.SmsOperation, new() { ["to"] = "+46701740605", ["from_time"] = "2026-10-13T10:00:00+02:00", ["to_time"] = "2026-10-14T00:00:00Z", ["state"] = "Delivered" });
            Assert.Equal(120, read.Count); // a short page the end
            Assert.Equal(new[] { $"{Api}/v4/sms?To=%2B46701740605&FromTS=1791878400&ToTS=1791936000&StatusCode=3&Limit=100&Offset=0", $"{Api}/v4/sms?To=%2B46701740605&FromTS=1791878400&ToTS=1791936000&StatusCode=3&Limit=100&Offset=100" }, mailjet.Requests.Select(request => request.Url));
            Assert.Equal(("s1", "{\"Code\":3,\"Name\":\"delivered\"}"), ((string?)read[0].Fields["ID"], (string?)read[0].Fields["Status"]));
            Assert.Equal(5, (await Sms(mailjet, MailjetSmsConnector.SmsOperation, new() { ["max"] = "5" })).Count);
            Assert.Equal($"{Api}/v4/sms?Limit=5&Offset=0", mailjet.Requests[^1].Url);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mailjets_errors_on_a_failure()
        {
            var mailjet = new Mailjet();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mailjet, operation, parameters, credentials: credentials))).Message;
            async Task<string> SmsRefused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Sms(mailjet, operation, parameters, credentials))).Message;
            Dictionary<string, string> Mail(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["subject"] = "Fika", ["text"] = "Hej" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.Equal("Mailjet has no operation 'contacts' - send_email, send_template, templates or message.", await Refused("contacts", new()));
            Assert.Equal("Give the sender (from).", await Refused(MailjetConnector.SendEmailOperation, Mail(("from", ""))));
            Assert.Equal("'kafe' is not an address - Kafé <kafe@example.com> or kafe@example.com (from).", await Refused(MailjetConnector.SendEmailOperation, Mail(("from", "kafe"))));
            Assert.Equal("'kafe,bo@example.com' is not an address - Kafé <kafe@example.com> or kafe@example.com (from).", await Refused(MailjetConnector.SendEmailOperation, Mail(("from", "kafe,bo@example.com"))));
            Assert.Equal("Give the recipients (to).", await Refused(MailjetConnector.SendEmailOperation, Mail(("to", ""))));
            Assert.Equal("Give at least one address (to).", await Refused(MailjetConnector.SendEmailOperation, Mail(("to", " , "))));
            Assert.Equal("'Ada <ada@example>' is not an address - Kafé <kafe@example.com> or kafe@example.com (to).", await Refused(MailjetConnector.SendEmailOperation, Mail(("to", "lin@example.com, Ada <ada@example>"))));
            Assert.Equal("'bo' is not an address - Kafé <kafe@example.com> or kafe@example.com (cc).", await Refused(MailjetConnector.SendEmailOperation, Mail(("cc", "bo"))));
            Assert.Equal("'cy' is not an address - Kafé <kafe@example.com> or kafe@example.com (bcc).", await Refused(MailjetConnector.SendEmailOperation, Mail(("bcc", "cy"))));
            Assert.Equal("'svar' is not an address - Kafé <kafe@example.com> or kafe@example.com (reply_to).", await Refused(MailjetConnector.SendEmailOperation, Mail(("reply_to", "svar"))));
            Assert.Equal("Give the subject (subject).", await Refused(MailjetConnector.SendEmailOperation, Mail(("subject", ""))));
            Assert.Equal("A subject is one line (subject).", await Refused(MailjetConnector.SendEmailOperation, Mail(("subject", "Fika\nBröd"))));
            Assert.Equal("Give a body - text or HTML (text, html).", await Refused(MailjetConnector.SendEmailOperation, Mail(("text", ""))));
            Assert.Equal("The variables are not a JSON object - {\"order\": \"1001\"} (variables).", await Refused(MailjetConnector.SendEmailOperation, Mail(("variables", "order = 1001"))));
            Assert.Equal("The variables are not a JSON object - {\"order\": \"1001\"} (variables).", await Refused(MailjetConnector.SendEmailOperation, Mail(("variables", "[1001]"))));
            Assert.Equal("'always' is not account_default, disabled or enabled (track_opens).", await Refused(MailjetConnector.SendEmailOperation, Mail(("track_opens", "always"))));
            Assert.Equal("'never' is not account_default, disabled or enabled (track_clicks).", await Refused(MailjetConnector.SendEmailOperation, Mail(("track_clicks", "never"))));
            Assert.Equal("'4' is not 0 to 3 (priority).", await Refused(MailjetConnector.SendEmailOperation, Mail(("priority", "4"))));
            Assert.Equal("'-1' is not 0 to 3 (priority).", await Refused(MailjetConnector.SendEmailOperation, Mail(("priority", "-1"))));
            Assert.Equal("'Höst\u0001' is not a campaign's name - 255 characters at most, one line (campaign).", await Refused(MailjetConnector.SendEmailOperation, Mail(("campaign", "Höst\u0001"))));
            Assert.Equal("'yes' is not true or false (deduplicate).", await Refused(MailjetConnector.SendEmailOperation, Mail(("deduplicate", "yes"))));
            Assert.Equal("Give the template's number (template).", await Refused(MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com" }));
            Assert.Equal("'kvitto' is not a template's number (template).", await Refused(MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "kvitto" }));
            Assert.Equal("'0' is not a template's number (template).", await Refused(MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "0" }));
            Assert.Equal("'maybe' is not true or false (template_language).", await Refused(MailjetConnector.SendTemplateOperation, new() { ["from"] = "kafe@example.com", ["to"] = "ada@example.com", ["template"] = "1", ["template_language"] = "maybe" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MailjetConnector.TemplatesOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the message's id (id).", await Refused(MailjetConnector.MessageOperation, new()));
            Assert.Equal("'5764/../1' is not a message's id - its number (id).", await Refused(MailjetConnector.MessageOperation, new() { ["id"] = "5764/../1" }));
            Assert.Equal("The Mailjet connection has no API key.", await Refused(MailjetConnector.SendEmailOperation, Mail(), new() { ["password"] = "secret-1" }));
            Assert.Equal("The Mailjet connection has no secret key.", await Refused(MailjetConnector.SendEmailOperation, Mail(), new() { ["username"] = "key-1" }));
            Assert.Equal("Mailjet SMS has no operation 'sms_count' - send_sms or sms.", await SmsRefused("sms_count", new()));
            Assert.Equal("Give the sender's name (from).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["to"] = "+46701740605", ["text"] = "Fika" }));
            Assert.Equal("'Kafé och bröd' is not a sender's name - 3 to 11 letters and digits (from).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafé och bröd", ["to"] = "+46701740605", ["text"] = "Fika" }));
            Assert.Equal("'KafeKassa123' is not a sender's name - 3 to 11 letters and digits (from).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "KafeKassa123", ["to"] = "+46701740605", ["text"] = "Fika" }));
            Assert.Equal("'46701740605' is not a number in its international form - +46701740605 (to).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "46701740605", ["text"] = "Fika" }));
            Assert.Equal("Give the number (to).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["text"] = "Fika" }));
            Assert.Equal("'0701740605' is not a number in its international form - +46701740605 (to).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "0701740605", ["text"] = "Fika" }));
            Assert.Equal("Give the text (text).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "+46701740605" }));
            Assert.Equal("The text is 1601 characters - 1,600 at most (text).", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "+46701740605", ["text"] = new string('x', 1601) }));
            Assert.Equal("'2026-10-13' is not a moment with its offset - 2026-10-01T00:00:00Z (from_time).", await SmsRefused(MailjetSmsConnector.SmsOperation, new() { ["from_time"] = "2026-10-13" }));
            Assert.Equal("'2026-10-13T10:00:00' is not a moment with its offset - 2026-10-01T00:00:00Z (to_time).", await SmsRefused(MailjetSmsConnector.SmsOperation, new() { ["to_time"] = "2026-10-13T10:00:00" }));
            Assert.Equal("'lost' is not pending, sent, delivered, undelivered or rejected (state).", await SmsRefused(MailjetSmsConnector.SmsOperation, new() { ["state"] = "lost" }));
            Assert.Equal("'0' is not a number above 0 (max).", await SmsRefused(MailjetSmsConnector.SmsOperation, new() { ["max"] = "0" }));
            Assert.Equal("The Mailjet SMS connection has no token.", await SmsRefused(MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "+46701740605", ["text"] = "Fika" }, new()));
            Assert.Empty(mailjet.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer, bool sms = false) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => sms ? Sms(new Mailjet { Answer = (_, _) => (status, answer) }, MailjetSmsConnector.SendSmsOperation, new() { ["from"] = "Kafe", ["to"] = "+46701740605", ["text"] = "Fika" }) : Run(new Mailjet { Answer = (_, _) => (status, answer) }, MailjetConnector.SendEmailOperation, Mail()))).Message;
            Assert.Equal("HTTP 400: \"From\" must be a valid e-mail. (From.Email)", await Failed(HttpStatusCode.BadRequest, "{\"Messages\":[{\"Status\":\"error\",\"Errors\":[{\"ErrorMessage\":\"\\\"From\\\" must be a valid e-mail.\",\"ErrorRelatedTo\":[\"From.Email\"]}]}]}"));
            Assert.Equal("HTTP 401: API key authentication/authorization failure. You may be unauthorized to access the API or your API key may be expired. Visit API keys management section to check your keys.", await Failed(HttpStatusCode.Unauthorized, "{\"ErrorIdentifier\":\"e-2\",\"ErrorCode\":\"mj-0002\",\"StatusCode\":401,\"ErrorMessage\":\"API key authentication/authorization failure. You may be unauthorized to access the API or your API key may be expired. Visit API keys management section to check your keys.\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"ErrorMessage\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Mailjet answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("HTTP 400: Invalid recipient (To)", await Failed(HttpStatusCode.BadRequest, "{\"ErrorIdentifier\":\"e-3\",\"ErrorCode\":\"sms-0002\",\"StatusCode\":400,\"ErrorMessage\":\"Invalid recipient\",\"ErrorRelatedTo\":[\"To\"]}", sms: true));
            Assert.Equal("Mailjet answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>", sms: true));
        }

        [Fact]
        public async Task A_test_names_the_senders_and_the_latest_sms()
        {
            var senders = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Count\":3,\"Data\":[{\"Email\":\"kafe@example.com\"},{\"Email\":\"svar@example.com\"},{\"Email\":\"info@example.com\"}],\"Total\":3}") };
            var tested = await new MailjetConnector().TestConnectionAsync(Contexts.Test(new StubEgress(senders), "", null, Credentials));
            Assert.Equal((true, "Mailjet: signed in - kafe@example.com and 2 more senders."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/v3/REST/sender?Limit=10", Basic), (senders.Requests[0].Url, senders.Requests[0].Authorization));
            var one = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Data\":[{\"Email\":\"kafe@example.com\"},{\"Email\":\"svar@example.com\"}]}") };
            Assert.Equal("Mailjet: signed in - kafe@example.com and 1 more sender.", (await new MailjetConnector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", null, Credentials))).Message);
            Assert.Equal("Mailjet: signed in - no sender yet.", (await new MailjetConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Data\":[]}") }), "", null, Credentials))).Message);
            var latest = new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Data\":[{\"ID\":\"s1\",\"To\":\"+46701740605\"}]}") };
            var sms = await new MailjetSmsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(latest), "", null, SmsCredentials));
            Assert.Equal((true, "Mailjet SMS: signed in - the latest to +46701740605."), (sms.Success, sms.Message));
            Assert.Equal(($"{Api}/v4/sms?Limit=1", "Bearer sms-token-1"), (latest.Requests[0].Url, latest.Requests[0].Authorization));
            Assert.Equal("Mailjet SMS: signed in - nothing sent yet.", (await new MailjetSmsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Mailjet { Answer = (_, _) => (HttpStatusCode.OK, "{\"Data\":[]}") }), "", null, SmsCredentials))).Message);
            Assert.Equal((AuthenticationType.Basic, "mailjet", 4), (new MailjetConnector().AuthType, new MailjetConnector().ConnectorType, new MailjetConnector().Operations.Count));
            Assert.Equal((AuthenticationType.Bearer, "mailjetsms", 2), (new MailjetSmsConnector().AuthType, new MailjetSmsConnector().ConnectorType, new MailjetSmsConnector().Operations.Count));
            Assert.Equal("API key (Account settings > REST API > API key management)", new MailjetConnector().CredentialLabels!["username"]); // in Mailjet's words
        }
    }
}
