#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AwsComprehend;
using Stomme.Modules.Connectors.AwsRekognition;
using Stomme.Modules.Connectors.AwsTextract;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - n8n's AWS Textract, AWS Rekognition and AWS Comprehend nodes over their JSON protocols, each call signed with
    /// SigV4 for its service in the connection's region: Textract's expenses as each document's fields and line items, a
    /// document's lines, a form's keys and values and the answers to questions asked of it; Rekognition's labels, lines and words,
    /// faces, unsafe content and celebrities; Comprehend's languages, sentiment, entities, key phrases and personal data; a
    /// document or an image a file of the run's folder sent as its bytes - over the service's limit refused - or an S3 object
    /// AWS reads itself; every parameter held to its shape before anything is signed; AWS's refusals said with their type.
    /// </summary>
    public sealed class AwsAiConnectorTests : IDisposable
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-west-1" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-awsai-" + Guid.NewGuid().ToString("N"));

        public AwsAiConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(string Url, string? Target, string? Authorization, string? ContentType, string Body);

        private sealed class Aws(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.RequestUri!.AbsoluteUri, Header("X-Amz-Target"), Header("Authorization"), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1") };

        private Task<List<ConnectorRecord>> Run(IConnectorExtractor connector, Aws aws, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 24) =>
            Contexts.DrainAsync(connector, Contexts.Extract(new StubEgress(aws), "", operation, parameters, Settings, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(IConnectorExtractor connector, string operation, Dictionary<string, string> parameters, bool files = true)
            {
                var aws = new Aws(_ => Json("{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(connector, aws, operation, parameters, files));
                Assert.Empty(aws.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Amazon Textract has no operation 'ocr' - analyze_expense,", await Refused(new AwsTextractConnector(), "ocr", new()), StringComparison.Ordinal);
            Assert.Equal("Give the file in the site's pipeline folder, or an S3 bucket and object (bucket, object).", await Refused(new AwsTextractConnector(), "analyze_expense", new()));
            Assert.Equal("Give the file or an S3 object, not both.", await Refused(new AwsTextractConnector(), "analyze_expense", new() { ["file"] = "a.png", ["bucket"] = "kafe" }));
            Assert.Equal("'Kafe_Kvitton' is not an S3 bucket's name (bucket).", await Refused(new AwsTextractConnector(), "detect_text", new() { ["bucket"] = "Kafe_Kvitton", ["object"] = "a.png" }));
            Assert.Equal("Give the S3 object's name - kvitton/1042.png (object).", await Refused(new AwsTextractConnector(), "detect_text", new() { ["bucket"] = "kafe-kvitton" }));
            Assert.Equal("Ask at most 15 questions, each at most 200 characters (questions).", await Refused(new AwsTextractConnector(), "analyze_document", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.png", ["questions"] = string.Join("\n", Enumerable.Range(0, 16).Select(n => "Q" + n)) }));
            Assert.Equal("A file is read from the run's pipeline folder - run it from a pipeline, or give an S3 object.", await Refused(new AwsTextractConnector(), "detect_text", new() { ["file"] = "a.png" }, files: false));
            Assert.Equal("'101' is not a number from 0 to 100 (min_confidence).", await Refused(new AwsRekognitionConnector(), "detect_labels", new() { ["bucket"] = "kafe", ["object"] = "a.jpg", ["min_confidence"] = "101" }));
            Assert.Equal("'maybe' is not true or false (words).", await Refused(new AwsRekognitionConnector(), "detect_text", new() { ["bucket"] = "kafe", ["object"] = "a.jpg", ["words"] = "maybe" }));
            Assert.Equal("Give the text - or write it in the step's Query box.", await Refused(new AwsComprehendConnector(), "detect_sentiment", new()));
            Assert.Equal("The text is 5001 bytes; Comprehend reads at most 5000 in detect_sentiment.", await Refused(new AwsComprehendConnector(), "detect_sentiment", new() { ["text"] = new string('a', 5001) }));
            Assert.Equal("'sv' is not a language Comprehend reads here - en, es, fr, de, it, pt, ar, hi, ja, ko, zh, zh-TW (language).", await Refused(new AwsComprehendConnector(), "detect_entities", new() { ["text"] = "Hej", ["language"] = "sv" }));
            Assert.Equal("'fr' is not a language Comprehend reads here - en, es (language).", await Refused(new AwsComprehendConnector(), "detect_pii", new() { ["text"] = "Hej", ["language"] = "fr" }));

            var noRegion = new Aws(_ => Json("{}"));
            var test = await new AwsComprehendConnector().TestConnectionAsync(Contexts.Test(new StubEgress(noRegion), "", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "Give the AWS region - eu-west-1."), (test.Success, test.Message));
            var ireland = await new AwsRekognitionConnector().TestConnectionAsync(Contexts.Test(new StubEgress(noRegion), "", new Dictionary<string, string> { ["region"] = "Ireland" }, Credentials));
            Assert.Equal((false, "'Ireland' is not an AWS region - eu-west-1."), (ireland.Success, ireland.Message));
            Assert.Empty(noRegion.Requests);
        }

        [Fact]
        public async Task Textract_reads_an_expense_a_documents_lines_and_a_forms_keys_and_answers()
        {
            File.WriteAllBytes(Path.Combine(_root, "kvitto.png"), new byte[] { 1, 2, 3, 4 });
            var aws = new Aws(sent => Json(sent.Target switch
            {
                "Textract.AnalyzeExpense" => "{\"ExpenseDocuments\":[{\"SummaryFields\":[{\"Type\":{\"Text\":\"VENDOR_NAME\"},\"ValueDetection\":{\"Text\":\"Kafé Ada\"}},{\"Type\":{\"Text\":\"TOTAL\"},\"ValueDetection\":{\"Text\":\"35,00\"}}],"
                    + "\"LineItemGroups\":[{\"LineItems\":[{\"LineItemExpenseFields\":[{\"Type\":{\"Text\":\"ITEM\"},\"ValueDetection\":{\"Text\":\"Kaffe\"}},{\"Type\":{\"Text\":\"PRICE\"},\"ValueDetection\":{\"Text\":\"35,00\"}},{\"Type\":{\"Text\":\"EXPENSE_ROW\"},\"ValueDetection\":{\"Text\":\"Kaffe 35,00\"}}]}]}]}]}",
                "Textract.DetectDocumentText" => "{\"Blocks\":[{\"BlockType\":\"PAGE\",\"Id\":\"p\"},{\"BlockType\":\"LINE\",\"Id\":\"l1\",\"Text\":\"Kafé Ada\",\"Confidence\":99.5,\"Page\":1},{\"BlockType\":\"WORD\",\"Id\":\"w1\",\"Text\":\"Kafé\"}]}",
                _ => sent.Body.Contains("QUERIES", StringComparison.Ordinal)
                    ? "{\"Blocks\":[{\"BlockType\":\"QUERY\",\"Id\":\"q1\",\"Query\":{\"Text\":\"Vem är kunden?\"},\"Relationships\":[{\"Type\":\"ANSWER\",\"Ids\":[\"r1\"]}]},{\"BlockType\":\"QUERY_RESULT\",\"Id\":\"r1\",\"Text\":\"Ada\",\"Confidence\":91.0}]}"
                    : "{\"Blocks\":[{\"BlockType\":\"KEY_VALUE_SET\",\"Id\":\"k1\",\"EntityTypes\":[\"KEY\"],\"Confidence\":88.0,\"Page\":1,\"Relationships\":[{\"Type\":\"VALUE\",\"Ids\":[\"v1\"]},{\"Type\":\"CHILD\",\"Ids\":[\"w1\"]}]},"
                      + "{\"BlockType\":\"KEY_VALUE_SET\",\"Id\":\"v1\",\"EntityTypes\":[\"VALUE\"],\"Relationships\":[{\"Type\":\"CHILD\",\"Ids\":[\"w2\",\"s1\"]}]},{\"BlockType\":\"WORD\",\"Id\":\"w1\",\"Text\":\"Kund:\"},{\"BlockType\":\"WORD\",\"Id\":\"w2\",\"Text\":\"Ada\"},{\"BlockType\":\"SELECTION_ELEMENT\",\"Id\":\"s1\",\"SelectionStatus\":\"SELECTED\"}]}",
            }));
            var expense = Assert.Single(await Run(new AwsTextractConnector(), aws, "analyze_expense", new() { ["file"] = "kvitto.png" })).Fields;
            var sent = aws.Requests.Last();
            Assert.Equal(("https://textract.eu-west-1.amazonaws.com/", "Textract.AnalyzeExpense", "application/x-amz-json-1.1", "{\"Document\":{\"Bytes\":\"AQIDBA==\"}}"), (sent.Url, sent.Target, sent.ContentType, sent.Body));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAKAFE/", sent.Authorization, StringComparison.Ordinal);
            Assert.Contains("/eu-west-1/textract/aws4_request", sent.Authorization, StringComparison.Ordinal);
            Assert.Equal(new Dictionary<string, object?> { ["document"] = 1, ["VENDOR_NAME"] = "Kafé Ada", ["TOTAL"] = "35,00", ["lineItems"] = "[{\"ITEM\":\"Kaffe\",\"PRICE\":\"35,00\"}]" }, expense);

            var line = Assert.Single(await Run(new AwsTextractConnector(), aws, "detect_text", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/1042.pdf", ["version"] = "v2" })).Fields;
            Assert.Equal("{\"Document\":{\"S3Object\":{\"Bucket\":\"kafe-kvitton\",\"Name\":\"2026/1042.pdf\",\"Version\":\"v2\"}}}", aws.Requests.Last().Body); // S3: Textract reads it itself
            Assert.Equal(("Kafé Ada", 99.5, 1L), ((string)line["text"]!, (double)line["confidence"]!, (long)line["page"]!));

            var form = Assert.Single(await Run(new AwsTextractConnector(), aws, "analyze_document", new() { ["file"] = "kvitto.png" })).Fields;
            Assert.Equal(("Kund:", "Ada X", 88.0), ((string)form["key"]!, (string)form["value"]!, (double)form["confidence"]!)); // a ticked box an X
            Assert.Contains("\"FeatureTypes\":[\"FORMS\"]", aws.Requests.Last().Body, StringComparison.Ordinal);

            var asked = Assert.Single(await Run(new AwsTextractConnector(), aws, "analyze_document", new() { ["file"] = "kvitto.png", ["questions"] = "Vem är kunden?" })).Fields;
            Assert.Equal(("Vem är kunden?", "Ada", 91.0), ((string)asked["question"]!, (string)asked["answer"]!, (double)asked["confidence"]!));
            Assert.Contains("\"FeatureTypes\":[\"QUERIES\"],\"QueriesConfig\":{\"Queries\":[{\"Text\":\"Vem är kunden?\"}]}", aws.Requests.Last().Body, StringComparison.Ordinal);

            var big = new Aws(_ => Json("{}"));
            File.WriteAllBytes(Path.Combine(_root, "stor.jpg"), new byte[AwsRekognitionConnector.MaxBytes + 1]);
            Assert.Equal("'stor.jpg' is 5242881 bytes; Rekognition reads at most 5242880 sent with the call - put it in S3 and give its bucket and object.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new AwsRekognitionConnector(), big, "detect_labels", new() { ["file"] = "stor.jpg" }))).Message);
            Assert.Empty(big.Requests); // nothing signed or sent
        }

        [Fact]
        public async Task Rekognition_names_labels_lines_faces_unsafe_content_and_celebrities()
        {
            File.WriteAllBytes(Path.Combine(_root, "kassa.jpg"), new byte[] { 9, 9 });
            var aws = new Aws(sent => Json(sent.Target switch
            {
                "RekognitionService.DetectLabels" => "{\"Labels\":[{\"Name\":\"Cup\",\"Confidence\":97.5,\"Parents\":[{\"Name\":\"Tableware\"}],\"Categories\":[{\"Name\":\"Food and Beverage\"}],\"Instances\":[{},{}]}]}",
                "RekognitionService.DetectText" => "{\"TextDetections\":[{\"DetectedText\":\"KAFÉ ADA\",\"Type\":\"LINE\",\"Id\":0,\"Confidence\":99.0},{\"DetectedText\":\"KAFÉ\",\"Type\":\"WORD\",\"Id\":1,\"ParentId\":0,\"Confidence\":99.0}]}",
                "RekognitionService.DetectFaces" => "{\"FaceDetails\":[{\"Confidence\":99.9,\"BoundingBox\":{\"Width\":0.2},\"AgeRange\":{\"Low\":25,\"High\":35},\"Smile\":{\"Value\":true},\"Eyeglasses\":{\"Value\":false},\"Emotions\":[{\"Type\":\"CALM\",\"Confidence\":20},{\"Type\":\"HAPPY\",\"Confidence\":75}]}]}",
                "RekognitionService.DetectModerationLabels" => "{\"ModerationLabels\":[{\"Name\":\"Alcohol\",\"ParentName\":\"\",\"Confidence\":80.5,\"TaxonomyLevel\":1}]}",
                _ => "{\"CelebrityFaces\":[{\"Name\":\"Ada Lovelace\",\"Id\":\"c1\",\"MatchConfidence\":98.0,\"Urls\":[\"www.wikidata.org/wiki/Q7259\"]}],\"UnrecognizedFaces\":[{},{}]}",
            }));
            var label = Assert.Single(await Run(new AwsRekognitionConnector(), aws, "detect_labels", new() { ["file"] = "kassa.jpg", ["max"] = "5", ["min_confidence"] = "80" })).Fields;
            Assert.Equal(("RekognitionService.DetectLabels", "{\"Image\":{\"Bytes\":\"CQk=\"},\"MaxLabels\":5,\"MinConfidence\":80}"), (aws.Requests.Last().Target, aws.Requests.Last().Body));
            Assert.Contains("/eu-west-1/rekognition/aws4_request", aws.Requests.Last().Authorization, StringComparison.Ordinal);
            Assert.Equal(("Cup", 97.5, "Tableware", "Food and Beverage", 2), ((string)label["name"]!, (double)label["confidence"]!, (string)label["parents"]!, (string)label["categories"]!, (int)label["instances"]!));

            Assert.Equal(new[] { "KAFÉ ADA" }, (await Run(new AwsRekognitionConnector(), aws, "detect_text", new() { ["file"] = "kassa.jpg" })).Select(text => (string)text.Fields["text"]!)); // lines alone
            var both = await Run(new AwsRekognitionConnector(), aws, "detect_text", new() { ["file"] = "kassa.jpg", ["words"] = "true", ["min_confidence"] = "90" });
            Assert.Equal(new[] { ("KAFÉ ADA", "line"), ("KAFÉ", "word") }, both.Select(text => ((string)text.Fields["text"]!, (string)text.Fields["type"]!)));
            Assert.Contains("\"Filters\":{\"WordFilter\":{\"MinConfidence\":90}}", aws.Requests.Last().Body, StringComparison.Ordinal);

            var face = Assert.Single(await Run(new AwsRekognitionConnector(), aws, "detect_faces", new() { ["bucket"] = "kafe-foton", ["object"] = "kassa.jpg" })).Fields;
            Assert.Equal("{\"Image\":{\"S3Object\":{\"Bucket\":\"kafe-foton\",\"Name\":\"kassa.jpg\"}},\"Attributes\":[\"ALL\"]}", aws.Requests.Last().Body);
            Assert.Equal((25L, 35L, "happy", true, false), ((long)face["ageLow"]!, (long)face["ageHigh"]!, (string)face["emotion"]!, (bool)face["smile"]!, (bool)face["eyeglasses"]!)); // its likeliest expression
            await Run(new AwsRekognitionConnector(), aws, "detect_faces", new() { ["file"] = "kassa.jpg", ["details"] = "false" });
            Assert.Contains("\"Attributes\":[\"DEFAULT\"]", aws.Requests.Last().Body, StringComparison.Ordinal);

            var unsafeLabel = Assert.Single(await Run(new AwsRekognitionConnector(), aws, "detect_moderation_labels", new() { ["file"] = "kassa.jpg", ["min_confidence"] = "60" })).Fields;
            Assert.Equal(("Alcohol", 1L, false), ((string)unsafeLabel["name"]!, (long)unsafeLabel["level"]!, unsafeLabel.ContainsKey("parent"))); // an empty parent left out
            var celebrities = await Run(new AwsRekognitionConnector(), aws, "recognize_celebrities", new() { ["file"] = "kassa.jpg" });
            Assert.Equal(("Ada Lovelace", "www.wikidata.org/wiki/Q7259"), ((string)celebrities[0].Fields["name"]!, (string)celebrities[0].Fields["urls"]!));
            Assert.Equal(2, celebrities[1].Fields["unrecognizedFaces"]);
        }

        [Fact]
        public async Task Comprehend_reads_languages_sentiment_entities_key_phrases_and_personal_data()
        {
            var aws = new Aws(sent => Json(sent.Target switch
            {
                "Comprehend_20171127.DetectDominantLanguage" => "{\"Languages\":[{\"LanguageCode\":\"sv\",\"Score\":0.98},{\"LanguageCode\":\"no\",\"Score\":0.01}]}",
                "Comprehend_20171127.DetectSentiment" => "{\"Sentiment\":\"POSITIVE\",\"SentimentScore\":{\"Positive\":0.9,\"Negative\":0.01,\"Neutral\":0.08,\"Mixed\":0.01}}",
                "Comprehend_20171127.DetectEntities" => "{\"Entities\":[{\"Text\":\"Ada\",\"Type\":\"PERSON\",\"Score\":0.99,\"BeginOffset\":0,\"EndOffset\":3}]}",
                "Comprehend_20171127.DetectKeyPhrases" => "{\"KeyPhrases\":[{\"Text\":\"the coffee\",\"Score\":0.97,\"BeginOffset\":10,\"EndOffset\":20}]}",
                _ => "{\"Entities\":[{\"Type\":\"NAME\",\"Score\":0.99,\"BeginOffset\":0,\"EndOffset\":3},{\"Type\":\"EMAIL\",\"Score\":0.98,\"BeginOffset\":14,\"EndOffset\":30}]}",
            }));
            var languages = await Run(new AwsComprehendConnector(), aws, "detect_language", new() { ["query"] = "Kassan stängd\nÅter 08:00" });
            Assert.Equal(("Comprehend_20171127.DetectDominantLanguage", "{\"Text\":\"Kassan stängd\\nÅter 08:00\"}"), (aws.Requests.Last().Target, aws.Requests.Last().Body)); // the Query box's text, as it is
            Assert.Contains("/eu-west-1/comprehend/aws4_request", aws.Requests.Last().Authorization, StringComparison.Ordinal);
            Assert.Equal(new[] { ("sv", 0.98), ("no", 0.01) }, languages.Select(language => ((string)language.Fields["languageCode"]!, (double)language.Fields["score"]!)));

            var sentiment = Assert.Single(await Run(new AwsComprehendConnector(), aws, "detect_sentiment", new() { ["text"] = "Ada loves the coffee", ["language"] = "EN" })).Fields;
            Assert.Equal("{\"Text\":\"Ada loves the coffee\",\"LanguageCode\":\"en\"}", aws.Requests.Last().Body);
            Assert.Equal(("POSITIVE", 0.9), ((string)sentiment["sentiment"]!, (double)sentiment["positive"]!));
            var entity = Assert.Single(await Run(new AwsComprehendConnector(), aws, "detect_entities", new() { ["text"] = "Ada loves the coffee" })).Fields;
            Assert.Equal(("Ada", "PERSON", 0L, 3L), ((string)entity["text"]!, (string)entity["type"]!, (long)entity["beginOffset"]!, (long)entity["endOffset"]!));
            Assert.Equal("the coffee", Assert.Single(await Run(new AwsComprehendConnector(), aws, "detect_key_phrases", new() { ["text"] = "Ada loves the coffee" })).Fields["text"]);
            var pii = await Run(new AwsComprehendConnector(), aws, "detect_pii", new() { ["text"] = "Ada, write to ada@kafe.example", ["language"] = "en" });
            Assert.Equal(new[] { ("Ada", "NAME"), ("ada@kafe.example", "EMAIL") }, pii.Select(entity => ((string)entity.Fields["text"]!, (string)entity.Fields["type"]!))); // the text read at its offsets

            var refused = new Aws(_ => Json("{\"__type\":\"com.amazonaws.comprehend#TextSizeLimitExceededException\",\"Message\":\"Input text size exceeds limit.\"}", HttpStatusCode.BadRequest));
            Assert.Equal("Amazon Comprehend answered HTTP 400 TextSizeLimitExceededException: Input text size exceeds limit.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new AwsComprehendConnector(), refused, "detect_entities", new() { ["text"] = "Ada" }))).Message);
            var test = await new AwsComprehendConnector().TestConnectionAsync(Contexts.Test(new StubEgress(aws), "", Settings, Credentials));
            Assert.Equal((true, "Amazon Comprehend in eu-west-1 answers (2 language(s) for a word)."), (test.Success, test.Message));
        }
    }
}
