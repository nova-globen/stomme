#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.MicrosoftGraphSecurity
{
    /// <summary>
    /// Microsoft Graph Security (n8n's Microsoft Graph Security node: a tenant's secure scores and their control
    /// profiles): Microsoft Graph's security API with an app's client credentials - the tenant's token endpoint - through the
    /// family's token provider, over the host's egress. A test names the tenant's latest secure score.
    /// </summary>
    /// <remarks>
    /// <c>secure_scores</c> (by an OData filter, their control scores when asked, newest first as Graph gives them) and
    /// <c>secure_score</c>; <c>control_profiles</c> (by a filter) and <c>control_profile</c>; <c>update_control_profile</c> - its
    /// state (Default, Ignored or ThirdParty), a comment and whom it is assigned to, with the vendor information Graph asks for
    /// (SecureScore and Microsoft unless given), Graph answering with the profile as it is now. A list is read page by page by
    /// Graph's next link - followed only while it stays under the same collection on the same host - up to <c>max</c> (1,000). A
    /// record is its fields as Graph gives them, a list or an object as its JSON, Graph's OData annotations left out. Every id,
    /// state and flag held to its shape before a token is asked for. A throttled call waits as Graph says; a failure is thrown
    /// with its status and Graph's code and message.
    /// </remarks>
    public sealed class MicrosoftGraphSecurityConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string SecureScoresOperation = "secure_scores";

        public const string SecureScoreOperation = "secure_score";

        public const string ControlProfilesOperation = "control_profiles";

        public const string ControlProfileOperation = "control_profile";

        public const string UpdateControlProfileOperation = "update_control_profile";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[A-Za-z0-9_.\\-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]{1,1000}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SecureScoresOperation, SecureScoreOperation, ControlProfilesOperation, ControlProfileOperation, UpdateControlProfileOperation };

        private static readonly string[] States = { "Default", "Ignored", "ThirdParty" };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsoftgraphsecurity";

        public string DisplayName => "Microsoft Graph Security";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) id (Entra ID > App registrations > the app)",
            ["client-secret"] = "Client secret (Certificates & secrets)",
            ["token-url"] = "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://learn.microsoft.com/graph/security-concept-overview");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/securescore");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false, Description: "The token's scope - change it only for a sovereign cloud.") { Default = DefaultScope },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField FilterField = Field("filter", "Filter", "An OData filter - createdDateTime ge 2026-10-01T00:00:00Z.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SecureScoresOperation, "Get secure scores", "n8n's Microsoft Graph Security Secure Score Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { FilterField, Field("include_control_scores", "Control scores", "true: each score's control scores too.", ConnectorConfigFieldType.Boolean), MaxField }),
            },
            new ConnectorOperation(SecureScoreOperation, "Get a secure score", "n8n's Microsoft Graph Security Secure Score Get.") { Parameters = new ConnectorConfigSchema(new[] { Field("id", "Secure score", "Its id.", required: true) }) },
            new ConnectorOperation(ControlProfilesOperation, "Get control profiles", "n8n's Microsoft Graph Security Secure Score Control Profile Get Many.") { Parameters = new ConnectorConfigSchema(new[] { FilterField, MaxField }) },
            new ConnectorOperation(ControlProfileOperation, "Get a control profile", "n8n's Microsoft Graph Security Secure Score Control Profile Get.") { Parameters = new ConnectorConfigSchema(new[] { Field("id", "Control profile", "Its id - AdminMFAV2.", required: true) }) },
            new ConnectorOperation(UpdateControlProfileOperation, "Update a control profile", "Its state, a comment and whom it is assigned to - n8n's Microsoft Graph Security Secure Score Control Profile Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Field("id", "Control profile", "Its id - AdminMFAV2.", required: true),
                    Field("state", "State", "Default, Ignored or ThirdParty."),
                    Field("comment", "Comment", "Why.", ConnectorConfigFieldType.Multiline),
                    Field("assigned_to", "Assigned to", "Who handles it."),
                    Field("provider", "Provider", "SecureScore (the default)."),
                    Field("vendor", "Vendor", "Microsoft (the default)."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v1.0/security";
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var latest = ((await SendAsync(client, token, HttpMethod.Get, api + "/secureScores?$top=1", null, cancellationToken))?["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                return ConnectionTestResult.Ok(latest is null
                    ? "Microsoft Graph Security: no secure score yet."
                    : $"Microsoft Graph Security: the latest secure score is {Number(latest["currentScore"])} of {Number(latest["maxScore"])}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SecureScoresOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Microsoft Graph Security has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v1.0/security";

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string what) => Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what}'s id (id).") : throw new InvalidOperationException($"Give {what}'s id (id).");
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            string? TextOf(string name) => Values.Get(p, name) is { } given ? Text.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a text of 1,000 characters at most, without control characters ({name}).") : null;

            string? collection = null;
            string? path = null;
            JsonObject? body = null;
            var keepControlScores = true;
            switch (operation)
            {
                case SecureScoresOperation:
                    collection = "secureScores";
                    keepControlScores = FlagOf("include_control_scores") ?? false;
                    break;
                case ControlProfilesOperation:
                    collection = "secureScoreControlProfiles";
                    break;
                case SecureScoreOperation:
                    path = "/secureScores/" + IdOf("the secure score");
                    break;
                case ControlProfileOperation:
                    path = "/secureScoreControlProfiles/" + IdOf("the control profile");
                    break;
                default:
                    {
                        path = "/secureScoreControlProfiles/" + IdOf("the control profile");
                        body = new JsonObject
                        {
                            ["vendorInformation"] = new JsonObject { ["provider"] = TextOf("provider") ?? "SecureScore", ["vendor"] = TextOf("vendor") ?? "Microsoft" },
                        };
                        if (Values.Get(p, "state") is { } state)
                        {
                            body["state"] = States.FirstOrDefault(known => string.Equals(known, state, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{state}' is not Default, Ignored or ThirdParty (state).");
                        }

                        foreach (var (name, field) in new[] { ("comment", "comment"), ("assigned_to", "assignedTo") })
                        {
                            if (TextOf(name) is { } value)
                            {
                                body[field] = value;
                            }
                        }

                        if (body.Count == 1)
                        {
                            throw new InvalidOperationException("Give what to change - its state, a comment or whom it is assigned to.");
                        }

                        break;
                    }
            }

            var filter = collection is null ? null : Values.Get(p, "filter");
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (collection is not null)
            {
                // Graph pages by its next link: followed only while it stays under the same collection on the same host.
                var start = $"{api}/{collection}";
                var read = 0;
                string? next = start + "?$top=" + Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture) + (filter is null ? "" : "&$filter=" + Uri.EscapeDataString(filter));
                while (next is not null)
                {
                    var answer = await SendAsync(client, token, HttpMethod.Get, next, null, cancellationToken);
                    foreach (var item in (answer?["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (!keepControlScores)
                        {
                            item.Remove("controlScores");
                        }

                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    var link = answer?["@odata.nextLink"] is JsonValue value && value.TryGetValue<string>(out var given) ? given : null;
                    if (link is not null && !(link.StartsWith(start + "?", StringComparison.OrdinalIgnoreCase) && link != next))
                    {
                        throw new InvalidOperationException("Microsoft Graph named a next page outside the collection - it is not followed.");
                    }

                    next = link;
                }

                yield break;
            }

            var result = await SendAsync(client, token, body is null ? HttpMethod.Get : HttpMethod.Patch, api + path, body, cancellationToken) as JsonObject ?? new JsonObject();
            yield return Record(result);
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => !pair.Key.StartsWith("@odata.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number.ToString("0.##", CultureInfo.InvariantCulture) : "?";

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Graph's answer as JSON, a throttled call waiting as Graph says; a failure thrown with its status and Graph's code and
        // message; a success that is not JSON a failure that says so.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Microsoft Graph answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Words(json?["error"]?["code"]);
                var said = Words(json?["error"]?["message"]);
                var text = string.Join(": ", new[] { code, said }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }
    }
}
