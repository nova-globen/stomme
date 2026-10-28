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
using Stomme.Modules.Connectors.GoogleTranslate;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google Translate (n8n's Google Translate node) without a server: every language, format, model and text held to its
    /// shape - and a request to Google's 100K bytes - before a token is asked for; a text translated into a language, from one
    /// given or detected, as text or HTML by a model; a text over several lines from the Query box; several texts in one call, a
    /// record each; each text's language detected with Google's confidence; the languages listed, named in one if asked; Google's
    /// status and message on a failure; a test counting the languages; Google's own address taken with its path.
    /// </summary>
    public sealed class GoogleTranslateConnectorTests
    {
        private const string Api = "https://translation.googleapis.com/language/translate/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == GoogleTranslateConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"translate-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static List<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleTranslateConnector.DefaultTokenUrl).ToList();

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, string endpoint = "") =>
            Contexts.DrainAsync(new GoogleTranslateConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_google_translate_node()
        {
            IConnector connector = new GoogleTranslateConnector();
            Assert.Equal(("googletranslate", "Google Translate", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "translate", "detect", "languages" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Assert.StartsWith("Google Translate has no operation 'speak' - translate, detect or languages", await Refused("speak", new()), StringComparison.Ordinal);
            Assert.Equal("Give the text (text) - over several lines in the step's Query box - or several as a JSON array (texts).", await Refused("translate", new() { ["to"] = "sv" }));
            Assert.Equal("Give the language to translate into - sv, en (to).", await Refused("translate", new() { ["text"] = "Closed" }));
            Assert.Equal("'svenska' is not a language Google names - sv, en, zh-CN (to).", await Refused("translate", new() { ["text"] = "Closed", ["to"] = "svenska" }));
            Assert.Equal("'en_US' is not a language Google names - sv, en, zh-CN (from).", await Refused("translate", new() { ["text"] = "Closed", ["to"] = "sv", ["from"] = "en_US" }));
            Assert.Equal("'markdown' is not text or html (format).", await Refused("translate", new() { ["text"] = "Closed", ["to"] = "sv", ["format"] = "markdown" }));
            Assert.Equal("'gpt' is not nmt or base (model).", await Refused("translate", new() { ["text"] = "Closed", ["to"] = "sv", ["model"] = "gpt" }));
            Assert.Equal("The texts are a JSON array of texts, none empty - [\"Kassan är stängd.\"] (texts).", await Refused("detect", new() { ["texts"] = "Closed" }));
            Assert.Equal("The texts are a JSON array of texts, none empty - [\"Kassan är stängd.\"] (texts).", await Refused("detect", new() { ["texts"] = "[\"Closed\", \" \"]" }));
            Assert.Equal("The texts are a JSON array of texts, none empty - [\"Kassan är stängd.\"] (texts).", await Refused("detect", new() { ["texts"] = "[1]" }));
            Assert.Equal("The texts are a JSON array of texts, none empty - [\"Kassan är stängd.\"] (texts).", await Refused("detect", new() { ["texts"] = "[]" }));
            Assert.Equal("The texts are more than Google Translate takes in one request - 100K bytes; split them over steps (text, texts).", await Refused("translate", new() { ["text"] = new string('å', 50_000), ["to"] = "en" }));
            Assert.Equal("'svenska' is not a language Google names - sv, en, zh-CN (names_in).", await Refused("languages", new() { ["names_in"] = "svenska" }));
        }

        [Fact]
        public async Task A_text_is_translated_from_a_language_given_or_detected()
        {
            var google = new Google { Answer = sent => (HttpStatusCode.OK, sent.Body!.Contains("\"source\"", StringComparison.Ordinal)
                ? "{\"data\":{\"translations\":[{\"translatedText\":\"The till is closed.\",\"model\":\"nmt\"}]}}"
                : "{\"data\":{\"translations\":[{\"translatedText\":\"The till is closed.\",\"detectedSourceLanguage\":\"sv\"}]}}") };
            var detected = Assert.Single(await Run(google, "translate", new() { ["text"] = "Kassan är stängd.", ["to"] = "en" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["text"] = "Kassan är stängd.", ["to"] = "en", ["translatedText"] = "The till is closed.", ["detectedSourceLanguage"] = "sv" }, detected);
            var sent = Calls(google).Single();
            Assert.Equal(("POST", Api, "Bearer translate-token", "{\"q\":[\"Kassan är stängd.\"],\"target\":\"en\",\"format\":\"text\"}"), (sent.Method, sent.Url, sent.Authorization, sent.Body));

            var given = Assert.Single(await Run(google, "translate", new() { ["text"] = "<b>Kassan</b>", ["to"] = "zh-CN", ["from"] = "sv", ["format"] = "HTML", ["model"] = "NMT" })).Fields;
            Assert.Equal("nmt", given["model"]);
            Assert.Equal("{\"q\":[\"<b>Kassan</b>\"],\"target\":\"zh-CN\",\"source\":\"sv\",\"format\":\"html\",\"model\":\"nmt\"}", Calls(google).Last().Body);

            await Run(google, "translate", new() { ["text"] = "ignored", ["query"] = "Kassan är stängd.\nÅter klockan åtta.", ["to"] = "en" });
            Assert.Equal("{\"q\":[\"Kassan är stängd.\\nÅter klockan åtta.\"],\"target\":\"en\",\"format\":\"text\"}", Calls(google).Last().Body); // the Query box's lines, one text
        }

        [Fact]
        public async Task Several_texts_are_translated_and_detected_in_one_call_a_record_each()
        {
            var google = new Google { Answer = sent => (HttpStatusCode.OK, sent.Url.EndsWith("/detect", StringComparison.Ordinal)
                ? "{\"data\":{\"detections\":[[{\"language\":\"sv\",\"isReliable\":false,\"confidence\":0.98}],[{\"language\":\"de\",\"isReliable\":true,\"confidence\":0.71},{\"language\":\"nl\",\"confidence\":0.2}]]}}"
                : "{\"data\":{\"translations\":[{\"translatedText\":\"Closed\",\"detectedSourceLanguage\":\"sv\"},{\"translatedText\":\"Open\",\"detectedSourceLanguage\":\"de\"}]}}") };
            var translated = await Run(google, "translate", new() { ["texts"] = "[\"Stängt\", \"Geöffnet\"]", ["to"] = "en" });
            Assert.Equal(new[] { ("Stängt", "Closed"), ("Geöffnet", "Open") }, translated.Select(record => ((string?)record.Fields["text"], (string?)record.Fields["translatedText"])));
            Assert.Equal("{\"q\":[\"Stängt\",\"Geöffnet\"],\"target\":\"en\",\"format\":\"text\"}", Calls(google).Single().Body);

            var detected = await Run(google, "detect", new() { ["texts"] = "[\"Stängt\", \"Geöffnet\"]" });
            Assert.Equal(new Dictionary<string, object?> { ["text"] = "Stängt", ["language"] = "sv", ["confidence"] = 0.98, ["isReliable"] = false }, detected[0].Fields);
            Assert.Equal(("Geöffnet", "de", (object?)0.71), ((string?)detected[1].Fields["text"], (string?)detected[1].Fields["language"], detected[1].Fields["confidence"])); // the likeliest of its detections
            Assert.Equal((Api + "/detect", "{\"q\":[\"Stängt\",\"Geöffnet\"]}"), (Calls(google).Last().Url, Calls(google).Last().Body));
        }

        [Fact]
        public async Task Languages_are_listed_named_if_asked_and_failures_are_googles_words()
        {
            var google = new Google { Answer = sent => (HttpStatusCode.OK, sent.Url.Contains("target=", StringComparison.Ordinal)
                ? "{\"data\":{\"languages\":[{\"language\":\"en\",\"name\":\"engelska\"},{\"language\":\"sv\",\"name\":\"svenska\"}]}}"
                : "{\"data\":{\"languages\":[{\"language\":\"en\"},{\"language\":\"sv\"},{\"language\":\"zh-CN\"}]}}") };
            Assert.Equal(new[] { "en", "sv", "zh-CN" }, (await Run(google, "languages", new())).Select(record => (string?)record.Fields["language"]));
            var named = await Run(google, "languages", new() { ["names_in"] = "sv" });
            Assert.Equal(new Dictionary<string, object?> { ["language"] = "sv", ["name"] = "svenska" }, named[1].Fields);
            Assert.Equal(("GET", Api + "/languages?target=sv"), (Calls(google).Last().Method, Calls(google).Last().Url));

            var tested = await new GoogleTranslateConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(google), "https://translation.googleapis.com/language/translate/v2", credentials: Credentials));
            Assert.Equal((true, "Google Translate answers - 3 languages."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/languages", Calls(google).Last().Url); // its own address with its path: the same API

            var refusing = new Google { Answer = _ => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"Request had insufficient authentication scopes.\",\"status\":\"PERMISSION_DENIED\"}}") };
            Assert.Equal("Google Translate answered HTTP 403 PERMISSION_DENIED: Request had insufficient authentication scopes. - check the refresh token and its scopes.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "translate", new() { ["text"] = "Stängt", ["to"] = "en" }))).Message);
            var bad = await new GoogleTranslateConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(refusing), "https://translation.googleapis.com/v3", credentials: Credentials));
            Assert.Equal((false, "'https://translation.googleapis.com/v3' is not Google Translate's address - its own, https://translation.googleapis.com."), (bad.Success, bad.Message));
        }
    }
}
