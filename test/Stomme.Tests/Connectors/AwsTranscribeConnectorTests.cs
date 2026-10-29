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
using Stomme.Modules.Connectors.AwsTranscribe;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// n8n's AWS Transcribe node over Transcribe's JSON protocol, each call signed with SigV4 for transcribe in the
    /// connection's region: every parameter held to its shape before anything is signed; a job started with its recording, its
    /// language given or identified and its settings; a job's state while it runs and, once COMPLETED, its transcript fetched from
    /// the presigned address Transcribe answers with - unsigned, and only an https one - as its text or as Transcribe writes it;
    /// jobs by status and a part of the name, page by page up to the most; a job deleted; AWS's refusals said with their type.
    /// </summary>
    public sealed class AwsTranscribeConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-west-1" };
        private const string Api = "https://transcribe.eu-west-1.amazonaws.com/";
        private const string Presigned = "https://s3.eu-west-1.amazonaws.com/aws-transcribe-eu-west-1-prod/kassa-1042.json?X-Amz-Signature=abc";

        private sealed record Sent(string Method, string Url, string? Target, string? Authorization, string Body);

        private sealed class Aws(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, Header("X-Amz-Target"), Header("Authorization"), body);
                Requests.Add(sent);
                return answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1") };

        private static Task<List<ConnectorRecord>> Run(Aws aws, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsTranscribeConnector(), Contexts.Extract(new StubEgress(aws), "", operation, parameters, Settings, Credentials));

        private static string Job(string status, string? transcript = null) =>
            "{\"TranscriptionJob\":{\"TranscriptionJobName\":\"kassa-1042\",\"TranscriptionJobStatus\":\"" + status + "\",\"LanguageCode\":\"sv-SE\",\"Media\":{\"MediaFileUri\":\"s3://kafe-ljud/kassa/1042.mp3\"}"
            + (transcript is null ? "" : ",\"Transcript\":{\"TranscriptFileUri\":\"" + transcript + "\"}") + ",\"CreationTime\":1792253498.1}}";

        private const string TranscriptFile = "{\"jobName\":\"kassa-1042\",\"results\":{\"transcripts\":[{\"transcript\":\"Kassan är stängd.\"},{\"transcript\":\"Åter klockan åtta.\"}],\"items\":[{\"start_time\":\"0.1\",\"alternatives\":[{\"content\":\"Kassan\"}]}]},\"status\":\"COMPLETED\"}";

        [Fact]
        public void It_declares_itself_as_n8ns_aws_transcribe_node()
        {
            IConnector connector = new AwsTranscribeConnector();
            Assert.Equal(("awstranscribe", "Amazon Transcribe", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "start_job", "job", "jobs", "delete_job" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "region" }, connector.ConfigSchema.Fields.Select(field => field.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var aws = new Aws(_ => Json("{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(aws, operation, parameters));
                Assert.Empty(aws.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Amazon Transcribe has no operation 'translate' - start_job, job, jobs", await Refused("translate", new()), StringComparison.Ordinal);
            Assert.Equal("Give the job's name (job).", await Refused("job", new()));
            Assert.Equal("'kassa 1042' is not a job's name - letters, digits, . _ and -, at most 200 (job).", await Refused("delete_job", new() { ["job"] = "kassa 1042" }));
            Assert.Equal("Give the recording's S3 address - s3://kafe-ljud/kassa/1042.mp3 (media).", await Refused("start_job", new() { ["job"] = "kassa-1042" }));
            Assert.Equal("'kassa/1042.mp3' is not a recording's S3 address - s3://kafe-ljud/kassa/1042.mp3 (media).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "kassa/1042.mp3" }));
            Assert.Equal("'svenska' is not a language Transcribe names - sv-SE, en-US (language).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/1042.mp3", ["language"] = "svenska" }));
            Assert.Equal("'11' is not a number from 2 to 10 (speakers).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/1042.mp3", ["speakers"] = "11" }));
            Assert.Equal("'1' is not a number from 2 to 10 (alternatives).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/1042.mp3", ["alternatives"] = "1" }));
            Assert.Equal("'maybe' is not true or false (channels).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/1042.mp3", ["channels"] = "maybe" }));
            Assert.Equal("'blur' is not remove, mask or tag (filter_method).", await Refused("start_job", new() { ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/1042.mp3", ["vocabulary_filter"] = "svordomar", ["filter_method"] = "blur" }));
            Assert.Equal("'DONE' is not QUEUED, IN_PROGRESS, FAILED or COMPLETED (status).", await Refused("jobs", new() { ["status"] = "DONE" }));
            Assert.Equal("'kassa 10' is not a part of a job's name (name_contains).", await Refused("jobs", new() { ["name_contains"] = "kassa 10" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("jobs", new() { ["max"] = "0" }));
        }

        [Fact]
        public async Task A_job_is_started_with_its_recording_language_and_settings_signed_for_transcribe()
        {
            var aws = new Aws(_ => Json(Job("IN_PROGRESS")));
            var started = Assert.Single(await Run(aws, "start_job", new()
            {
                ["job"] = "kassa-1042", ["media"] = "s3://kafe-ljud/kassa/1042.mp3", ["language"] = "sv-SE", ["channels"] = "true", ["alternatives"] = "3", ["speakers"] = "2",
                ["vocabulary"] = "kafe-ord", ["vocabulary_filter"] = "svordomar", ["filter_method"] = "MASK",
            })).Fields;
            var sent = aws.Requests.Single();
            Assert.Equal(("POST", Api, "Transcribe.StartTranscriptionJob"), (sent.Method, sent.Url, sent.Target));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAKAFE/", sent.Authorization, StringComparison.Ordinal);
            Assert.Contains("/eu-west-1/transcribe/aws4_request", sent.Authorization, StringComparison.Ordinal);
            Assert.Equal("{\"TranscriptionJobName\":\"kassa-1042\",\"Media\":{\"MediaFileUri\":\"s3://kafe-ljud/kassa/1042.mp3\"},\"LanguageCode\":\"sv-SE\",\"Settings\":{\"ChannelIdentification\":true,\"ShowAlternatives\":true,\"MaxAlternatives\":3,\"ShowSpeakerLabels\":true,\"MaxSpeakerLabels\":2,\"VocabularyName\":\"kafe-ord\",\"VocabularyFilterName\":\"svordomar\",\"VocabularyFilterMethod\":\"mask\"}}", sent.Body);
            Assert.Equal(("kassa-1042", "IN_PROGRESS", "{\"MediaFileUri\":\"s3://kafe-ljud/kassa/1042.mp3\"}"), ((string?)started["TranscriptionJobName"], (string?)started["TranscriptionJobStatus"], (string?)started["Media"]));

            await Run(aws, "start_job", new() { ["job"] = "kassa-1043", ["media"] = "s3://kafe-ljud/kassa/1043.mp3" });
            Assert.Equal("{\"TranscriptionJobName\":\"kassa-1043\",\"Media\":{\"MediaFileUri\":\"s3://kafe-ljud/kassa/1043.mp3\"},\"IdentifyLanguage\":true}", aws.Requests.Last().Body); // no language: identified, no settings
        }

        [Fact]
        public async Task A_finished_jobs_transcript_is_fetched_unsigned_from_its_presigned_address()
        {
            var aws = new Aws(sent => sent.Url == Presigned ? Json(TranscriptFile) : Json(Job("COMPLETED", Presigned)));
            var simple = Assert.Single(await Run(aws, "job", new() { ["job"] = "kassa-1042" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["job"] = "kassa-1042", ["status"] = "COMPLETED", ["language"] = "sv-SE", ["transcript"] = "Kassan är stängd. Åter klockan åtta." }, simple);
            Assert.Equal(("Transcribe.GetTranscriptionJob", "{\"TranscriptionJobName\":\"kassa-1042\"}"), (aws.Requests[0].Target, aws.Requests[0].Body));
            Assert.Equal(("GET", Presigned, (string?)null), (aws.Requests[1].Method, aws.Requests[1].Url, aws.Requests[1].Authorization)); // its own authorisation in its address: nothing of ours sent

            var full = Assert.Single(await Run(aws, "job", new() { ["job"] = "kassa-1042", ["simple"] = "false" })).Fields;
            Assert.StartsWith("{\"transcripts\":[{\"transcript\":\"Kassan är stängd.\"}", (string)full["results"]!, StringComparison.Ordinal);

            aws.Requests.Clear();
            var state = Assert.Single(await Run(aws, "job", new() { ["job"] = "kassa-1042", ["transcript"] = "false" })).Fields;
            Assert.Equal("COMPLETED", state["TranscriptionJobStatus"]);
            Assert.Single(aws.Requests); // its state alone: nothing fetched
            var running = new Aws(_ => Json(Job("IN_PROGRESS")));
            Assert.Equal("IN_PROGRESS", Assert.Single(await Run(running, "job", new() { ["job"] = "kassa-1042" })).Fields["TranscriptionJobStatus"]);

            var plain = new Aws(_ => Json(Job("COMPLETED", "http://169.254.169.254/latest/meta-data")));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(plain, "job", new() { ["job"] = "kassa-1042" }));
            Assert.Equal("Amazon Transcribe answered with an address that is not one to fetch - an https address.", refused.Message);
            Assert.Single(plain.Requests); // never fetched
            var withUser = new Aws(_ => Json(Job("COMPLETED", "https://kafe:x@s3.eu-west-1.amazonaws.com/kassa-1042.json")));
            Assert.Equal(refused.Message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(withUser, "job", new() { ["job"] = "kassa-1042" }))).Message);
            Assert.Single(withUser.Requests);

            var expired = new Aws(sent => sent.Url == Presigned ? Json("<Error><Code>AccessDenied</Code></Error>", HttpStatusCode.Forbidden) : Json(Job("COMPLETED", Presigned)));
            Assert.Equal("Amazon Transcribe's file answered HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(expired, "job", new() { ["job"] = "kassa-1042" }))).Message);
            var garbled = new Aws(sent => sent.Url == Presigned ? Json("<html>") : Json(Job("COMPLETED", Presigned)));
            Assert.Equal("Amazon Transcribe's file is not JSON.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(garbled, "job", new() { ["job"] = "kassa-1042" }))).Message);
        }

        [Fact]
        public async Task Jobs_are_listed_page_by_page_and_a_job_deleted()
        {
            string Page(int from, int count, string? next) => "{\"TranscriptionJobSummaries\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"TranscriptionJobName\":\"kassa-" + i + "\",\"TranscriptionJobStatus\":\"COMPLETED\"}")) + "]" + (next is null ? "" : ",\"NextToken\":\"" + next + "\"") + "}";
            var aws = new Aws(sent => sent.Body.Contains("\"NextToken\":\"p2\"", StringComparison.Ordinal) ? Json(Page(100, 30, null)) : Json(Page(0, 100, "p2")));
            var all = await Run(aws, "jobs", new() { ["status"] = "completed", ["name_contains"] = "kassa" });
            Assert.Equal(130, all.Count);
            Assert.Equal(new[] { "{\"Status\":\"COMPLETED\",\"JobNameContains\":\"kassa\",\"MaxResults\":100}", "{\"Status\":\"COMPLETED\",\"JobNameContains\":\"kassa\",\"MaxResults\":100,\"NextToken\":\"p2\"}" }, aws.Requests.Select(sent => sent.Body));
            aws.Requests.Clear();
            Assert.Equal(5, (await Run(aws, "jobs", new() { ["max"] = "5" })).Count);
            Assert.Equal("{\"MaxResults\":5}", aws.Requests.Single().Body); // a page no larger than what is wanted

            Assert.Equal(new Dictionary<string, object?> { ["job"] = "kassa-1042", ["deleted"] = true }, Assert.Single(await Run(aws, "delete_job", new() { ["job"] = "kassa-1042" })).Fields);
            Assert.Equal(("Transcribe.DeleteTranscriptionJob", "{\"TranscriptionJobName\":\"kassa-1042\"}"), (aws.Requests.Last().Target, aws.Requests.Last().Body));

            var refusing = new Aws(_ => Json("{\"__type\":\"com.amazonaws.transcribe#NotFoundException\",\"Message\":\"The requested job couldn't be found.\"}", HttpStatusCode.BadRequest));
            Assert.Equal("Amazon Transcribe answered HTTP 400 NotFoundException: The requested job couldn't be found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "job", new() { ["job"] = "kassa-9" }))).Message);
        }
    }
}
