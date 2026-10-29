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

namespace Stomme.Modules.Connectors.Pushover
{
    /// <summary>
    /// Pushover (BL-229, n8n's Pushover node: a message pushed to a user's devices): Pushover's API with an application's token,
    /// over the host's egress. A test names how many messages the token has left this month.
    /// </summary>
    /// <remarks>
    /// <c>push</c> sends a message to a user or group key - a title, a priority from -2 to 2 (an emergency one retried and
    /// expiring as asked), an address and its title, a device, a sound, HTML or monospace, a time and a time to live;
    /// <c>validate_user</c> checks a user key (and a device); <c>receipt</c> reads an emergency message's receipt and
    /// <c>cancel_receipt</c> stops its retries; <c>sounds</c> lists the sounds. The token goes in a POST's form, as Pushover
    /// takes it - in a GET's query, as Pushover takes it there, and no message this connector writes names the address. Every
    /// key, priority, number, time, sound and device held to its shape before anything is sent; Pushover's status 0 a failure
    /// with its errors. A throttled call waits as Pushover says.
    /// </remarks>
    public sealed class PushoverConnector : IConnector, IConnectorExtractor
    {
        public const string PushOperation = "push";

        public const string ValidateUserOperation = "validate_user";

        public const string ReceiptOperation = "receipt";

        public const string CancelReceiptOperation = "cancel_receipt";

        public const string SoundsOperation = "sounds";

        private const string DefaultEndpoint = "https://api.pushover.net/1";

        private static readonly Regex Key = new("^[A-Za-z0-9]{30}$", RegexOptions.CultureInvariant);

        private static readonly Regex Device = new("^[A-Za-z0-9_-]{1,25}$", RegexOptions.CultureInvariant);

        private static readonly Regex Sound = new("^[a-z0-9_-]{1,40}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { PushOperation, ValidateUserOperation, ReceiptOperation, CancelReceiptOperation, SoundsOperation };

        public string ConnectorType => "pushover";

        public string DisplayName => "Pushover";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (Your Applications > the application)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://pushover.net/");

        public Uri ApiDocumentationUrl { get; } = new("https://pushover.net/api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "A user's or a group's key - 30 letters and digits.", required: true);

        private static readonly ConnectorConfigField ReceiptField = Field("receipt", "Receipt", "An emergency message's receipt - 30 letters and digits.", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PushOperation, "Push a message", "n8n's Pushover Message Push.")
            {
                Parameters = Schema(
                    UserField,
                    Field("message", "Message", "What it says.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("title", "Title", "Its title."),
                    Field("priority", "Priority", "-2 (lowest) to 2 (emergency); 0 by default.", ConnectorConfigFieldType.Integer),
                    Field("retry", "Retry", "An emergency message's seconds between retries - 30 at least.", ConnectorConfigFieldType.Integer),
                    Field("expire", "Expire", "An emergency message's seconds of retrying - 10800 at most.", ConnectorConfigFieldType.Integer),
                    Field("url", "Address", "An address shown with it."),
                    Field("url_title", "Address title", "The address's title."),
                    Field("device", "Device", "One device of the user's."),
                    Field("sound", "Sound", "A sound's name - pushover."),
                    Field("html", "HTML", "true: its message is HTML.", ConnectorConfigFieldType.Boolean),
                    Field("monospace", "Monospace", "true: its message in a fixed font.", ConnectorConfigFieldType.Boolean),
                    Field("timestamp", "Time", "When it happened - 2026-10-13T10:00:00Z."),
                    Field("ttl", "Time to live", "Seconds before it is deleted from the devices.", ConnectorConfigFieldType.Integer)),
            },
            new ConnectorOperation(ValidateUserOperation, "Validate a user", "A user's or a group's key, and a device.") { Parameters = Schema(UserField, Field("device", "Device", "One device of the user's.")) },
            new ConnectorOperation(ReceiptOperation, "Get a receipt", "An emergency message's.") { Parameters = Schema(ReceiptField) },
            new ConnectorOperation(CancelReceiptOperation, "Cancel a receipt", "An emergency message's retries stopped.") { Parameters = Schema(ReceiptField) },
            new ConnectorOperation(SoundsOperation, "Get sounds", "The sounds a message can make.") { Parameters = Schema() },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var limits = await session.GetAsync("/apps/limits.json", cancellationToken);
                return limits?["remaining"] is JsonValue remaining && remaining.TryGetValue<long>(out var left) && limits["limit"] is JsonValue limit && limit.TryGetValue<long>(out var total)
                    ? ConnectionTestResult.Ok($"Pushover: the token has {left.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} messages left this month.")
                    : ConnectionTestResult.Ok("Pushover: the token is an application's.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail("Pushover could not be reached.");
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PushOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Pushover has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            string KeyOf(string name, string what) => Values.Get(p, name) is { } given ? Key.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {what} - 30 letters and digits ({name}).") : throw new InvalidOperationException($"Give the {what}.");
            int? NumberOf(string name, int least, int most) => Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= least && number <= most ? number : throw new InvalidOperationException($"'{given}' is not a whole number from {least} to {most} ({name}).")
                : null;
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

            var form = new List<KeyValuePair<string, string>>();
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    form.Add(new(name, value));
                }
            }

            string path;
            switch (operation)
            {
                case PushOperation:
                    {
                        Add("user", KeyOf("user", "user's or group's key"));
                        Add("message", Values.Get(p, "message") ?? throw new InvalidOperationException("Give the message."));
                        Add("title", Values.Get(p, "title"));
                        var priority = NumberOf("priority", -2, 2);
                        Add("priority", priority?.ToString(CultureInfo.InvariantCulture));
                        var retry = NumberOf("retry", 30, 10800);
                        var expire = NumberOf("expire", 1, 10800);
                        if (priority == 2)
                        {
                            Add("retry", (retry ?? throw new InvalidOperationException("An emergency message needs its retry - 30 seconds at least.")).ToString(CultureInfo.InvariantCulture));
                            Add("expire", (expire ?? throw new InvalidOperationException("An emergency message needs its expire - 10800 seconds at most.")).ToString(CultureInfo.InvariantCulture));
                        }
                        else if (retry is not null || expire is not null)
                        {
                            throw new InvalidOperationException("Only an emergency message (priority 2) is retried and expires.");
                        }

                        if (Values.Get(p, "url") is { } url)
                        {
                            Add("url", Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out _) ? url : throw new InvalidOperationException($"'{url}' is not an address (url)."));
                        }

                        Add("url_title", Values.Get(p, "url_title"));
                        Add("device", Values.Get(p, "device") is { } device ? Device.IsMatch(device) ? device : throw new InvalidOperationException($"'{device}' is not a device's name - letters, digits, '-' and '_' (device).") : null);
                        Add("sound", Values.Get(p, "sound") is { } sound ? Sound.IsMatch(sound) ? sound : throw new InvalidOperationException($"'{sound}' is not a sound's name - pushover (sound).") : null);
                        var html = FlagOf("html");
                        var monospace = FlagOf("monospace");
                        if (html == true && monospace == true)
                        {
                            throw new InvalidOperationException("A message is HTML or monospace - not both.");
                        }

                        Add("html", html == true ? "1" : null);
                        Add("monospace", monospace == true ? "1" : null);
                        if (Values.Get(p, "timestamp") is { } time)
                        {
                            Add("timestamp", DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && time.Contains('T')
                                ? when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                                : throw new InvalidOperationException($"'{time}' is not a time - 2026-10-13T10:00:00Z (timestamp)."));
                        }

                        Add("ttl", NumberOf("ttl", 1, int.MaxValue)?.ToString(CultureInfo.InvariantCulture));
                        path = "/messages.json";
                        break;
                    }

                case ValidateUserOperation:
                    Add("user", KeyOf("user", "user's or group's key"));
                    Add("device", Values.Get(p, "device") is { } validated ? Device.IsMatch(validated) ? validated : throw new InvalidOperationException($"'{validated}' is not a device's name - letters, digits, '-' and '_' (device).") : null);
                    path = "/users/validate.json";
                    break;
                case ReceiptOperation:
                    path = $"/receipts/{KeyOf("receipt", "receipt")}.json";
                    break;
                case CancelReceiptOperation:
                    path = $"/receipts/{KeyOf("receipt", "receipt")}/cancel.json";
                    break;
                default:
                    path = "/sounds.json";
                    break;
            }

            using var client = session.Client;
            switch (operation)
            {
                case SoundsOperation:
                    foreach (var (sound, name) in (await session.GetAsync(path, cancellationToken))?["sounds"] as JsonObject ?? new JsonObject())
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["sound"] = sound, ["name"] = Text(name) });
                    }

