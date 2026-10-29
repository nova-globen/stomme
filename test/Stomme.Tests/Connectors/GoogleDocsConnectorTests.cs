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
using Stomme.Modules.Connectors.GoogleDocs;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Docs (n8n's Google Docs node: a document created, got and updated): the refresh token exchanged at the token
    /// endpoint, the access token sent as a bearer; a document by its id or its address; its text - paragraphs, tables and
    /// contents in order; a document made in a folder through Drive; text inserted at an index or at the end, in the body or a
    /// segment; every occurrence of a text replaced, how many said; Docs requests sent as they are, with a revision required or
    /// targeted; what could go wrong refused before anything is sent; Google's refusal its status word and message; a test reading
    /// the connection's document.
    /// </summary>
    public sealed class GoogleDocsConnectorTests
    {
        private const string Api = "https://docs.example.test/v1";

        private const string Doc = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-xyz";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private static readonly Dictionary<string, string> Settings = new() { ["token_url"] = "https://oauth2.example.test/token", ["drive_endpoint"] = "https://drive.example.test/drive/v3" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Docs { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : Docs(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> DocsRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(google), Api, operation, parameters, settings: settings ?? Settings, credentials: Credentials);

        private static GoogleDocsConnector Connector() => new(new OAuth2TokenProvider());

        private const string Document = """
            {"documentId":"@DOC","title":"Café <b>plan</b>","revisionId":"rev-1","body":{"content":[
              {"sectionBreak":{}},
              {"paragraph":{"elements":[{"textRun":{"content":"Hello "}},{"textRun":{"content":"Ada & co\n"}},{"inlineObjectElement":{}}]}},
              {"table":{"tableRows":[{"tableCells":[{"content":[{"paragraph":{"elements":[{"textRun":{"content":"cell 1\n"}}]}}]},{"content":[{"paragraph":{"elements":[{"textRun":{"content":"cell 2\n"}}]}}]}]}]}},
              {"tableOfContents":{"content":[{"paragraph":{"elements":[{"textRun":{"content":"Contents\n"}}]}}]}},
              {"paragraph":{"elements":[{"textRun":{"content":"The end.\n"}}]}}
            ]}}
            """;

        [Fact]
        public async Task A_documents_text_is_read_in_order_by_its_id_or_its_address()
        {
            var google = new Google { Docs = (_, _) => (HttpStatusCode.OK, Document.Replace("@DOC", Doc, StringComparison.Ordinal)) };
            var read = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.GetDocumentOperation, new() { ["document"] = $"https://docs.google.com/document/d/{Doc}/edit?tab=t.0" })));

            Assert.Equal("https://oauth2.example.test/token", google.Requests[0].Url);
            Assert.Contains("refresh_token=refresh-1", google.Requests[0].Body);
            Assert.Equal(($"{Api}/documents/{Doc}", "Bearer access-1"), (Assert.Single(google.DocsRequests).Url, google.DocsRequests[0].Authorization));
            Assert.Equal((Doc, "Café <b>plan</b>", "rev-1", $"https://docs.google.com/document/d/{Doc}/edit"), ((string?)read.Fields["id"], (string?)read.Fields["title"], (string?)read.Fields["revision"], (string?)read.Fields["url"]));
            Assert.Equal("Hello Ada & co\ncell 1\ncell 2\nContents\nThe end.\n", (string?)read.Fields["text"]); // paragraphs, tables and contents in order

            var bySetting = new Google { Docs = google.Docs };
            await Contexts.DrainAsync(Connector(), Context(bySetting, GoogleDocsConnector.GetDocumentOperation, new(), new Dictionary<string, string>(Settings) { ["document"] = Doc }));
            Assert.Equal($"{Api}/documents/{Doc}", Assert.Single(bySetting.DocsRequests).Url); // the connection's
            var signedIn = new Google { Docs = google.Docs };
            await Contexts.DrainAsync(Connector(), Context(signedIn, GoogleDocsConnector.GetDocumentOperation, new() { ["document"] = $"https://docs.google.com/document/u/1/d/{Doc}" }));
            Assert.Equal($"{Api}/documents/{Doc}", Assert.Single(signedIn.DocsRequests).Url); // an account's address too
        }

        [Fact]
        public async Task A_document_is_made_in_a_folder_text_inserted_and_replaced_and_requests_sent_as_they_are()
        {
            var google = new Google
            {
                Docs = (_, url) => (HttpStatusCode.OK, url.StartsWith("https://drive.example.test/", StringComparison.Ordinal)
                    ? "{\"id\":\"" + Doc + "\",\"name\":\"Plan & notes\",\"webViewLink\":\"https://docs.google.com/document/d/" + Doc + "/edit?usp=drivesdk\"}"
                    : "{\"documentId\":\"" + Doc + "\",\"replies\":[{\"replaceAllText\":{\"occurrencesChanged\":3}}],\"writeControl\":{\"requiredRevisionId\":\"rev-2\"}}"),
            };
            (string, string, string?) Last() => (google.DocsRequests[^1].Method.Method, google.DocsRequests[^1].Url, google.DocsRequests[^1].Body);

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.CreateDocumentOperation, new() { ["title"] = " Plan & notes ", ["folder"] = "0BxFolder_1" })));
            Assert.Equal(("POST", "https://drive.example.test/drive/v3/files?fields=id,name,webViewLink&supportsAllDrives=true", "{\"name\":\"Plan & notes\",\"mimeType\":\"application/vnd.google-apps.document\",\"parents\":[\"0BxFolder_1\"]}"), Last());
            Assert.Equal((Doc, "Plan & notes", $"https://docs.google.com/document/d/{Doc}/edit?usp=drivesdk"), ((string?)made.Fields["id"], (string?)made.Fields["title"], (string?)made.Fields["url"]));
            await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.CreateDocumentOperation, new() { ["title"] = "Plan" }));
            Assert.Equal(("https://drive.example.test/drive/v3/files?fields=id,name,webViewLink", "{\"name\":\"Plan\",\"mimeType\":\"application/vnd.google-apps.document\"}"), (Last().Item2, Last().Item3)); // My Drive

            var inserted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.InsertTextOperation, new() { ["document"] = Doc, ["text"] = "Café <b>new</b>\n" })));
            Assert.Equal(("POST", $"{Api}/documents/{Doc}:batchUpdate", "{\"requests\":[{\"insertText\":{\"text\":\"Café <b>new</b>\\n\",\"endOfSegmentLocation\":{\"segmentId\":\"\"}}}]}"), Last()); // at the end of the body
            Assert.Equal((Doc, "rev-2"), ((string?)inserted.Fields["id"], (string?)inserted.Fields["revision"]));
            await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.InsertTextOperation, new() { ["document"] = Doc, ["text"] = "Hi", ["index"] = "1", ["segment"] = "kix.header1" }));
            Assert.Equal("{\"requests\":[{\"insertText\":{\"text\":\"Hi\",\"location\":{\"segmentId\":\"kix.header1\",\"index\":1}}}]}", Last().Item3);

            var replaced = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.ReplaceTextOperation, new() { ["document"] = Doc, ["find"] = "{{name}}", ["replace"] = "Ada", ["match_case"] = "TRUE" })));
            Assert.Equal("{\"requests\":[{\"replaceAllText\":{\"replaceText\":\"Ada\",\"containsText\":{\"text\":\"{{name}}\",\"matchCase\":true}}}]}", Last().Item3);
            Assert.Equal(3L, (long?)replaced.Fields["changed"]);
            await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.ReplaceTextOperation, new() { ["document"] = Doc, ["find"] = "draft" }));
            Assert.Equal("{\"requests\":[{\"replaceAllText\":{\"replaceText\":\"\",\"containsText\":{\"text\":\"draft\",\"matchCase\":false}}}]}", Last().Item3); // removed; case not counting

            var updated = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.UpdateDocumentOperation, new()
            {
                ["document"] = Doc, ["requests"] = """[{"insertPageBreak":{"endOfSegmentLocation":{"segmentId":""}}},{"insertTable":{"rows":2,"columns":3,"location":{"segmentId":"","index":5}}}]""", ["required_revision"] = "rev-1",
            })));
            Assert.Equal("{\"requests\":[{\"insertPageBreak\":{\"endOfSegmentLocation\":{\"segmentId\":\"\"}}},{\"insertTable\":{\"rows\":2,\"columns\":3,\"location\":{\"segmentId\":\"\",\"index\":5}}}],\"writeControl\":{\"requiredRevisionId\":\"rev-1\"}}", Last().Item3);
            Assert.Equal("[{\"replaceAllText\":{\"occurrencesChanged\":3}}]", (string?)updated.Fields["replies"]);
            await Contexts.DrainAsync(Connector(), Context(google, GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = """[{"deleteHeader":{"headerId":"kix.h1"}}]""", ["target_revision"] = "rev-1" }));
            Assert.EndsWith(",\"writeControl\":{\"targetRevisionId\":\"rev-1\"}}", Last().Item3);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_googles_refusal_is_its_words()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;

            Assert.Equal("Name the document - on the operation or the connection.", await Refused(GoogleDocsConnector.GetDocumentOperation, new()));
            Assert.Equal("'my doc' is not a document - its id, or its address (docs.google.com/document/d/{id}/edit).", await Refused(GoogleDocsConnector.GetDocumentOperation, new() { ["document"] = "my doc" }));
            Assert.Equal("'https://evil.example/document/d/" + Doc + "' is not a document - its id, or its address (docs.google.com/document/d/{id}/edit).", await Refused(GoogleDocsConnector.GetDocumentOperation, new() { ["document"] = "https://evil.example/document/d/" + Doc }));
            Assert.Equal("Give the document's title.", await Refused(GoogleDocsConnector.CreateDocumentOperation, new()));
            Assert.Equal("The title has a line break - one line only.", await Refused(GoogleDocsConnector.CreateDocumentOperation, new() { ["title"] = "a\nb" }));
            Assert.Equal("'my folder' is not a folder's id.", await Refused(GoogleDocsConnector.CreateDocumentOperation, new() { ["title"] = "x", ["folder"] = "my folder" }));
            Assert.Equal("'a/b' is not a shared drive's id.", await Refused(GoogleDocsConnector.CreateDocumentOperation, new() { ["title"] = "x", ["drive"] = "a/b" }));
            Assert.Equal("Give the text to insert.", await Refused(GoogleDocsConnector.InsertTextOperation, new() { ["document"] = Doc }));
            Assert.Equal("'0' is not an index - 1 the start.", await Refused(GoogleDocsConnector.InsertTextOperation, new() { ["document"] = Doc, ["text"] = "x", ["index"] = "0" }));
            Assert.Equal("'kix/../x' is not a segment's id.", await Refused(GoogleDocsConnector.InsertTextOperation, new() { ["document"] = Doc, ["text"] = "x", ["segment"] = "kix/../x" }));
            Assert.Equal("Give the text to find.", await Refused(GoogleDocsConnector.ReplaceTextOperation, new() { ["document"] = Doc }));
            Assert.Equal("'yes' is not true or false (match case).", await Refused(GoogleDocsConnector.ReplaceTextOperation, new() { ["document"] = Doc, ["find"] = "x", ["match_case"] = "yes" }));
            Assert.Equal("Give the requests - a JSON list of Docs API requests.", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc }));
            Assert.Equal("The requests are not a JSON list of Docs API requests, each one request - [{\"insertPageBreak\": {...}}].", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = "insertPageBreak" }));
            Assert.Equal("The requests are not a JSON list of Docs API requests, each one request - [{\"insertPageBreak\": {...}}].", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = "[{\"a\":{},\"b\":{}}]" }));
            Assert.Equal("The requests are not a JSON list of Docs API requests, each one request - [{\"insertPageBreak\": {...}}].", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = "[]" }));
            Assert.Equal("A revision is required or targeted, not both.", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = "[{\"a\":{}}]", ["required_revision"] = "r1", ["target_revision"] = "r2" }));
            Assert.Equal("'r 1' is not a revision's id.", await Refused(GoogleDocsConnector.UpdateDocumentOperation, new() { ["document"] = Doc, ["requests"] = "[{\"a\":{}}]", ["required_revision"] = "r 1" }));
            Assert.StartsWith("Google Docs has no operation 'export' - get_document, create_document,", await Refused("export", new() { ["document"] = Doc }));
            Assert.Empty(google.Requests); // not even a token asked for

            var refusing = new Google { Docs = (_, _) => (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"The caller does not have permission","status":"PERMISSION_DENIED"}}""") };
            Assert.Equal("HTTP 403 PERMISSION_DENIED: The caller does not have permission", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, GoogleDocsConnector.GetDocumentOperation, new() { ["document"] = Doc })))).Message);
        }

        [Fact]
        public async Task A_test_reads_the_connections_document()
        {
            var google = new Google { Docs = (_, _) => (HttpStatusCode.OK, """{"title":"Plan"}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, settings: new Dictionary<string, string>(Settings) { ["document"] = Doc }, credentials: Credentials));
            Assert.Equal((true, "Signed in - Plan read."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/documents/{Doc}?fields=title", "Bearer access-1"), (google.DocsRequests[0].Url, google.DocsRequests[0].Authorization));
            var plain = new Google();
            var signed = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(plain), Api, settings: Settings, credentials: Credentials));
            Assert.Equal((true, "Signed in."), (signed.Success, signed.Message));
            Assert.Single(plain.Requests); // the token alone
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googledocs", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "get_document", "create_document", "insert_text", "replace_text", "update_document" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
