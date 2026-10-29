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

namespace Stomme.Modules.Connectors.GoogleSlides
{
    /// <summary>
    /// Google Slides (BL-229, n8n's Google Slides node) over the Slides API (<c>https://slides.googleapis.com/v1</c>) as a person
    /// signed in - an OAuth client's refresh token granted the presentations scope, exchanged at Google's token endpoint through
    /// the shared <see cref="OAuth2TokenProvider"/>, as Gmail's is. A test reads a presentation the connection names, or asks for
    /// a token alone.
    /// </summary>
    /// <remarks>
    /// <c>create</c> (a presentation by its title - n8n's Create); <c>presentation</c> (its title, locale, size, revision and slides
    /// - n8n's Get); <c>slides</c> (each slide, up to <c>max</c> - n8n's Get Slides); <c>replace_text</c> (texts found and replaced
    /// across the presentation or on the slides named, by case if asked, under a revision if given - a {{placeholder}} by its name
    /// alone if asked, so no braces meet a step's expressions - n8n's Replace Text, its revision honoured as n8n's was not); <c>page</c> (one slide - n8n's Page Get); <c>thumbnail</c> (a slide's image's address
    /// and size, and with <c>file</c> the PNG itself saved into the run's pipeline folder - fetched from Google's own address,
    /// which carries its own access, without our token - n8n's Get Thumbnail). Every id held to Google's shape before a token is
    /// asked for; Google's status and message on a failure, a 401 or 403 pointing at the token's scopes.
    /// </remarks>
    public sealed class GoogleSlidesConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://slides.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const int DefaultMax = 1000;

        private static readonly string[] Known = { "create", "presentation", "slides", "replace_text", "page", "thumbnail" };

        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googleslides";

