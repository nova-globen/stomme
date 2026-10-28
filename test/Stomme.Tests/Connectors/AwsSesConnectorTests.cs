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
using Stomme.Modules.Connectors.AwsSes;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Amazon SES (n8n's AWS SES node: e-mail sent plain or from a template, templates, custom verification e-mails): every
    /// call signed with AWS Signature Version 4 for SES in the connection's region at the clock's time; an e-mail sent - its
    /// sender, recipients, reply-to, configuration set, tags, subject and parts as written - or sent from a template with its
    /// data; templates and verification templates listed page by page, got, made, changed with what is not given kept and
    /// deleted; a verification sent; what could go wrong refused before anything is signed or sent; AWS's error type and
    /// message; a test naming the region, the sending state and the quota.
    /// </summary>
    public sealed class AwsSesConnectorTests
    {
        private const string Api = "https://email.eu-north-1.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDEXAMPLE", ["secret-key"] = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" };

        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-north-1" };

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static AwsSesConnector Connector() => new(new FixedClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero)));

        private sealed class Ses : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Date, string? Body, string? ContentType)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            public string? ErrorType { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, request.Headers.TryGetValues("x-amz-date", out var date) ? date.Single() : null, body, request.Content?.Headers.ContentType?.ToString()));
                var (status, answer) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (ErrorType is not null)
                {
                    response.Headers.TryAddWithoutValidation("x-amzn-ErrorType", ErrorType);
                }

                return response;
            }
        }

        private static ConnectorExtractContext Context(Ses ses, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(ses), Api, operation, parameters, settings: Settings, credentials: Credentials);

        [Fact]
        public async Task An_email_is_sent_signed_plain_or_from_a_template()
        {
            var ses = new Ses { Answer = (_, _) => (HttpStatusCode.OK, """{"MessageId":"0102018-abc"}""") };
            var sent = Assert.Single(await Contexts.DrainAsync(Connector(), Context(ses, AwsSesConnector.SendEmailOperation, new()
            {
                ["from"] = "Café Shop <shop@example.com>", ["to"] = "\"Lin, B\" <lin@example.com>, ada@example.com", ["cc"] = "boss@example.com", ["bcc"] = "Ops\\Team <audit@example.com>", ["reply_to"] = "help@example.com",
                ["subject"] = "Your café order", ["text"] = "Thanks\nFika", ["html"] = "<p>Thanks - <b>café</b></p>", ["configuration_set"] = "orders", ["tags"] = "{\"campaign\": \"autumn\"}",
            })));

            var call = Assert.Single(ses.Requests);
            Assert.Equal(("POST", $"{Api}/v2/email/outbound-emails", "20261012T093000Z"), (call.Method.Method, call.Url, call.Date));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-north-1/ses/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=", call.Authorization, StringComparison.Ordinal);
            Assert.Equal("application/json", call.ContentType);
            // A name with a comma or a backslash quoted - the backslash escaped - and the comma inside the quotes no address's end.
            Assert.Equal("{\"FromEmailAddress\":\"=?UTF-8?B?Q2Fmw6kgU2hvcA==?= <shop@example.com>\",\"Destination\":{\"ToAddresses\":[\"\\\"Lin, B\\\" <lin@example.com>\",\"ada@example.com\"],\"CcAddresses\":[\"boss@example.com\"],\"BccAddresses\":[\"\\\"Ops\\\\\\\\Team\\\" <audit@example.com>\"]},"
                + "\"ReplyToAddresses\":[\"help@example.com\"],\"ConfigurationSetName\":\"orders\",\"EmailTags\":[{\"Name\":\"campaign\",\"Value\":\"autumn\"}],"
                + "\"Content\":{\"Simple\":{\"Subject\":{\"Data\":\"Your café order\",\"Charset\":\"UTF-8\"},\"Body\":{\"Text\":{\"Data\":\"Thanks\\nFika\",\"Charset\":\"UTF-8\"},\"Html\":{\"Data\":\"<p>Thanks - <b>café</b></p>\",\"Charset\":\"UTF-8\"}}}}}", call.Body);
            Assert.Equal("0102018-abc", (string?)sent.Fields["message_id"]);

            await Contexts.DrainAsync(Connector(), Context(ses, AwsSesConnector.SendTemplateOperation, new() { ["template"] = "Order_1", ["data"] = "{\"name\": \"Ada\", \"items\": 2}", ["from"] = "shop@example.com", ["to"] = "ada@example.com" }));
            Assert.Equal("{\"FromEmailAddress\":\"shop@example.com\",\"Destination\":{\"ToAddresses\":[\"ada@example.com\"]},\"Content\":{\"Template\":{\"TemplateName\":\"Order_1\",\"TemplateData\":\"{\\\"name\\\":\\\"Ada\\\",\\\"items\\\":2}\"}}}", ses.Requests[^1].Body); // the data a JSON string, as SES takes it
            await Contexts.DrainAsync(Connector(), Context(ses, AwsSesConnector.SendTemplateOperation, new() { ["template"] = "Order_1", ["from"] = "shop@example.com", ["to"] = "ada@example.com" }));
            Assert.Contains("\"TemplateData\":\"{}\"", ses.Requests[^1].Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Templates_are_listed_got_made_changed_and_deleted_and_a_verification_sent()
        {
            var ses = new Ses
            {
                Answer = (method, url) =>
                    url.Contains("/templates?", StringComparison.Ordinal) ? (HttpStatusCode.OK, url.Contains("NextToken", StringComparison.Ordinal)
                        ? """{"TemplatesMetadata":[{"TemplateName":"T3","CreatedTimestamp":1760259600.5}]}"""
                        : """{"TemplatesMetadata":[{"TemplateName":"T1","CreatedTimestamp":1760259600.5},{"TemplateName":"T2","CreatedTimestamp":1760259601}],"NextToken":"a+b/c="}""")
                    : url.Contains("/custom-verification-email-templates?", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"CustomVerificationEmailTemplates":[{"TemplateName":"V1","FromEmailAddress":"shop@example.com","TemplateSubject":"Verify","SuccessRedirectionURL":"https://example.com/ok","FailureRedirectionURL":"https://example.com/no"}]}""")
                    : url.Contains("/custom-verification-email-templates/", StringComparison.Ordinal) && method == HttpMethod.Get ? (HttpStatusCode.OK, """{"TemplateName":"V1","FromEmailAddress":"shop@example.com","TemplateSubject":"Verify","TemplateContent":"<p>Hi</p>","SuccessRedirectionURL":"https://example.com/ok","FailureRedirectionURL":"https://example.com/no"}""")
                    : url.Contains("/templates/", StringComparison.Ordinal) && method == HttpMethod.Get ? (HttpStatusCode.OK, """{"TemplateName":"Order_1","TemplateContent":{"Subject":"Hi {{name}}","Text":"Kept","Html":"<p>Kept</p>"}}""")
                    : url.EndsWith("/outbound-custom-verification-emails", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"MessageId":"v-1"}""")
                    : (HttpStatusCode.OK, "{}"),
            };
            (string, string, string?) Last() => (ses.Requests[^1].Method.Method, ses.Requests[^1].Url, ses.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(ses, operation, parameters));

            var templates = await Run(AwsSesConnector.TemplatesOperation, new() { ["max"] = "3" });
            Assert.Equal(new[] { "T1", "T2", "T3" }, templates.Select(template => (string?)template.Fields["name"]));
            Assert.Equal("2025-10-12T09:00:00Z", (string?)templates[0].Fields["created_timestamp"]);
            Assert.Equal(new[] { $"{Api}/v2/email/templates?PageSize=3", $"{Api}/v2/email/templates?PageSize=1&NextToken=a%2Bb%2Fc%3D" }, ses.Requests.Select(request => request.Url)); // a page no bigger than what is left
            Assert.Equal(2, (await Run(AwsSesConnector.TemplatesOperation, new() { ["max"] = "2" })).Count);
            Assert.Equal(3, ses.Requests.Count); // the page that held as many as asked the last one asked for
            var verification = Assert.Single(await Run(AwsSesConnector.VerificationTemplatesOperation, new() { ["max"] = "5" })).Fields;
            Assert.Equal($"{Api}/v2/email/custom-verification-email-templates?PageSize=5", Last().Item2);
            await Run(AwsSesConnector.VerificationTemplatesOperation, new());
            Assert.Equal($"{Api}/v2/email/custom-verification-email-templates?PageSize=100", Last().Item2); // 1,000 at most, 100 a page
            Assert.Equal(("V1", "shop@example.com", "https://example.com/ok"), ((string?)verification["name"], (string?)verification["from_email_address"], (string?)verification["success_redirection_url"])); // SES's names in snake case, URL one word

            var template = Assert.Single(await Run(AwsSesConnector.GetTemplateOperation, new() { ["template"] = "Order_1" })).Fields;
            Assert.Equal(("GET", $"{Api}/v2/email/templates/Order_1", (string?)null), Last());
            Assert.Equal(("Order_1", "Hi {{name}}", "Kept", "<p>Kept</p>"), ((string?)template["name"], (string?)template["subject"], (string?)template["text"], (string?)template["html"]));
            await Run(AwsSesConnector.CreateTemplateOperation, new() { ["template"] = "Order_2", ["subject"] = "Hej {{name}}", ["html"] = "<p>Café</p>" });
            Assert.Equal(("POST", $"{Api}/v2/email/templates", (string?)"{\"TemplateName\":\"Order_2\",\"TemplateContent\":{\"Subject\":\"Hej {{name}}\",\"Html\":\"<p>Café</p>\"}}"), Last());
            await Run(AwsSesConnector.UpdateTemplateOperation, new() { ["template"] = "Order_1", ["html"] = "<p>New</p>" });
            Assert.Equal(new[] { ("GET", $"{Api}/v2/email/templates/Order_1", (string?)null), ("PUT", $"{Api}/v2/email/templates/Order_1", "{\"TemplateContent\":{\"Subject\":\"Hi {{name}}\",\"Text\":\"Kept\",\"Html\":\"<p>New</p>\"}}") },
                ses.Requests.TakeLast(2).Select(request => (request.Method.Method, request.Url, request.Body))); // what is not given kept
            var gone = Assert.Single(await Run(AwsSesConnector.DeleteTemplateOperation, new() { ["template"] = "Order_1" }));
            Assert.Equal(("DELETE", $"{Api}/v2/email/templates/Order_1", (string?)null), Last());
            Assert.Equal(("Order_1", true), ((string?)gone.Fields["template"], (bool?)gone.Fields["done"]));

            await Run(AwsSesConnector.CreateVerificationTemplateOperation, new() { ["template"] = "V2", ["from"] = "shop@example.com", ["subject"] = "Verify", ["content"] = "<p>Hi</p>", ["success_url"] = "https://example.com/ok", ["failure_url"] = "https://example.com/no" });
            Assert.Equal(("POST", $"{Api}/v2/email/custom-verification-email-templates", (string?)"{\"FromEmailAddress\":\"shop@example.com\",\"TemplateSubject\":\"Verify\",\"TemplateContent\":\"<p>Hi</p>\",\"SuccessRedirectionURL\":\"https://example.com/ok\",\"FailureRedirectionURL\":\"https://example.com/no\",\"TemplateName\":\"V2\"}"), Last());
            await Run(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V1", ["subject"] = "Verify now" });
            Assert.Equal(("PUT", $"{Api}/v2/email/custom-verification-email-templates/V1", (string?)"{\"FromEmailAddress\":\"shop@example.com\",\"TemplateSubject\":\"Verify now\",\"TemplateContent\":\"<p>Hi</p>\",\"SuccessRedirectionURL\":\"https://example.com/ok\",\"FailureRedirectionURL\":\"https://example.com/no\"}"), Last());
            await Run(AwsSesConnector.DeleteVerificationTemplateOperation, new() { ["template"] = "V1" });
            Assert.Equal(("DELETE", $"{Api}/v2/email/custom-verification-email-templates/V1", (string?)null), Last());
            var verify = Assert.Single(await Run(AwsSesConnector.SendVerificationOperation, new() { ["template"] = "V1", ["email"] = "ada@example.com", ["configuration_set"] = "verify" }));
            Assert.Equal(("POST", $"{Api}/v2/email/outbound-custom-verification-emails", (string?)"{\"EmailAddress\":\"ada@example.com\",\"TemplateName\":\"V1\",\"ConfigurationSetName\":\"verify\"}"), Last());
            Assert.Equal(("v-1", "ada@example.com"), ((string?)verify.Fields["message_id"], (string?)verify.Fields["email"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed_or_sent_and_awss_errors_are_its_words()
        {
            var ses = new Ses();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(ses, operation, parameters)))).Message;
            var mail = new Dictionary<string, string> { ["from"] = "shop@example.com", ["to"] = "ada@example.com", ["subject"] = "s", ["text"] = "t" };
            Dictionary<string, string> With(string key, string value) => new(mail) { [key] = value };

            Assert.Equal("Give the sender.", await Refused(AwsSesConnector.SendEmailOperation, new() { ["to"] = "a@example.com" }));
            Assert.Equal("'shop' is not an e-mail address (sender).", await Refused(AwsSesConnector.SendEmailOperation, With("from", "shop")));
            Assert.Equal("Give the recipients - to.", await Refused(AwsSesConnector.SendEmailOperation, new() { ["from"] = "shop@example.com", ["subject"] = "s", ["text"] = "t" }));
            Assert.Equal("'a@b@c' is not an e-mail address (cc).", await Refused(AwsSesConnector.SendEmailOperation, With("cc", "ok@example.com, a@b@c")));
            Assert.Equal("'ada@' is not an e-mail address (to).", await Refused(AwsSesConnector.SendEmailOperation, With("to", "ada@")));
            Assert.Equal("An e-mail goes to 50 recipients at most - 51 given.", await Refused(AwsSesConnector.SendEmailOperation, With("bcc", string.Join(",", Enumerable.Range(1, 50).Select(i => $"r{i}@example.com")))));
            Assert.Equal("'help' is not an e-mail address (reply to).", await Refused(AwsSesConnector.SendEmailOperation, With("reply_to", "help")));
            Assert.Equal("'my set' is not a configuration set's name.", await Refused(AwsSesConnector.SendEmailOperation, With("configuration_set", "my set")));
            Assert.Equal("The tags are not a JSON object - {\"campaign\": \"autumn\"}.", await Refused(AwsSesConnector.SendEmailOperation, With("tags", "campaign=autumn")));
            Assert.Equal("'bad name' is not a tag's name - letters, digits, - and _.", await Refused(AwsSesConnector.SendEmailOperation, With("tags", "{\"bad name\": \"x\"}")));
            Assert.Equal("The tag campaign's value is not letters, digits, - and _.", await Refused(AwsSesConnector.SendEmailOperation, With("tags", "{\"campaign\": \"autumn sale\"}")));
            Assert.Equal("Give the e-mail's subject.", await Refused(AwsSesConnector.SendEmailOperation, new() { ["from"] = "shop@example.com", ["to"] = "a@example.com", ["text"] = "t" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(AwsSesConnector.SendEmailOperation, With("subject", "a\nb")));
            Assert.Equal("Give the e-mail's text or HTML.", await Refused(AwsSesConnector.SendEmailOperation, new() { ["from"] = "shop@example.com", ["to"] = "a@example.com", ["subject"] = "s", ["text"] = " " }));
            Assert.Equal("Give the template - its name.", await Refused(AwsSesConnector.SendTemplateOperation, new(mail)));
            Assert.Equal("'my template' is not a template's name - letters, digits, - and _, up to 64.", await Refused(AwsSesConnector.GetTemplateOperation, new() { ["template"] = "my template" }));
            Assert.Equal("The data are not a JSON object - {\"name\": \"Ada\"}.", await Refused(AwsSesConnector.SendTemplateOperation, new(mail) { ["template"] = "T", ["data"] = "[1]" }));
            Assert.Equal("Give the template's subject.", await Refused(AwsSesConnector.CreateTemplateOperation, new() { ["template"] = "T", ["text"] = "t" }));
            Assert.Equal("Give the template's text or HTML.", await Refused(AwsSesConnector.CreateTemplateOperation, new() { ["template"] = "T", ["subject"] = "s" }));
            Assert.Equal("Give something to change - a subject, a text or an HTML part.", await Refused(AwsSesConnector.UpdateTemplateOperation, new() { ["template"] = "T" }));
            Assert.Equal("Give the template's sender.", await Refused(AwsSesConnector.CreateVerificationTemplateOperation, new() { ["template"] = "V" }));
            Assert.Equal("'ok' is not an address (address after verifying) - https://example.com/thanks.", await Refused(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V", ["success_url"] = "ok" }));
            Assert.Equal("'javascript:alert(1)' is not an address (address after failing) - https://example.com/thanks.", await Refused(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V", ["failure_url"] = "javascript:alert(1)" }));
            Assert.Equal("'shop' is not an e-mail address (sender).", await Refused(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V", ["from"] = "shop" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V", ["subject"] = "a\nb" }));
            Assert.Equal("Give something to change - a sender, a subject, the content or an address after verifying or failing.", await Refused(AwsSesConnector.UpdateVerificationTemplateOperation, new() { ["template"] = "V" }));
            Assert.Equal("Give the e-mail address to verify.", await Refused(AwsSesConnector.SendVerificationOperation, new() { ["template"] = "V" }));
            Assert.Equal("'my set' is not a configuration set's name.", await Refused(AwsSesConnector.SendVerificationOperation, new() { ["template"] = "V", ["email"] = "ada@example.com", ["configuration_set"] = "my set" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(AwsSesConnector.TemplatesOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Amazon SES has no operation 'identities' - send_email, send_template,", await Refused("identities", new()));
            Assert.Empty(ses.Requests); // nothing signed or sent

            var noRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(ses), Api, AwsSesConnector.TemplatesOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string> { ["region"] = "Stockholm" }, credentials: Credentials)));
            Assert.Equal("'Stockholm' is not an AWS region - eu-north-1.", noRegion.Message);
            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(ses), Api, AwsSesConnector.TemplatesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["access-key"] = "AKIDEXAMPLE" })));
            Assert.Equal("The Amazon SES connection has no secret key.", keyless.Message);
            var nameless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(ses), Api, AwsSesConnector.TemplatesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["secret-key"] = "s" })));
            Assert.Equal("The Amazon SES connection has no access key.", nameless.Message);
            Assert.Empty(ses.Requests);

            var rejected = new Ses { ErrorType = "MessageRejected:http://internal.amazon.com/coral/com.amazonaws.ses/", Answer = (_, _) => (HttpStatusCode.BadRequest, """{"message":"Email address is not verified."}""") };
            Assert.Equal("HTTP 400 MessageRejected: Email address is not verified.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(rejected, AwsSesConnector.SendEmailOperation, new(mail))))).Message);
            var silent = new Ses { Answer = (_, _) => (HttpStatusCode.Forbidden, "") };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, AwsSesConnector.TemplatesOperation, new())))).Message);
            var wordy = new Ses { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"message\":\"" + new string('x', 400) + "\"}") };
            Assert.Equal("HTTP 400: " + new string('x', 300), (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(wordy, AwsSesConnector.TemplatesOperation, new())))).Message); // a long message cut
        }

        [Fact]
        public async Task A_test_names_the_region_the_sending_state_and_the_quota()
        {
            var ses = new Ses { Answer = (_, _) => (HttpStatusCode.OK, """{"SendingEnabled":true,"ProductionAccessEnabled":false,"SendQuota":{"Max24HourSend":200.0,"MaxSendRate":1.0,"SentLast24Hours":12.0}}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(ses), "", settings: Settings, credentials: Credentials));
            Assert.Equal((true, "SES in eu-north-1: sending on, in the sandbox, 12 of 200 sent in the last day."), (tested.Success, tested.Message));
            Assert.Equal("https://email.eu-north-1.amazonaws.com/v2/email/account", ses.Requests[0].Url); // the region's own address when none is given
            var paused = new Ses { Answer = (_, _) => (HttpStatusCode.OK, """{"SendingEnabled":false,"ProductionAccessEnabled":true,"SendQuota":{"Max24HourSend":50000.0,"SentLast24Hours":0.0}}""") };
            var off = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(paused), "", settings: Settings, credentials: Credentials));
            Assert.Equal("SES in eu-north-1: sending off, out of the sandbox, 0 of 50,000 sent in the last day.", off.Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "awsses"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(13, Connector().Operations.Count);
        }
    }
}