                    yield break;
                case ReceiptOperation:
                    yield return Record(await session.GetAsync(path, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    yield return Record(await session.PostAsync(path, form, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
            }
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        // Pushover answers with texts and numbers - its flags are 0 and 1 - a list or an object as its JSON.
        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Pushover's API with the application's token in a POST's form and a GET's query, as Pushover
        // takes it; a throttled call waiting as Pushover says; status 0 a failure with Pushover's errors - never the address.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Pushover connection has no API token."), egress.CreateHttpClient(timeout));

            public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken) =>
                SendAsync(() => new HttpRequestMessage(HttpMethod.Get, baseUrl + path + "?token=" + Uri.EscapeDataString(token)), cancellationToken);

            public Task<JsonNode?> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
            {
                var fields = form.Prepend(new("token", token)).ToList();
                return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = new FormUrlEncodedContent(fields) }, cancellationToken);
            }

            private async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, request, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Pushover's - a sign-in page, a proxy's - read as sent would hide that it never was.
                    throw new InvalidOperationException($"Pushover answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                var errors = (json as JsonObject)?["errors"] is JsonArray list ? string.Join("; ", list.Select(Text).OfType<string>()) : null;
                var status = (json as JsonObject)?["status"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : (long?)null;
                if (!response.IsSuccessStatusCode || status != 1)
                {
                    // Pushover says ok with its status 1 - anything else, a 200 among them, is a refusal.
                    throw new InvalidOperationException($"{(response.IsSuccessStatusCode ? "Pushover refused it" : $"HTTP {(int)response.StatusCode}")}{(string.IsNullOrEmpty(errors) ? "." : ": " + (errors.Length > 300 ? errors[..300] : errors))}");
                }

                return json;
            }
        }
    }
}
