#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsTranscribe
{
    /// <summary>
    /// Amazon Transcribe (n8n's AWS Transcribe node: speech in an S3 recording turned into text) over Transcribe's JSON
    /// protocol (<c>https://transcribe.{region}.amazonaws.com</c>, <c>X-Amz-Target: Transcribe.*</c>) through the shared
    /// <see cref="AwsJsonSession"/>, every call signed with SigV4 by an IAM user's access key and secret, over the host's egress. A
    /// test lists the account's transcription jobs, one.
    /// </summary>
    /// <remarks>
    /// <c>start_job</c> (a job named, its recording an S3 object, its language given or identified, its speakers and channels told
    /// apart, alternatives, a custom vocabulary and a vocabulary filter - n8n's Create); <c>job</c> (a job's state and, once it is
    /// COMPLETED, its transcript - fetched from the presigned address Transcribe answers with, unsigned, over the same egress - its
    /// text alone unless <c>simple</c> is false, as n8n's Get; a job not yet done its state); <c>jobs</c> (by status and a part of
    /// the name, page by page up to <c>max</c> - n8n's Get Many); <c>delete_job</c>. Every name, address, language, number and
    /// status held to Transcribe's shape before anything is signed; AWS's error type and message on a failure.
    /// </remarks>
    public sealed class AwsTranscribeConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private static readonly string[] Known = { "start_job", "job", "jobs", "delete_job" };

        private static readonly string[] Statuses = { "QUEUED", "IN_PROGRESS", "FAILED", "COMPLETED" };

        private static readonly Regex Name = new("^[0-9a-zA-Z._-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex Language = new("^[a-z]{2,3}-[A-Z]{2}$", RegexOptions.CultureInvariant);

        private static readonly Regex Media = new("^(s3://[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]/.+|https://[^\\s]+)$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awstranscribe";

        public string DisplayName => "Amazon Transcribe";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://transcribe.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/transcribe/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/transcribe/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region Transcribe is called in - eu-west-1. The address is Transcribe's in it when left empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField JobField = Field("job", "Job", "The job's name - kassa-2026-10-17.", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("start_job", "Start a transcription", "A recording in S3 turned into text - n8n's Create.")
            {
                Parameters = new(new[]
                {
                    JobField,
                    Field("media", "Recording", "Its S3 address - s3://kafe-ljud/kassa/1042.mp3.", required: true),
                    Field("language", "Language", "Its language - sv-SE, en-US. Empty: Transcribe identifies it."),
                    Field("channels", "Channels apart", "true: each channel transcribed apart.", ConnectorConfigFieldType.Boolean),
                    Field("alternatives", "Alternatives", "Up to how many alternatives each phrase has, 2 to 10.", ConnectorConfigFieldType.Integer),
                    Field("speakers", "Speakers", "Up to how many speakers are told apart, 2 to 10.", ConnectorConfigFieldType.Integer),
                    Field("vocabulary", "Vocabulary", "A custom vocabulary's name."),
                    Field("vocabulary_filter", "Vocabulary filter", "A vocabulary filter's name."),
                    Field("filter_method", "Filter method", "remove (the default), mask or tag."),
                }),
            },
            new ConnectorOperation("job", "Get a transcription", "A job's state and, once done, its transcript - n8n's Get.")
            {
                Parameters = new(new[]
                {
                    JobField,
                    Field("transcript", "Its transcript", "false: its state alone.", ConnectorConfigFieldType.Boolean) with { Default = "true" },
                    Field("simple", "Simplify", "false: the whole transcript - every word with its times - as Transcribe writes it.", ConnectorConfigFieldType.Boolean) with { Default = "true" },
                }),
            },
            new ConnectorOperation("jobs", "Get transcriptions", "The account's jobs - n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    Field("status", "Status", "QUEUED, IN_PROGRESS, FAILED or COMPLETED."),
                    Field("name_contains", "Name contains", "A part of the jobs' names."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("delete_job", "Delete a transcription", "n8n's Delete.") { Parameters = new(new[] { JobField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.CallAsync("ListTranscriptionJobs", new JsonObject { ["MaxResults"] = 1 }, cancellationToken);
                return ConnectionTestResult.Ok($"Amazon Transcribe in {session.Region} answers ({((answer?["TranscriptionJobSummaries"] as JsonArray)?.Count ?? 0) switch { 0 => "no jobs yet", _ => "its jobs listed" }}).");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "start_job").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Amazon Transcribe has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var job = operation == "jobs" ? string.Empty : Values.Get(p, "job") is { } named ? Name.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a job's name - letters, digits, . _ and -, at most 200 (job).") : throw new InvalidOperationException("Give the job's name (job).");
            var body = new JsonObject();
            switch (operation)
            {
                case "start_job":
                    {
                        var media = Values.Get(p, "media") ?? throw new InvalidOperationException("Give the recording's S3 address - s3://kafe-ljud/kassa/1042.mp3 (media).");
                        body["TranscriptionJobName"] = job;
                        body["Media"] = new JsonObject { ["MediaFileUri"] = Media.IsMatch(media) ? media : throw new InvalidOperationException($"'{media}' is not a recording's S3 address - s3://kafe-ljud/kassa/1042.mp3 (media).") };
                        if (Values.Get(p, "language") is { } language)
                        {
                            body["LanguageCode"] = Language.IsMatch(language) ? language : throw new InvalidOperationException($"'{language}' is not a language Transcribe names - sv-SE, en-US (language).");
                        }
                        else
                        {
                            body["IdentifyLanguage"] = true;
                        }

                        var settings = new JsonObject();
                        if (Flag(p, "channels") == true)
                        {
                            settings["ChannelIdentification"] = true;
                        }

                        if (Whole(p, "alternatives") is { } alternatives)
                        {
                            settings["ShowAlternatives"] = true;
                            settings["MaxAlternatives"] = alternatives;
                        }

                        if (Whole(p, "speakers") is { } speakers)
                        {
                            settings["ShowSpeakerLabels"] = true;
                            settings["MaxSpeakerLabels"] = speakers;
                        }

                        if (Values.Get(p, "vocabulary") is { } vocabulary)
                        {
                            settings["VocabularyName"] = Name.IsMatch(vocabulary) ? vocabulary : throw new InvalidOperationException($"'{vocabulary}' is not a vocabulary's name (vocabulary).");
                        }

                        if (Values.Get(p, "vocabulary_filter") is { } filter)
                        {
                            settings["VocabularyFilterName"] = Name.IsMatch(filter) ? filter : throw new InvalidOperationException($"'{filter}' is not a vocabulary filter's name (vocabulary_filter).");
                            settings["VocabularyFilterMethod"] = (Values.Get(p, "filter_method") ?? "remove").ToLowerInvariant() is ("remove" or "mask" or "tag") and var method ? method : throw new InvalidOperationException($"'{Values.Get(p, "filter_method")}' is not remove, mask or tag (filter_method).");
                        }

                        if (settings.Count > 0)
                        {
                            body["Settings"] = settings;
                        }

                        break;
                    }

                case "jobs":
                    if (Values.Get(p, "status") is { } status)
                    {
                        body["Status"] = Statuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{status}' is not QUEUED, IN_PROGRESS, FAILED or COMPLETED (status).");
                    }

                    if (Values.Get(p, "name_contains") is { } part)
                    {
                        body["JobNameContains"] = Name.IsMatch(part) ? part : throw new InvalidOperationException($"'{part}' is not a part of a job's name (name_contains).");
                    }

                    break;
                default:
                    body["TranscriptionJobName"] = job;
                    break;
            }

            var withTranscript = Flag(p, "transcript") ?? true;
            var simple = Flag(p, "simple") ?? true;
            var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "start_job":
                    yield return Record((await session.CallAsync("StartTranscriptionJob", body, cancellationToken))?["TranscriptionJob"] as JsonObject ?? new JsonObject());
                    break;
                case "job":
                    {
                        var found = (await session.CallAsync("GetTranscriptionJob", body, cancellationToken))?["TranscriptionJob"] as JsonObject ?? new JsonObject();
                        // Transcribe gives a transcript's address once the job is COMPLETED, and only then.
                        if (!withTranscript || Text(found["Transcript"]?["TranscriptFileUri"]) is not { } address)
                        {
                            yield return Record(found); // not done, or its state alone asked for
                            break;
                        }

                        var transcript = await session.FetchJsonAsync(address, cancellationToken) as JsonObject ?? new JsonObject();
                        var results = transcript["results"] as JsonObject ?? new JsonObject();
                        var text = string.Join(" ", (results["transcripts"] as JsonArray ?? new JsonArray()).Select(part => Text(part?["transcript"])).OfType<string>());
                        var status = Text(found["TranscriptionJobStatus"]);
                        yield return simple
                            ? Fields(("job", job), ("status", status), ("language", Text(found["LanguageCode"])), ("transcript", text))
                            : Fields(("job", job), ("status", status), ("language", Text(found["LanguageCode"])), ("transcript", text), ("results", results.ToJsonString(Wire)));
                        break;
                    }

                case "jobs":
                    {
                        // Page by page by Transcribe's NextToken, a page no larger than what is still wanted (at most 100).
                        var read = 0;
                        string? next = null;
                        do
                        {
                            body["MaxResults"] = Math.Min(100, max - read);
                            if (next is null)
                            {
                                body.Remove("NextToken");
                            }
                            else
                            {
                                body["NextToken"] = next;
                            }

                            var page = await session.CallAsync("ListTranscriptionJobs", body, cancellationToken) as JsonObject;
                            foreach (var summary in (page?["TranscriptionJobSummaries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(summary);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["NextToken"]) is { Length: > 0 } token ? token : null;
                        }
                        while (next is not null);

                        break;
                    }

                default:
                    await session.CallAsync("DeleteTranscriptionJob", body, cancellationToken);
                    yield return Fields(("job", job), ("deleted", true));
                    break;
            }
        }

        private AwsJsonSession Session(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
            AwsJsonSession.Of("Amazon Transcribe", "transcribe", "Transcribe", endpoint, settings, credentials, egress, timeout, time);

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static int? Whole(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 2 and <= 10 ? value : throw new InvalidOperationException($"'{given}' is not a number from 2 to 10 ({name}).")
                : null;

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
