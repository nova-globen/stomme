#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.GoogleSlides;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Slides (n8n's Google Slides node) without a server: every id, text and size held to its shape before a token
    /// is asked for; a presentation created, read - the step's or the connection's - and its slides held to the most; texts found
    /// and replaced across it or on the slides named, by case, under a revision, a placeholder by its name alone, each with how many
    /// it changed; a slide read; a
    /// thumbnail's address and size, and its PNG saved into the run's folder without our token - only from an https address, and
    /// held to the folder's size; Google's status and message; a test reading the connection's presentation, or exchanging a token.
    /// </summary>
    public sealed class GoogleSlidesConnectorTests
    {
        private const string Api = "https://slides.googleapis.com/v1";
        private const string Thumb = "https://lh7-us.googleusercontent.com/kassa-slide-1=s1600";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            public byte[] Image { get; set; } = Array.Empty<byte>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                if (sent.Url.StartsWith(Thumb, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
                }

                var (status, answer) = sent.Url == GoogleSlidesConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"slides-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private sealed class Folder(long max = 1_000_000) : IConnectorFiles
        {
            public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);

            public long MaxBytes => max;

            public Task<ConnectorFileContent> OpenReadAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public async Task<long> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
            {
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, cancellationToken);
                Written[path] = buffer.ToArray();
                return buffer.Length;
            }
        }

        private static List<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleSlidesConnector.DefaultTokenUrl).ToList();

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, IConnectorFiles? files = null) =>
            Contexts.DrainAsync(new GoogleSlidesConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, settings ?? new Dictionary<string, string> { ["presentation"] = "kassaDeck01" }, Credentials) with { Files = files });

        [Fact]
        public void It_declares_itself_as_n8ns_google_slides_node()
        {
            IConnector connector = new GoogleSlidesConnector();
            Assert.Equal(("googleslides", "Google Slides", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "create", "presentation", "slides", "replace_text", "page", "thumbnail" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters, settings));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Assert.StartsWith("Google Slides has no operation 'present' - create, presentation, slides", await Refused("present", new()), StringComparison.Ordinal);
            Assert.Equal("Give the presentation's title (title).", await Refused("create", new()));
            Assert.Equal("Give the presentation's id (presentation), or the connection's.", await Refused("presentation", new(), new Dictionary<string, string>()));
            Assert.Equal("'kassa/deck' is not an id Google Slides gives - letters, digits, _ and - (presentation).", await Refused("slides", new() { ["presentation"] = "kassa/deck" }));
            Assert.Equal("Give the slide's object id (page).", await Refused("page", new()));
            Assert.Equal("'p 1' is not an id Google Slides gives - letters, digits, _ and - (page).", await Refused("thumbnail", new() { ["page"] = "p 1" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("slides", new() { ["max"] = "0" }));
            foreach (var texts in new[] { "", "{\"name\"}", "[\"Ada\"]", "{}", "{\"{{name}}\": 1}", "{\"\": \"Ada\"}" })
            {
                Assert.Equal("The texts are a JSON object of what to find and the text to put - {\"{{name}}\": \"Ada\"} (texts).", await Refused("replace_text", new() { ["texts"] = texts }));
            }

            Assert.Equal("With placeholders, a text to find is its name alone - {\"name\": \"Ada\"} for {{name}} (texts).", await Refused("replace_text", new() { ["texts"] = "{\"{{name}}\": \"Ada\"}", ["placeholders"] = "true" }));
            Assert.Equal("'maybe' is not true or false (match_case).", await Refused("replace_text", new() { ["texts"] = "{\"a\": \"b\"}", ["match_case"] = "maybe" }));
            Assert.Equal("'g1/x' is not an id Google Slides gives - letters, digits, _ and - (pages).", await Refused("replace_text", new() { ["texts"] = "{\"a\": \"b\"}", ["pages"] = "g1, g1/x" }));
            Assert.Equal("'rev 7' is not a revision's id - from Get a presentation (revision).", await Refused("replace_text", new() { ["texts"] = "{\"a\": \"b\"}", ["revision"] = "rev 7" }));
            Assert.Equal("'huge' is not small, medium or large (size).", await Refused("thumbnail", new() { ["page"] = "p1", ["size"] = "huge" }));
            Assert.Equal("A thumbnail is saved into the run's pipeline folder - run it from a pipeline (file).", await Refused("thumbnail", new() { ["page"] = "p1", ["file"] = "kassa/" }));
        }

        [Fact]
        public async Task A_presentation_is_created_read_and_its_slides_held_to_the_most()
        {
            var google = new Google { Answer = sent => (HttpStatusCode.OK, sent.Method == "POST"
                ? "{\"presentationId\":\"newDeck02\",\"title\":\"Kassans veckomöte\",\"revisionId\":\"r1\"}"
                : sent.Url.EndsWith("?fields=slides", StringComparison.Ordinal)
                    ? "{\"slides\":[{\"objectId\":\"p1\",\"pageElements\":[]},{\"objectId\":\"p2\"},{\"objectId\":\"p3\"}]}"
                    : "{\"presentationId\":\"kassaDeck01\",\"title\":\"Kassa\",\"locale\":\"sv\",\"revisionId\":\"r7\",\"pageSize\":{\"width\":{\"magnitude\":9144000,\"unit\":\"EMU\"}},\"slides\":[{\"objectId\":\"p1\"}]}") };
            var made = Assert.Single(await Run(google, "create", new() { ["title"] = "Kassans veckomöte" })).Fields;
            Assert.Equal(("newDeck02", "Kassans veckomöte"), ((string?)made["presentationId"], (string?)made["title"]));
            Assert.Equal(("POST", Api + "/presentations", "Bearer slides-token", "{\"title\":\"Kassans veckomöte\"}"), (Calls(google)[0].Method, Calls(google)[0].Url, Calls(google)[0].Authorization, Calls(google)[0].Body));

            var read = Assert.Single(await Run(google, "presentation", new())).Fields; // the connection's presentation
            Assert.Equal(("Kassa", "r7", "[{\"objectId\":\"p1\"}]"), ((string?)read["title"], (string?)read["revisionId"], (string?)read["slides"]));
            Assert.Equal(Api + "/presentations/kassaDeck01", Calls(google).Last().Url);
            await Run(google, "presentation", new() { ["presentation"] = "otherDeck03" });
            Assert.Equal(Api + "/presentations/otherDeck03", Calls(google).Last().Url); // the step's before the connection's

            Assert.Equal(new[] { "p1", "p2" }, (await Run(google, "slides", new() { ["max"] = "2" })).Select(slide => (string?)slide.Fields["objectId"]));
            Assert.Equal(Api + "/presentations/kassaDeck01?fields=slides", Calls(google).Last().Url);
        }

        [Fact]
        public async Task Texts_are_replaced_on_the_slides_named_by_case_under_a_revision()
        {
            var google = new Google { Answer = _ => (HttpStatusCode.OK, "{\"presentationId\":\"kassaDeck01\",\"replies\":[{\"replaceAllText\":{\"occurrencesChanged\":3}},{\"replaceAllText\":{}}],\"writeControl\":{\"requiredRevisionId\":\"r8\"}}") };
            var replaced = await Run(google, "replace_text", new() { ["texts"] = "{\"{{name}}\": \"Ada\", \"{{date}}\": \"17 oktober\"}", ["match_case"] = "TRUE", ["pages"] = "p1, p2, p1", ["revision"] = "r7" });
            Assert.Equal(new[] { ("{{name}}", "Ada", (object?)3), ("{{date}}", "17 oktober", (object?)0) }, replaced.Select(record => ((string?)record.Fields["find"], (string?)record.Fields["replace"], record.Fields["occurrencesChanged"])));
            Assert.Equal("r8", replaced[0].Fields["revision"]);
            var sent = Calls(google).Single();
            Assert.Equal(("POST", Api + "/presentations/kassaDeck01:batchUpdate"), (sent.Method, sent.Url));
            Assert.Equal("{\"requests\":[{\"replaceAllText\":{\"containsText\":{\"text\":\"{{name}}\",\"matchCase\":true},\"replaceText\":\"Ada\",\"pageObjectIds\":[\"p1\",\"p2\"]}},{\"replaceAllText\":{\"containsText\":{\"text\":\"{{date}}\",\"matchCase\":true},\"replaceText\":\"17 oktober\",\"pageObjectIds\":[\"p1\",\"p2\"]}}],\"writeControl\":{\"requiredRevisionId\":\"r7\"}}", sent.Body);

            await Run(google, "replace_text", new() { ["texts"] = "{\"name\": \"Ada\", \" dag \": \"fredag\"}", ["placeholders"] = "true" });
            Assert.Equal("{\"requests\":[{\"replaceAllText\":{\"containsText\":{\"text\":\"{{name}}\",\"matchCase\":false},\"replaceText\":\"Ada\"}},{\"replaceAllText\":{\"containsText\":{\"text\":\"{{ dag }}\",\"matchCase\":false},\"replaceText\":\"fredag\"}}]}", Calls(google).Last().Body); // a placeholder by its name, its braces added as it is written
            await Run(google, "replace_text", new() { ["texts"] = "{\"Kassa\": \"\"}" });
            Assert.Equal("{\"requests\":[{\"replaceAllText\":{\"containsText\":{\"text\":\"Kassa\",\"matchCase\":false},\"replaceText\":\"\"}}]}", Calls(google).Last().Body); // every slide, any case, no revision
        }

        [Fact]
        public async Task A_slide_and_its_thumbnail_are_read_and_the_png_saved_without_our_token()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var google = new Google
            {
                Image = png,
                Answer = sent => sent.Url.Contains("/thumbnail?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"width\":1600,\"height\":900,\"contentUrl\":\"" + Thumb + "\"}")
                    : (HttpStatusCode.OK, "{\"objectId\":\"p1\",\"pageType\":\"SLIDE\",\"pageElements\":[{\"objectId\":\"t1\"}]}"),
            };
            var slide = Assert.Single(await Run(google, "page", new() { ["page"] = "p1" })).Fields;
            Assert.Equal(("p1", "SLIDE"), ((string?)slide["objectId"], (string?)slide["pageType"]));
            Assert.Equal(Api + "/presentations/kassaDeck01/pages/p1", Calls(google).Last().Url);

            var address = Assert.Single(await Run(google, "thumbnail", new() { ["page"] = "p1", ["size"] = "medium" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["page"] = "p1", ["contentUrl"] = Thumb, ["width"] = 1600L, ["height"] = 900L, ["file"] = null, ["size"] = null }, address);
            Assert.Equal(Api + "/presentations/kassaDeck01/pages/p1/thumbnail?thumbnailProperties.thumbnailSize=MEDIUM&thumbnailProperties.mimeType=PNG", Calls(google).Last().Url);
            Assert.DoesNotContain(Calls(google), sent => sent.Url.StartsWith(Thumb, StringComparison.Ordinal)); // nothing fetched unless asked

            var folder = new Folder();
            var saved = Assert.Single(await Run(google, "thumbnail", new() { ["page"] = "p1", ["file"] = "kassa/" }, files: folder)).Fields;
            Assert.Equal(("kassa/p1.png", (object?)7L), ((string?)saved["file"], saved["size"]));
            Assert.Equal(png, folder.Written["kassa/p1.png"]);
            Assert.Contains("thumbnailSize=LARGE", Calls(google)[^2].Url, StringComparison.Ordinal); // large by default
            Assert.Equal(("GET", Thumb, (string?)null), (Calls(google)[^1].Method, Calls(google)[^1].Url, Calls(google)[^1].Authorization)); // its own access in its address: our token not sent

            var plain = new Google { Answer = sent => (HttpStatusCode.OK, "{\"width\":1600,\"height\":900,\"contentUrl\":\"http://169.254.169.254/latest/meta-data\"}") };
            Assert.Equal("Google Slides answered with a thumbnail address that is not one to fetch - an https address.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(plain, "thumbnail", new() { ["page"] = "p1", ["file"] = "x.png" }, files: new Folder()))).Message);
            Assert.Single(Calls(plain)); // never fetched
            var big = new Folder(max: 3);
            Assert.StartsWith("The thumbnail is 7 bytes, over the pipeline folder's 3", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, "thumbnail", new() { ["page"] = "p1", ["file"] = "x.png" }, files: big))).Message, StringComparison.Ordinal);
            Assert.Empty(big.Written);
        }

        [Fact]
        public async Task Failures_are_googles_words_and_a_test_reads_the_connections_presentation()
        {
            var refusing = new Google { Answer = _ => (HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"Requested entity was not found.\",\"status\":\"NOT_FOUND\"}}") };
            Assert.Equal("Google Slides answered HTTP 404 NOT_FOUND: Requested entity was not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "presentation", new()))).Message);
            var forbidden = new Google { Answer = _ => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"The caller does not have permission\",\"status\":\"PERMISSION_DENIED\"}}") };
            Assert.EndsWith(" - check the refresh token and its scopes.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(forbidden, "slides", new()))).Message, StringComparison.Ordinal);

            var google = new Google { Answer = _ => (HttpStatusCode.OK, "{\"title\":\"Kassa\"}") };
            var tested = await new GoogleSlidesConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(google), "https://slides.googleapis.com/v1", new Dictionary<string, string> { ["presentation"] = "kassaDeck01" }, Credentials));
            Assert.Equal((true, "Google Slides answers: 'Kassa'."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/presentations/kassaDeck01?fields=title", Calls(google).Last().Url);
            var token = await new GoogleSlidesConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(google), "", credentials: Credentials));
            Assert.Equal((true, "Google Slides: the refresh token was exchanged for an access token."), (token.Success, token.Message));
        }
    }
}