        public string DisplayName => "Google Slides";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted presentations)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://workspace.google.com/products/slides/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/workspace/slides/api/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
            new ConnectorConfigField("presentation", "Presentation", ConnectorConfigFieldType.Text, Required: false, Description: "A presentation's id a test reads, and a step names none of."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField PresentationField = Field("presentation", "Presentation", "Its id - from its address, docs.google.com/presentation/d/<id>. Empty: the connection's.");

        private static readonly ConnectorConfigField PageField = Field("page", "Slide", "The slide's object id - from Get slides.", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("create", "Create a presentation", "An empty one by its title - n8n's Create.") { Parameters = new(new[] { Field("title", "Title", "Its title - Kassans veckomöte.", required: true) }) },
            new ConnectorOperation("presentation", "Get a presentation", "Its title, size, revision and slides - n8n's Get.") { Parameters = new(new[] { PresentationField }) },
            new ConnectorOperation("slides", "Get slides", "Each slide - n8n's Get Slides.")
            {
                Parameters = new(new[] { PresentationField, new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation("replace_text", "Replace text", "Texts found and replaced - n8n's Replace Text.")
            {
                Parameters = new(new[]
                {
                    PresentationField,
                    Field("texts", "Texts", "A JSON object of what to find and what to put - {\"{{name}}\": \"Ada\", \"{{date}}\": \"17 oktober\"}.", required: true),
                    Field("placeholders", "Placeholders", "true: each text to find is a {{placeholder}}, its name given alone - {\"name\": \"Ada\"} finds {{name}} - so no braces meet the step's expressions.", ConnectorConfigFieldType.Boolean),
                    Field("match_case", "Match case", "true: found only with its case.", ConnectorConfigFieldType.Boolean),
                    Field("pages", "On slides", "The slides' object ids, by commas. Empty: every slide."),
                    Field("revision", "Revision", "The revision it must still be - from Get a presentation - or nothing is changed."),
                }),
            },
            new ConnectorOperation("page", "Get a slide", "One slide's elements - n8n's Page Get.") { Parameters = new(new[] { PresentationField, PageField }) },
            new ConnectorOperation("thumbnail", "Get a slide's thumbnail", "Its image's address and size, the PNG saved if asked - n8n's Get Thumbnail.")
            {
                Parameters = new(new[]
                {
                    PresentationField,
                    PageField,
                    Field("size", "Size", "small, medium or large (the default)."),
                    Field("file", "Save to", "A file in the run's pipeline folder - kassa/slide1.png, or a folder ending in / for <slide>.png."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var presentation = Values.Get(context.Settings, "presentation") is { } given ? IdOf(given, "presentation") : null;
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                if (presentation is null)
                {
                    return ConnectionTestResult.Ok("Google Slides: the refresh token was exchanged for an access token.");
                }

                var found = await session.JsonAsync(HttpMethod.Get, $"/presentations/{presentation}?fields=title", null, cancellationToken);
                return ConnectionTestResult.Ok($"Google Slides answers: '{Text(found?["title"])}'.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "presentation").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Slides has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var presentation = operation == "create" ? string.Empty
                : (Values.Get(p, "presentation") ?? Values.Get(context.Settings, "presentation")) is { } named ? IdOf(named, "presentation")
                : throw new InvalidOperationException("Give the presentation's id (presentation), or the connection's.");
            var page = operation is "page" or "thumbnail" ? IdOf(Values.Get(p, "page") ?? throw new InvalidOperationException("Give the slide's object id (page)."), "page") : string.Empty;
            JsonObject? body = null;
            string? file = null;
            var size = "LARGE";
            switch (operation)
            {
                case "create":
                    body = new JsonObject { ["title"] = Values.Get(p, "title") ?? throw new InvalidOperationException("Give the presentation's title (title).") };
                    break;
                case "replace_text":
                    {
                        JsonObject? texts;
                        try
                        {
                            texts = Values.Get(p, "texts") is { } given ? JsonNode.Parse(given) as JsonObject : null;
                        }
                        catch (JsonException)
                        {
                            texts = null;
                        }

                        if (texts is null || texts.Count == 0 || texts.Any(pair => pair.Key.Length == 0 || pair.Value is not JsonValue value || !value.TryGetValue<string>(out _)))
                        {
                            throw new InvalidOperationException("The texts are a JSON object of what to find and the text to put - {\"{{name}}\": \"Ada\"} (texts).");
                        }

                        var placeholders = Flag(p, "placeholders") ?? false;
                        if (placeholders && texts.Any(pair => pair.Key.Contains('{') || pair.Key.Contains('}')))
                        {
                            throw new InvalidOperationException("With placeholders, a text to find is its name alone - {\"name\": \"Ada\"} for {{name}} (texts).");
                        }

                        var matchCase = Flag(p, "match_case") ?? false;
                        var pages = Values.Get(p, "pages") is { } listed
                            ? listed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(each => IdOf(each, "pages")).Distinct().ToList()
                            : new List<string>();
                        var requests = new JsonArray();
                        foreach (var (find, put) in texts)
                        {
                            var replace = new JsonObject
                            {
                                ["containsText"] = new JsonObject { ["text"] = placeholders ? "{{" + find + "}}" : find, ["matchCase"] = matchCase },
                                ["replaceText"] = put!.GetValue<string>(),
                            };
                            if (pages.Count > 0)
                            {
                                replace["pageObjectIds"] = new JsonArray(pages.Select(each => (JsonNode?)each).ToArray());
                            }

                            requests.Add(new JsonObject { ["replaceAllText"] = replace });
                        }

                        body = new JsonObject { ["requests"] = requests };
                        if (Values.Get(p, "revision") is { } revision)
                        {
                            body["writeControl"] = new JsonObject { ["requiredRevisionId"] = revision.Length <= 256 && revision.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? revision : throw new InvalidOperationException($"'{revision}' is not a revision's id - from Get a presentation (revision).") };
                        }

                        break;
                    }

                case "thumbnail":
                    size = (Values.Get(p, "size") ?? "large").ToUpperInvariant() is ("SMALL" or "MEDIUM" or "LARGE") and var chosen ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "size")}' is not small, medium or large (size).");
                    if (Values.Get(p, "file") is { } target)
                    {
                        file = target.EndsWith('/') ? target + page + ".png" : target;
                        if (context.Files is null)
                        {
                            throw new InvalidOperationException("A thumbnail is saved into the run's pipeline folder - run it from a pipeline (file).");
                        }
                    }

                    break;
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case "create":
                    yield return Record(await session.JsonAsync(HttpMethod.Post, "/presentations", body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "presentation":
                    {
                        var found = await session.JsonAsync(HttpMethod.Get, $"/presentations/{presentation}", null, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Record(found);
                        break;
                    }

                case "slides":
                    {
                        var found = await session.JsonAsync(HttpMethod.Get, $"/presentations/{presentation}?fields=slides", null, cancellationToken);
                        foreach (var slide in (found?["slides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                        {
                            yield return Record(slide);
                        }

                        break;
                    }

                case "replace_text":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, $"/presentations/{presentation}:batchUpdate", body, cancellationToken);
                        var replies = (answer?["replies"] as JsonArray ?? new JsonArray()).ToList();
                        var requests = (JsonArray)body!["requests"]!;
                        for (var i = 0; i < requests.Count; i++)
                        {
                            var asked = requests[i]!["replaceAllText"]!;
                            var changed = i < replies.Count && replies[i]?["replaceAllText"]?["occurrencesChanged"] is JsonValue count && count.TryGetValue<int>(out var times) ? times : 0;
                            yield return Fields(("find", Text(asked["containsText"]!["text"])), ("replace", Text(asked["replaceText"])), ("occurrencesChanged", changed), ("revision", Text(answer?["writeControl"]?["requiredRevisionId"])));
                        }

                        break;
                    }

                case "page":
                    yield return Record(await session.JsonAsync(HttpMethod.Get, $"/presentations/{presentation}/pages/{page}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                default:
                    {
                        var found = await session.JsonAsync(HttpMethod.Get, $"/presentations/{presentation}/pages/{page}/thumbnail?thumbnailProperties.thumbnailSize={size}&thumbnailProperties.mimeType=PNG", null, cancellationToken) as JsonObject ?? new JsonObject();
                        var address = Text(found["contentUrl"]);
                        long? written = null;
                        if (file is not null)
                        {
                            written = await session.SaveAsync(address ?? throw new InvalidOperationException("Google Slides gave no thumbnail address."), file, context.Files!, cancellationToken);
                        }

                        yield return Fields(("page", page), ("contentUrl", address), ("width", Number(found["width"])), ("height", Number(found["height"])), ("file", file), ("size", written));
                        break;
                    }
            }
        }

        private static string IdOf(string given, string name) =>
            Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id Google Slides gives - letters, digits, _ and - ({name}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

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

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;

            private Session(HttpClient client, string token, string api)
            {
                this.client = client;
                this.token = token;
                this.api = api;
            }

            public static async Task<Session> OpenAsync(GoogleSlidesConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/v1", StringComparison.Ordinal))
                {
                    root = root[..^"/v1".Length]; // the API's versioned address given: its own
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Google Slides' address - its own, {DefaultEndpoint}.");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, root + "/v1");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's JSON, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Google Slides answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json;
                }

                var status = (int)response.StatusCode;
                var error = json?["error"];
                var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                throw new InvalidOperationException($"Google Slides answered HTTP {status}{(Text(error?["status"]) is { } kind ? " " + kind : string.Empty)}{(message is null ? "." : ": " + message)}"
                    + (status is 401 or 403 ? " - check the refresh token and its scopes." : string.Empty));
            }

            /// <summary>
            /// The thumbnail Google answered with, saved into the run's folder: its address carries its own access, so it is
            /// fetched without our token - an https address, or the API's own host (a stand-in's) - and held to the folder's size.
            /// </summary>
            public async Task<long> SaveAsync(string address, string file, IConnectorFiles files, CancellationToken cancellationToken)
            {
                if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(address, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !string.Equals(uri.Host, new Uri(api).Host, StringComparison.OrdinalIgnoreCase)) || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException("Google Slides answered with a thumbnail address that is not one to fetch - an https address.");
                }

                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Google Slides' thumbnail answered HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
                {
                    throw new InvalidOperationException($"The thumbnail is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await files.WriteAsync(file, content, cancellationToken);
            }
        }
    }
}
