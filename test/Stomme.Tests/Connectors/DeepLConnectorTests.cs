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
using Stomme.Modules.Connectors.DeepL;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// DeepL (n8n's DeepL node: a text translated): every call DeepL's API v2 - its Free API for a key ending in :fx,
    /// its Pro API otherwise, or the address given - with the key in DeepL's own header; a text or a list of texts translated,
    /// fifty to a call, into a language from one (its variant dropped) or detected, with a formality, sentence splitting,
    /// formatting kept, tags, a context, a glossary and a model, each translation its text, the language detected and the text
    /// it came from; the languages, the usage and the glossaries; what could go wrong refused before anything is sent; DeepL's
    /// word on a failure, its spent characters said; a test naming the plan and its characters used.
    /// </summary>
    public sealed class DeepLConnectorTests
    {
        private static readonly Dictionary<string, string> Pro = new() { ["api-key"] = "deepl-key-1" };

        private static readonly Dictionary<string, string> Free = new() { ["api-key"] = "deepl-key-2:fx" };

        private sealed class DeepL : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(DeepL deepL, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null, string endpoint = "") =>
            Contexts.DrainAsync(new DeepLConnector(), Contexts.Extract(new StubEgress(deepL), endpoint, operation, parameters, credentials: credentials ?? Pro));

        // Each text back as its own translation, in German.
        private static (HttpStatusCode, string) Translated(HttpMethod method, string url, string? body) =>
            (HttpStatusCode.OK, new JsonObject { ["translations"] = new JsonArray(JsonNode.Parse(body!)!["text"]!.AsArray().Select(text => (JsonNode?)new JsonObject { ["detected_source_language"] = "EN", ["text"] = "DE:" + text!.GetValue<string>() }).ToArray()) }.ToJsonString());

        [Fact]
        public async Task A_text_and_a_list_of_texts_are_translated_fifty_to_a_call_with_every_option()
        {
            var deepL = new DeepL { Answer = Translated };

            var one = Assert.Single(await Run(deepL, DeepLConnector.TranslateOperation, new() { ["text"] = "Fika at three, café closed\n", ["target"] = "de", ["source"] = "en-gb", ["formality"] = "Prefer_More", ["split_sentences"] = "nonewlines", ["preserve_formatting"] = "True", ["tag_handling"] = "html", ["context"] = "A café's sign", ["glossary"] = "def3a26b-3e84-45b3-84ae-0c0aaf3525f7", ["model"] = "quality_optimized" })).Fields;
            Assert.Equal((HttpMethod.Post, "https://api.deepl.com/v2/translate", "DeepL-Auth-Key deepl-key-1"), (deepL.Requests[^1].Method, deepL.Requests[^1].Url, deepL.Requests[^1].Authorization)); // a Pro key, the Pro API
            Assert.Equal("{\"target_lang\":\"DE\",\"source_lang\":\"EN\",\"formality\":\"prefer_more\",\"split_sentences\":\"nonewlines\",\"tag_handling\":\"html\",\"model_type\":\"quality_optimized\",\"preserve_formatting\":true,\"context\":\"A café's sign\",\"glossary_id\":\"def3a26b-3e84-45b3-84ae-0c0aaf3525f7\",\"text\":[\"Fika at three, café closed\\n\"]}", deepL.Requests[^1].Body); // the source without its variant; the text as given
            Assert.Equal(("DE:Fika at three, café closed\n", "EN", "Fika at three, café closed\n"), ((string?)one["text"], (string?)one["detected_source_language"], (string?)one["original"]));

            var many = await Run(deepL, DeepLConnector.TranslateOperation, new() { ["text"] = "First", ["texts"] = "[" + string.Join(",", Enumerable.Range(1, 60).Select(i => "\"t" + i + "\"")) + "]", ["target"] = "PT-BR", ["preserve_formatting"] = "false" }, Free);
            Assert.Equal(61, many.Count);
            Assert.Equal(new[] { "DE:First", "DE:t1", "DE:t60" }, new[] { many[0], many[1], many[60] }.Select(record => (string)record.Fields["text"]!));
            Assert.Equal("t60", (string?)many[60].Fields["original"]); // each its own text, in the second call too
            var calls = deepL.Requests.TakeLast(2).ToList();
            Assert.All(calls, call => Assert.Equal(("https://api-free.deepl.com/v2/translate", "DeepL-Auth-Key deepl-key-2:fx"), (call.Url, call.Authorization))); // a key ending in :fx, the Free API
            Assert.Equal(new[] { 50, 11 }, calls.Select(call => JsonNode.Parse(call.Body!)!["text"]!.AsArray().Count)); // fifty to a call
            Assert.StartsWith("{\"target_lang\":\"PT-BR\",\"preserve_formatting\":false,\"text\":[\"First\",\"t1\",", calls[0].Body); // its target's variant kept

            await Run(deepL, DeepLConnector.TranslateOperation, new() { ["texts"] = "[\"Hej\"]", ["target"] = "EN-US" }, endpoint: "https://deepl.proxy.example.test/v2/");
            Assert.Equal("https://deepl.proxy.example.test/v2/translate", deepL.Requests[^1].Url); // the address given, its v2 not doubled
            await Run(deepL, DeepLConnector.TranslateOperation, new() { ["text"] = "Hej", ["target"] = "EN-US" }, endpoint: "https://deepl.proxy.example.test");
            Assert.Equal("https://deepl.proxy.example.test/v2/translate", deepL.Requests[^1].Url);
        }

        [Fact]
        public async Task The_languages_the_usage_and_the_glossaries_are_read()
        {
            var deepL = new DeepL
            {
                Answer = (_, url, _) => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("/languages", StringComparison.Ordinal) => "[{\"language\":\"DE\",\"name\":\"German\",\"supports_formality\":true},{\"language\":\"EN-GB\",\"name\":\"English (British)\",\"supports_formality\":false},{\"language\":\"BG\",\"name\":\"Bulgarian\"}]",
                    _ when url.Contains("/usage", StringComparison.Ordinal) => "{\"character_count\":180118,\"character_limit\":1250000,\"products\":[{\"product_type\":\"translate\",\"character_count\":180118}]}",
                    _ => "{\"glossaries\":[{\"glossary_id\":\"def3a26b-3e84-45b3-84ae-0c0aaf3525f7\",\"name\":\"Café words\",\"ready\":true,\"source_lang\":\"en\",\"target_lang\":\"de\",\"creation_time\":\"2026-10-01T08:00:00.000Z\",\"entry_count\":12}]}",
                }),
            };

            var targets = await Run(deepL, DeepLConnector.LanguagesOperation, new());
            Assert.Equal("https://api.deepl.com/v2/languages?type=target", deepL.Requests[^1].Url);
            Assert.Equal(new[] { ("DE", "German", (bool?)true), ("EN-GB", "English (British)", false), ("BG", "Bulgarian", null) }, targets.Select(language => ((string)language.Fields["language"]!, (string)language.Fields["name"]!, (bool?)language.Fields["supports_formality"])));
            await Run(deepL, DeepLConnector.LanguagesOperation, new() { ["type"] = "Source" });
            Assert.Equal("https://api.deepl.com/v2/languages?type=source", deepL.Requests[^1].Url);

            var usage = Assert.Single(await Run(deepL, DeepLConnector.UsageOperation, new())).Fields;
            Assert.Equal(("https://api.deepl.com/v2/usage", HttpMethod.Get), (deepL.Requests[^1].Url, deepL.Requests[^1].Method));
            Assert.Equal((180118L, 1250000L, "[{\"product_type\":\"translate\",\"character_count\":180118}]"), ((long?)usage["character_count"], (long?)usage["character_limit"], (string?)usage["products"]));

            var glossary = Assert.Single(await Run(deepL, DeepLConnector.GlossariesOperation, new())).Fields;
            Assert.Equal("https://api.deepl.com/v2/glossaries", deepL.Requests[^1].Url);
            Assert.Equal(("def3a26b-3e84-45b3-84ae-0c0aaf3525f7", "Café words", true, "en", "de", 12L, "2026-10-01T08:00:00.000Z"), ((string?)glossary["glossary_id"], (string?)glossary["name"], (bool?)glossary["ready"], (string?)glossary["source_lang"], (string?)glossary["target_lang"], (long?)glossary["entry_count"], (string?)glossary["creation_time"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_deepls_word_on_a_failure()
        {
            var deepL = new DeepL();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null, string endpoint = "") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(deepL, operation, parameters, credentials, endpoint))).Message;
            const string Translate = DeepLConnector.TranslateOperation;

            Assert.Equal("DeepL has no operation 'write' - translate, languages, usage or glossaries.", await Refused("write", new()));
            Assert.Equal("Give the text to translate - or a list of texts.", await Refused(Translate, new() { ["text"] = "  ", ["target"] = "DE" }));
            Assert.Equal("The texts are not a JSON list - [\"Hello\", \"Goodbye\"].", await Refused(Translate, new() { ["texts"] = "Hello, Goodbye", ["target"] = "DE" }));
            Assert.Equal("The texts are not a JSON list of texts - [\"Hello\", \"Goodbye\"].", await Refused(Translate, new() { ["texts"] = "[\"Hello\", 3]", ["target"] = "DE" }));
            Assert.Equal("Give the language to translate into - DE, EN-GB.", await Refused(Translate, new() { ["text"] = "Hej" }));
            Assert.Equal("'German' is not a language's code - DE, EN-GB, PT-BR.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "German" }));
            Assert.Equal("'en_GB' is not a language's code - EN, DE.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["source"] = "en_GB" }));
            Assert.Equal("'formal' is not more, less, prefer_more, prefer_less or default (formality).", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["formality"] = "formal" }));
            Assert.Equal("'2' is not 0, 1 or nonewlines (split_sentences).", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["split_sentences"] = "2" }));
            Assert.Equal("'markdown' is not html or xml (tag_handling).", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["tag_handling"] = "markdown" }));
            Assert.Equal("'fast' is not quality_optimized, prefer_quality_optimized or latency_optimized (model).", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["model"] = "fast" }));
            Assert.Equal("'yes' is not true or false (preserve_formatting).", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["preserve_formatting"] = "yes" }));
            Assert.Equal("'café words' is not a glossary's id.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["source"] = "EN", ["glossary"] = "café words" }));
            Assert.Equal("A glossary needs the language the text is in - give source.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE", ["glossary"] = "def3a26b-3e84-45b3-84ae-0c0aaf3525f7" }));
            Assert.Equal("'both' is not target or source (type).", await Refused(DeepLConnector.LanguagesOperation, new() { ["type"] = "both" }));
            Assert.Equal("The DeepL connection has no authentication key.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE" }, new()));
            Assert.Equal("The endpoint 'api.deepl.com' is not an absolute http(s) URL.", await Refused(Translate, new() { ["text"] = "Hej", ["target"] = "DE" }, endpoint: "api.deepl.com"));
            Assert.Empty(deepL.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new DeepL { Answer = (_, _, _) => (status, answer) }, Translate, new() { ["text"] = "Hej", ["target"] = "DE" }))).Message;
            Assert.Equal("HTTP 403: Wrong endpoint. Use https://api-free.deepl.com", await Failed(HttpStatusCode.Forbidden, "{\"message\":\"Wrong endpoint. Use https://api-free.deepl.com\"}")); // a Free key on the Pro API
            Assert.Equal("HTTP 400: Value for 'target_lang' not supported. - Use EN-GB or EN-US", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"Value for 'target_lang' not supported.\",\"detail\":\"Use EN-GB or EN-US\"}"));
            Assert.Equal("HTTP 456: the plan's characters are spent.", await Failed((HttpStatusCode)456, ""));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("DeepL answered 0 translations for 1 text.", await Failed(HttpStatusCode.OK, "{\"translations\":[]}")); // never fewer than were sent
            Assert.Equal("DeepL answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_plan_and_its_characters_used()
        {
            Task<ConnectionTestResult> Test(DeepL deepL, Dictionary<string, string> credentials) =>
                new DeepLConnector().TestConnectionAsync(Contexts.Test(new StubEgress(deepL), "", null, credentials));
            var pro = new DeepL { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"character_count\":180118,\"character_limit\":1250000}") };
            var tested = await Test(pro, Pro);
            Assert.Equal((true, "DeepL API Pro: 180,118 of 1,250,000 characters used this period."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.deepl.com/v2/usage", "DeepL-Auth-Key deepl-key-1"), (pro.Requests[0].Url, pro.Requests[0].Authorization));
            var free = new DeepL { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"character_count\":12,\"character_limit\":500000}") };
            Assert.Equal("DeepL API Free: 12 of 500,000 characters used this period.", (await Test(free, Free)).Message);
            Assert.Equal("https://api-free.deepl.com/v2/usage", free.Requests[0].Url);
            var refused = await Test(new DeepL { Answer = (_, _, _) => (HttpStatusCode.Forbidden, "") }, Pro);
            Assert.Equal((false, "HTTP 403."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "deepl"), (new DeepLConnector().AuthType, new DeepLConnector().ConnectorType));
            Assert.Equal(4, new DeepLConnector().Operations.Count);
        }
    }
}
