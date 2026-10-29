#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Hunter
{
    /// <summary>
    /// Hunter (BL-229, n8n's Hunter node: a domain's email addresses, a person's address found, an address verified) over Hunter's
    /// API v2 (<c>https://api.hunter.io/v2</c>) by an API key in its <c>X-API-KEY</c> header - never in the address, where a proxy
    /// would log it. A test reads the account: its plan and the searches and verifications left.
    /// </summary>
    /// <remarks>
    /// <c>domain_search</c> (a domain's or a company's addresses - personal or generic, by seniority and department - page by page
    /// by offset, 100 a page, up to <c>max</c>: each address a record, n8n's Only Emails, or the domain's one record with them
    /// all); <c>email_finder</c> (a person's address at a domain or a company, by their names, with Hunter's score);
    /// <c>email_verifier</c> (an address verified - asked again while Hunter says it is still verifying, a few times); <c>account</c>
    /// (the plan and what is left of it). Every domain, name, address and choice held to Hunter's shape before anything is sent;
    /// Hunter's error id and details on a failure.
    /// </remarks>
    public sealed class HunterConnector(TimeSpan? pollInterval = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.hunter.io";

        public const int DefaultMax = 100;

        private const int PageSize = 100; // Hunter's most a call

        private const int VerifyTries = 5;

        private static readonly string[] Known = { "domain_search", "email_finder", "email_verifier", "account" };

        private static readonly string[] Seniorities = { "junior", "senior", "executive" };

        private static readonly string[] Departments = { "executive", "it", "finance", "management", "sales", "legal", "support", "hr", "marketing", "communication", "education", "design", "health", "operations" };

        private static readonly Regex Domain = new("^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex Address = new("^[^@\\s]{1,64}@[^@\\s]{1,255}$", RegexOptions.CultureInvariant);

        private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(2);

        public string ConnectorType => "hunter";

        public string DisplayName => "Hunter";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://hunter.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://hunter.io/api-documentation/v2");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("domain_search", "Search a domain", "A domain's email addresses - n8n's Domain Search.")
            {
                Parameters = new(new[]
                {
                    Field("domain", "Domain", "kafe.example - or a company instead."),
                    Field("company", "Company", "Its name, when no domain is given - Kafé Kassan."),
                    Field("only_emails", "Only the addresses", "true (the default): each address a record. false: the domain's one record, its addresses among its fields."),
                    Field("type", "Type", "personal or generic. Empty: both."),
                    Field("seniority", "Seniority", "junior, senior, executive - by commas."),
                    Field("department", "Department", "executive, it, finance, management, sales, legal, support, hr, marketing, communication, education, design, health, operations - by commas."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many addresses, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("email_finder", "Find an address", "A person's address - n8n's Email Finder.")
            {
                Parameters = new(new[]
                {
                    Field("domain", "Domain", "kafe.example - or a company instead."),
                    Field("company", "Company", "Its name, when no domain is given."),
                    Field("first_name", "First name", "Ada."),
                    Field("last_name", "Last name", "Lovelace."),
                    Field("full_name", "Full name", "Ada Lovelace - instead of the two."),
                }),
            },
            new ConnectorOperation("email_verifier", "Verify an address", "Whether an address is deliverable - n8n's Email Verifier.") { Parameters = new(new[] { Field("email", "Address", "ada@kafe.example.", required: true) }) },
            new ConnectorOperation("account", "Get the account", "The plan, and the searches and verifications left."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var (_, answer) = await session.GetAsync("/account", Array.Empty<(string, string)>(), cancellationToken);
                var data = answer?["data"] as JsonObject ?? throw new InvalidOperationException("Hunter answered with no account - check the address.");
                var searches = data["requests"]?["searches"];
                return ConnectionTestResult.Ok($"Hunter answers - {Text(data["plan_name"]) ?? "its"} plan, {Number(searches?["available"]) - Number(searches?["used"])} searches left.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "domain_search").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Hunter has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var query = new List<(string Name, string Value)>();
            var onlyEmails = true;
            var max = DefaultMax;
            switch (operation)
            {
                case "domain_search":
                    Where(p, query);
                    if (Values.Get(p, "only_emails") is { } only)
                    {
                        onlyEmails = bool.TryParse(only, out var flag) ? flag : throw new InvalidOperationException($"'{only}' is not true or false (only_emails).");
                    }

                    if (Values.Get(p, "type") is { } type)
                    {
                        query.Add(("type", type.ToLowerInvariant() is ("personal" or "generic") and var known ? known : throw new InvalidOperationException($"'{type}' is not personal or generic (type).")));
                    }

                    foreach (var (name, allowed) in new[] { ("seniority", Seniorities), ("department", Departments) })
                    {
                        if (Values.Get(p, name) is { } given)
                        {
                            var each = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
                            query.Add((name, string.Join(",", each.Select(value => allowed.Contains(value) ? value : throw new InvalidOperationException($"'{value}' is not one of {string.Join(", ", allowed)} ({name}).")))));
                        }
                    }

                    max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
                    break;
                case "email_finder":
                    Where(p, query);
                    if (Values.Get(p, "full_name") is { } full && Values.Get(p, "first_name") is null && Values.Get(p, "last_name") is null)
                    {
                        query.Add(("full_name", full));
                    }
                    else
                    {
                        query.Add(("first_name", Values.Get(p, "first_name") ?? throw new InvalidOperationException("Give the person's first and last name (first_name, last_name) - or their full name (full_name).")));
                        query.Add(("last_name", Values.Get(p, "last_name") ?? throw new InvalidOperationException("Give the person's last name too (last_name).")));
                    }

                    break;
                case "email_verifier":
                    {
                        var email = Values.Get(p, "email") ?? throw new InvalidOperationException("Give the address to verify - ada@kafe.example (email).");
                        query.Add(("email", Address.IsMatch(email) ? email : throw new InvalidOperationException($"'{email}' is not an email address (email).")));
                        break;
                    }
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "domain_search":
                    {
                        JsonObject? domain = null;
                        var emails = new JsonArray();
                        var read = 0;
                        while (true)
                        {
                            var page = new List<(string, string)>(query) { ("limit", Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)), ("offset", read.ToString(CultureInfo.InvariantCulture)) };
                            var (_, answer) = await session.GetAsync("/domain-search", page, cancellationToken);
                            var data = answer?["data"] as JsonObject ?? new JsonObject();
                            domain ??= data;
                            var found = (data["emails"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var email in found)
                            {
                                if (onlyEmails)
                                {
                                    yield return Record(email);
                                }
                                else
                                {
                                    emails.Add(email.DeepClone());
                                }

                                if (++read >= max)
                                {
                                    break;
                                }
                            }

                            var total = answer?["meta"]?["results"] is JsonValue results && results.TryGetValue<int>(out var all) ? all : 0;
                            if (read >= max || found.Count == 0 || read >= total)
                            {
                                break;
                            }
                        }

                        if (!onlyEmails)
                        {
                            domain!["emails"] = emails;
                            yield return Record(domain);
                        }

                        break;
                    }

                case "email_finder":
                    {
                        var (_, answer) = await session.GetAsync("/email-finder", query, cancellationToken);
                        yield return Record(answer?["data"] as JsonObject ?? new JsonObject());
                        break;
                    }

                case "email_verifier":
                    {
                        // Hunter answers 202 while it is still verifying: asked again, a few times, before it is said.
                        for (var attempt = 1; ; attempt++)
                        {
                            var (status, answer) = await session.GetAsync("/email-verifier", query, cancellationToken);
                            if (status != HttpStatusCode.Accepted)
                            {
                                yield return Record(answer?["data"] as JsonObject ?? new JsonObject());
                                break;
                            }

                            if (attempt == VerifyTries)
                            {
                                throw new InvalidOperationException($"Hunter was still verifying {query[0].Value} after {VerifyTries} asks - try again later.");
                            }

                            await Task.Delay(poll, cancellationToken);
                        }

                        break;
                    }

                default:
                    {
                        var (_, answer) = await session.GetAsync("/account", Array.Empty<(string, string)>(), cancellationToken);
                        yield return Record(answer?["data"] as JsonObject ?? new JsonObject());
                        break;
                    }
            }
        }

        // A domain, or a company's name when no domain is given - Hunter takes the domain when both are.
        private static void Where(IReadOnlyDictionary<string, string> p, List<(string Name, string Value)> query)
        {
            if (Values.Get(p, "domain") is { } domain)
            {
                var plain = domain.Trim().ToLowerInvariant();
                plain = Stomme.Shared.Utilities.TryCreateAbsoluteUri(plain, out var site) && site.Scheme is "http" or "https" ? site.Host : plain; // n8n's users give a site's address too
                query.Add(("domain", Domain.IsMatch(plain) ? plain : throw new InvalidOperationException($"'{domain}' is not a domain - kafe.example (domain).")));
            }
            else
            {
                query.Add(("company", Values.Get(p, "company") ?? throw new InvalidOperationException("Give the domain - kafe.example (domain) - or the company's name (company).")));
            }
        }

        // An object as a record: its plain values as they are, a list or an object as JSON.
        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(),
        }, StringComparer.Ordinal));

        private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string key;

            private Session(HttpClient client, string api, string key)
            {
                this.client = client;
                this.api = api;
                this.key = key;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/v2", StringComparison.Ordinal))
                {
                    root = root[..^"/v2".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Hunter's address - its own, {DefaultEndpoint}.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Hunter connection has no API key.");
                return new Session(egress.CreateHttpClient(timeout), root + "/v2", key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A GET with the key in its header; the status and JSON, or Hunter's error id and details.</summary>
            public async Task<(HttpStatusCode Status, JsonNode? Json)> GetAsync(string route, IEnumerable<(string Name, string Value)> query, CancellationToken cancellationToken)
            {
                var address = api + route + (query.Any() ? "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Name) + "=" + Uri.EscapeDataString(pair.Value))) : string.Empty);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, address);
                    request.Headers.Add("X-API-KEY", key);
                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Hunter answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is JsonObject || response.StatusCode == HttpStatusCode.Accepted
                        ? (response.StatusCode, json)
                        : throw new InvalidOperationException("Hunter answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                var error = (json?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var said = Text(error?["details"]);
                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"Hunter answered HTTP {status}{(Text(error?["id"]) is { } id ? " " + id : string.Empty)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}"
                    + (status == 401 ? " - check the API key." : status is 403 or 429 ? " - Hunter's rate or the plan's searches are spent; try again later." : string.Empty));
            }
        }
    }
}
