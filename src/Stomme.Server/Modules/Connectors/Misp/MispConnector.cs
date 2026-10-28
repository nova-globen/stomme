#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.Misp
{
    /// <summary>
    /// MISP (BL-229, n8n's MISP node: events, their attributes and tags) over a MISP instance's REST API by an authentication key
    /// in its <c>Authorization</c> header, as MISP takes it - never in the address. A test asks MISP for its version.
    /// </summary>
    /// <remarks>
    /// Events: <c>events</c> (MISP's index, page by page), <c>event</c>, <c>event_create</c> (its info, distribution, threat
    /// level, analysis, sharing group, date and organisation), <c>event_update</c> (only what is given), <c>event_delete</c>,
    /// <c>event_publish</c>, <c>event_unpublish</c>, <c>event_search</c> (MISP's restSearch, without the attributes - by value,
    /// text anywhere, tags, type, category, whether published or deleted, when published or changed, or MISP's own JSON from the step's Query box, as n8n's
    /// node took it), <c>event_tag_add</c> and <c>event_tag_remove</c>. Attributes: <c>attributes</c>, <c>attribute</c>,
    /// <c>attribute_create</c> (on an event, of any MISP type, with its category, IDS flag, comment and distribution),
    /// <c>attribute_update</c>, <c>attribute_delete</c> and <c>attribute_search</c>. Tags: <c>tags</c>, <c>tag_create</c>,
    /// <c>tag_update</c> and <c>tag_delete</c>. A list page by page, its page the same size each time - MISP counts its pages by
    /// it - up to <c>max</c>; an event without its attributes, as n8n's node gives it. Every id, choice, colour, date and length
    /// held to MISP's shape before anything is sent; MISP's message and its field errors on a failure.
    /// </remarks>
    public sealed class MispConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100;

        private const int MostInfo = 65535;

        private static readonly string[] Known =
        {
            "events", "event", "event_create", "event_update", "event_delete", "event_publish", "event_unpublish", "event_search", "event_tag_add", "event_tag_remove",
            "attributes", "attribute", "attribute_create", "attribute_update", "attribute_delete", "attribute_search",
            "tags", "tag_create", "tag_update", "tag_delete",
        };

        // MISP's distribution levels by number and by the words its interface uses; 5 (Inherit event) an attribute's alone.
        private static readonly Dictionary<string, int> Distributions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = 0, ["organisation"] = 0, ["organization"] = 0, ["1"] = 1, ["community"] = 1, ["2"] = 2, ["connected"] = 2,
            ["3"] = 3, ["all"] = 3, ["4"] = 4, ["sharing_group"] = 4, ["5"] = 5, ["inherit"] = 5,
        };

        private static readonly Dictionary<string, int> ThreatLevels = new(StringComparer.OrdinalIgnoreCase) { ["1"] = 1, ["high"] = 1, ["2"] = 2, ["medium"] = 2, ["3"] = 3, ["low"] = 3, ["4"] = 4, ["undefined"] = 4 };

        private static readonly Dictionary<string, int> Analyses = new(StringComparer.OrdinalIgnoreCase) { ["0"] = 0, ["initial"] = 0, ["1"] = 1, ["ongoing"] = 1, ["2"] = 2, ["complete"] = 2, ["completed"] = 2 };

        private static readonly Regex Number = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Type = new("^[A-Za-z0-9][A-Za-z0-9|_-]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex Colour = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // MISP's text as it is, not as \u escapes

        private static readonly Regex Last = new("^[0-9]{1,6}[smhdw]$", RegexOptions.CultureInvariant);

        public string ConnectorType => "misp";

        public string DisplayName => "MISP";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Authentication key (My Profile > Auth keys)" };

        public string EndpointExample => "https://misp.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://www.misp-project.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.misp-project.org/openapi/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static ConnectorConfigField Max => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField EventField = Field("event", "Event", "Its id or UUID - 42.", required: true);

        private static readonly ConnectorConfigField AttributeField = Field("attribute", "Attribute", "Its id or UUID.", required: true);

        private static readonly ConnectorConfigField TagField = Field("tag", "Tag", "Its id - 75.", required: true);

        private static readonly ConnectorConfigField DistributionField = Field("distribution", "Distribution", "organisation (0), community (1), connected (2), all (3), sharing_group (4) - or inherit (5) for an attribute.");

        private static readonly ConnectorConfigField SharingGroupField = Field("sharing_group", "Sharing group", "Its id, for a distribution to a sharing group.");

        private static ConnectorConfigField[] EventFields(bool create) => new[]
        {
            Field("info", "Info", "What the event is - Phishing wave against the till.", required: create),
            DistributionField,
            Field("threat_level", "Threat level", "high (1), medium (2), low (3) or undefined (4)."),
            Field("analysis", "Analysis", "initial (0), ongoing (1) or complete (2)."),
            SharingGroupField,
            Field("date", "Date", "The event's day - 2026-10-18. Empty: today."),
        };

        private static ConnectorConfigField[] SearchFields(bool events) => new[]
        {
            Field("value", "Value", "An attribute's value - 203.0.113.7, or %.kafe.example with % as a wildcard."),
            Field("searchall", "Anywhere", "Text in an event's info, its attributes' values and comments, and its tags' names - anywhere in them unless it carries MISP's % itself.", required: false),
            Field("tags", "Tags", "By commas - tlp:green,kafe:phishing."),
            Field("type", "Type", "An attribute type - ip-dst."),
            Field("category", "Category", "Network activity."),
            Field("published", "Published", events ? "true or false." : "true or false - its event's."),
            Field("to_ids", "For IDS", events ? "true or false - its attributes'." : "true or false."),
            Field("deleted", "Deleted", "true: only what was deleted."),
            Field("last", "Published in the last", "1d, 12h, 30m - a number and s, m, h, d or w."),
            Field("timestamp", "Changed in the last", "7d - a number and s, m, h, d or w."),
            Max,
        }.Where(field => events || field.Name != "searchall").ToArray();

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("events", "List events", "MISP's events, newest first - n8n's Get Many.") { Parameters = new(new[] { Max }) },
            new ConnectorOperation("event", "Get an event", "One event, without its attributes - n8n's Get.") { Parameters = new(new[] { EventField }) },
            new ConnectorOperation("event_create", "Create an event", "n8n's Create.") { Parameters = new(EventFields(create: true).Append(Field("organisation", "Organisation", "Its id or UUID. Empty: the key's own.")).ToArray()) },
            new ConnectorOperation("event_update", "Update an event", "Only what is given - n8n's Update.") { Parameters = new(new[] { EventField }.Concat(EventFields(create: false)).ToArray()) },
            new ConnectorOperation("event_delete", "Delete an event", "n8n's Delete.") { Parameters = new(new[] { EventField }) },
            new ConnectorOperation("event_publish", "Publish an event", "n8n's Publish.") { Parameters = new(new[] { EventField }) },
            new ConnectorOperation("event_unpublish", "Unpublish an event", "n8n's Unpublish.") { Parameters = new(new[] { EventField }) },
            new ConnectorOperation("event_search", "Search events", "MISP's restSearch, without the attributes - n8n's Search. MISP's own JSON in the Query box instead.") { Parameters = new(SearchFields(events: true)) },
            new ConnectorOperation("event_tag_add", "Tag an event", "n8n's Event Tag Add.") { Parameters = new(new[] { EventField, TagField }) },
            new ConnectorOperation("event_tag_remove", "Untag an event", "n8n's Event Tag Remove.") { Parameters = new(new[] { EventField, TagField }) },
            new ConnectorOperation("attributes", "List attributes", "MISP's attributes - n8n's Get Many.") { Parameters = new(new[] { Max }) },
            new ConnectorOperation("attribute", "Get an attribute", "n8n's Get.") { Parameters = new(new[] { AttributeField }) },
            new ConnectorOperation("attribute_create", "Create an attribute", "On an event - n8n's Create.")
            {
                Parameters = new(new[]
                {
                    EventField,
                    Field("type", "Type", "Any MISP type - ip-dst, domain, url, sha256, text, comment.", required: true),
                    Field("value", "Value", "203.0.113.7.", required: true),
                    Field("category", "Category", "Network activity. Empty: the type's own default."),
                    Field("to_ids", "For IDS", "true or false. Empty: the type's own default."),
                    Field("comment", "Comment", "Seen in the till's logs."),
                    DistributionField, SharingGroupField,
                }),
            },
            new ConnectorOperation("attribute_update", "Update an attribute", "Only what is given - n8n's Update.")
            {
                Parameters = new(new[]
                {
                    AttributeField, Field("value", "Value", "203.0.113.8."), Field("type", "Type", "ip-src."), Field("category", "Category", "Network activity."),
                    Field("to_ids", "For IDS", "true or false."), Field("comment", "Comment", "Seen twice."), DistributionField, SharingGroupField,
                }),
            },
            new ConnectorOperation("attribute_delete", "Delete an attribute", "n8n's Delete.") { Parameters = new(new[] { AttributeField }) },
            new ConnectorOperation("attribute_search", "Search attributes", "MISP's restSearch - n8n's Search. MISP's own JSON in the Query box instead.") { Parameters = new(SearchFields(events: false)) },
            new ConnectorOperation("tags", "List tags", "MISP's tags - n8n's Get Many.") { Parameters = new(new[] { Max }) },
            new ConnectorOperation("tag_create", "Create a tag", "n8n's Create.")
            {
                Parameters = new(new[] { Field("name", "Name", "kafe:phishing.", required: true), Field("colour", "Colour", "#ff0000."), Field("exportable", "Exportable", "true (MISP's default) or false.") }),
            },
            new ConnectorOperation("tag_update", "Update a tag", "n8n's Update.") { Parameters = new(new[] { TagField, Field("name", "Name", "kafe:phishing."), Field("colour", "Colour", "#00ff00.") }) },
            new ConnectorOperation("tag_delete", "Delete a tag", "n8n's Delete.") { Parameters = new(new[] { TagField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var version = await session.SendAsync(HttpMethod.Get, "/servers/getVersion", null, cancellationToken) as JsonObject;
                return Text(version?["version"]) is { Length: > 0 } number
                    ? ConnectionTestResult.Ok($"MISP {number} answers.")
                    : ConnectionTestResult.Fail("MISP answered without its version - check the address: a sign-in page or a proxy answers so.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "events").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"MISP has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to MISP's shape before anything is sent.
            HttpMethod method = HttpMethod.Get;
            string route;
            JsonObject? body = null;
            var max = DefaultMax;
            switch (operation)
            {
                case "events" or "attributes" or "tags":
                    max = Most(p);
                    route = operation switch { "events" => "/events/index", "attributes" => "/attributes/restSearch", _ => "/tags" };
                    break;
                case "event" or "event_delete" or "event_publish" or "event_unpublish":
                    {
                        var id = Id(p, "event", "an event");
                        (method, route) = operation switch
                        {
                            "event" => (HttpMethod.Get, $"/events/view/{id}"),
                            "event_delete" => (HttpMethod.Delete, $"/events/delete/{id}"),
                            "event_publish" => (HttpMethod.Post, $"/events/publish/{id}"),
                            _ => (HttpMethod.Post, $"/events/unpublish/{id}"),
                        };
                        break;
                    }

                case "event_create" or "event_update":
                    {
                        var create = operation == "event_create";
                        body = new JsonObject();
                        if (Values.Get(p, "info") is { } info)
                        {
                            body["info"] = info.Length <= MostInfo ? info : throw new InvalidOperationException($"An event's info is at most {MostInfo} characters - this is {info.Length} (info).");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give what the event is (info).");
                        }

                        Choose(p, body, "distribution", Distributions, "organisation (0), community (1), connected (2), all (3) or sharing_group (4)", most: 4);
                        Choose(p, body, "threat_level", ThreatLevels, "high (1), medium (2), low (3) or undefined (4)", wire: "threat_level_id");
                        Choose(p, body, "analysis", Analyses, "initial (0), ongoing (1) or complete (2)");
                        SharingGroup(p, body);
                        if (Values.Get(p, "date") is { } date)
                        {
                            body["date"] = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{date}' is not a day - 2026-10-18 (date).");
                        }

                        if (create && Values.Get(p, "organisation") is not null)
                        {
                            body["orgc_id"] = Id(p, "organisation", "an organisation");
                        }

                        if (!create && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - info, distribution, threat level, analysis, sharing group or date.");
                        }

                        (method, route) = create ? (HttpMethod.Post, "/events/add") : (HttpMethod.Put, $"/events/edit/{Id(p, "event", "an event")}");
                        break;
                    }

                case "event_tag_add" or "event_tag_remove":
                    {
                        var eventId = Id(p, "event", "an event");
                        var tag = Values.Get(p, "tag") ?? throw new InvalidOperationException("Give the tag - its id, 75 (tag).");
                        tag = Number.IsMatch(tag) ? tag : throw new InvalidOperationException($"'{tag}' is not a tag's id - 75 (tag).");
                        (method, route, body) = operation == "event_tag_add"
                            ? (HttpMethod.Post, "/events/addTag", new JsonObject { ["event"] = eventId, ["tag"] = tag })
                            : (HttpMethod.Post, $"/events/removeTag/{eventId}/{tag}", (JsonObject?)null);
                        break;
                    }

                case "attribute" or "attribute_delete":
                    {
                        var id = Id(p, "attribute", "an attribute");
                        (method, route) = operation == "attribute" ? (HttpMethod.Get, $"/attributes/view/{id}") : (HttpMethod.Delete, $"/attributes/delete/{id}");
                        break;
                    }

                case "attribute_create" or "attribute_update":
                    {
                        var create = operation == "attribute_create";
                        body = new JsonObject();
                        if (Values.Get(p, "type") is { } type)
                        {
                            body["type"] = Type.IsMatch(type) ? type : throw new InvalidOperationException($"'{type}' is not a MISP type - ip-dst, domain, url, sha256 (type).");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the attribute's type - ip-dst, domain, url, sha256 (type).");
                        }

                        if (Values.Get(p, "value") is { } value)
                        {
                            body["value"] = value.Length <= MostInfo ? value : throw new InvalidOperationException($"A value is at most {MostInfo} characters - this is {value.Length} (value).");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the attribute's value - 203.0.113.7 (value).");
                        }

                        if (Values.Get(p, "category") is { } category)
                        {
                            body["category"] = category.Length <= 255 ? category : throw new InvalidOperationException("A category is at most 255 characters (category).");
                        }

                        if (Values.Get(p, "to_ids") is { } toIds)
                        {
                            body["to_ids"] = bool.TryParse(toIds, out var ids) ? ids : throw new InvalidOperationException($"'{toIds}' is not true or false (to_ids).");
                        }

                        if (Values.Get(p, "comment") is { } comment)
                        {
                            body["comment"] = comment.Length <= MostInfo ? comment : throw new InvalidOperationException($"A comment is at most {MostInfo} characters - this is {comment.Length} (comment).");
                        }

                        Choose(p, body, "distribution", Distributions, "organisation (0), community (1), connected (2), all (3), sharing_group (4) or inherit (5)");
                        SharingGroup(p, body);
                        if (!create && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - value, type, category, IDS flag, comment, distribution or sharing group.");
                        }

                        (method, route) = create ? (HttpMethod.Post, $"/attributes/add/{Id(p, "event", "an event")}") : (HttpMethod.Put, $"/attributes/edit/{Id(p, "attribute", "an attribute")}");
                        break;
                    }

                case "event_search" or "attribute_search":
                    body = Search(p, operation == "event_search");
                    max = Most(p);
                    route = operation == "event_search" ? "/events/restSearch" : "/attributes/restSearch";
                    break;
                case "tag_create" or "tag_update":
                    {
                        var create = operation == "tag_create";
                        body = new JsonObject();
                        if (Values.Get(p, "name") is { } name)
                        {
                            body["name"] = name.Length <= 255 && !name.Any(char.IsControl) ? name : throw new InvalidOperationException("A tag's name is at most 255 characters, on one line (name).");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the tag's name - kafe:phishing (name).");
                        }

                        if (Values.Get(p, "colour") is { } colour)
                        {
                            body["colour"] = Colour.IsMatch(colour) ? "#" + colour.TrimStart('#').ToLowerInvariant() : throw new InvalidOperationException($"'{colour}' is not a colour - #ff0000 (colour).");
                        }

                        if (create && Values.Get(p, "exportable") is { } exportable)
                        {
                            body["exportable"] = bool.TryParse(exportable, out var flag) ? flag : throw new InvalidOperationException($"'{exportable}' is not true or false (exportable).");
                        }

                        if (!create && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - name or colour.");
                        }

                        (method, route) = create ? (HttpMethod.Post, "/tags/add") : (HttpMethod.Post, $"/tags/edit/{TagId(p)}");
                        break;
                    }

                default:
                    (method, route) = (HttpMethod.Post, $"/tags/delete/{TagId(p)}");
                    break;
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "events" or "attributes" or "event_search" or "attribute_search":
                    {
                        // Page by page, each the same size - MISP counts a page by its size - until a page comes back short or the most is read.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var query = body?.DeepClone().AsObject() ?? new JsonObject();
                            if (operation != "events")
                            {
                                query["returnFormat"] = "json";
                            }

                            if (operation == "event_search")
                            {
                                query["metadata"] = true; // an event without its attributes
                            }

                            query["limit"] = size;
                            query["page"] = page;
                            var answer = await session.SendAsync(HttpMethod.Post, route, query, cancellationToken);
                            var items = operation switch
                            {
                                "events" => answer as JsonArray,
                                "event_search" => ((answer as JsonObject)?["response"] as JsonArray) is { } found ? new JsonArray(found.Select(item => (item as JsonObject)?["Event"]?.DeepClone()).ToArray()) : null,
                                _ => ((answer as JsonObject)?["response"] as JsonObject)?["Attribute"] as JsonArray ?? ((answer as JsonObject)?["response"] is JsonArray { Count: 0 } ? new JsonArray() : null),
                            } ?? throw new InvalidOperationException("MISP answered without its list - check the address: a sign-in page or a proxy answers so.");
                            foreach (var item in items.OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count < size)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "tags":
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, route, null, cancellationToken);
                        var tags = (answer as JsonObject)?["Tag"] as JsonArray ?? throw new InvalidOperationException("MISP answered without its tags - check the address: a sign-in page or a proxy answers so.");
                        foreach (var tag in tags.OfType<JsonObject>().Take(max))
                        {
                            yield return Record(tag);
                        }

                        break;
                    }

                default:
                    {
                        var answer = await session.SendAsync(method, route, body, cancellationToken) as JsonObject
                            ?? throw new InvalidOperationException("MISP answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                        var one = answer["Event"] as JsonObject ?? answer["Attribute"] as JsonObject ?? answer["Tag"] as JsonObject ?? answer;
                        yield return Record(one);
                        break;
                    }
            }
        }

        private static int Most(IReadOnlyDictionary<string, string> p) =>
            Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;

        // An event's, an attribute's or an organisation's id as MISP takes it: its number, or its UUID.
        private static string Id(IReadOnlyDictionary<string, string> p, string name, string what)
        {
            var given = Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what} - its id or UUID ({name}).");
            return Number.IsMatch(given) ? given : Guid.TryParseExact(given, "D", out var uuid) ? uuid.ToString("D") : throw new InvalidOperationException($"'{given}' is not {what}'s id or UUID ({name}).");
        }

        private static string TagId(IReadOnlyDictionary<string, string> p)
        {
            var tag = Values.Get(p, "tag") ?? throw new InvalidOperationException("Give the tag - its id, 75 (tag).");
            return Number.IsMatch(tag) ? tag : throw new InvalidOperationException($"'{tag}' is not a tag's id - 75 (tag).");
        }

        // A choice by its number or its word, sent as MISP's number; an event's distribution stops at a sharing group.
        private static void Choose(IReadOnlyDictionary<string, string> p, JsonObject body, string name, Dictionary<string, int> choices, string said, string? wire = null, int most = int.MaxValue)
        {
            if (Values.Get(p, name) is { } given)
            {
                body[wire ?? name] = choices.TryGetValue(given, out var value) && value <= most ? value.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not {said} ({name}).");
            }
        }

        // A sharing group only for a distribution to one, and one required for it - as n8n's node holds it.
        private static void SharingGroup(IReadOnlyDictionary<string, string> p, JsonObject body)
        {
            var toGroup = Text(body["distribution"]) == "4";
            if (Values.Get(p, "sharing_group") is { } group)
            {
                body["sharing_group_id"] = !toGroup ? throw new InvalidOperationException("A sharing group is for a distribution to one - give distribution = sharing_group (sharing_group).")
                    : Number.IsMatch(group) ? group : throw new InvalidOperationException($"'{group}' is not a sharing group's id (sharing_group).");
            }
            else if (toGroup)
            {
                throw new InvalidOperationException("Give the sharing group - its id (sharing_group).");
            }
        }

        // A search's body: MISP's own JSON from the Query box as n8n's node took it, or the fields given - tags split on commas.
        private static JsonObject Search(IReadOnlyDictionary<string, string> p, bool events)
        {
            if (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box))
            {
                JsonNode? given;
                try
                {
                    given = JsonNode.Parse(box);
                }
                catch (JsonException)
                {
                    given = null;
                }

                var json = given as JsonObject ?? throw new InvalidOperationException("The search is MISP's own JSON, an object - {\"value\": \"203.0.113.7\"} (query).");
                foreach (var reserved in new[] { "limit", "page", "returnFormat" })
                {
                    json.Remove(reserved); // the connector's own paging and format
                }

                return json;
            }

            var body = new JsonObject();
            foreach (var name in new[] { "value", "type", "category" }.Concat(events ? new[] { "searchall" } : Array.Empty<string>()))
            {
                if (Values.Get(p, name) is { } text)
                {
                    body[name] = name == "type" && !Type.IsMatch(text) ? throw new InvalidOperationException($"'{text}' is not a MISP type - ip-dst, domain, url, sha256 (type).")
                        : name == "searchall" && !text.Contains('%') ? "%" + text + "%" : text; // MISP finds text anywhere only between its own %s (measured)
                }
            }

            if (!events && Values.Get(p, "searchall") is not null)
            {
                throw new InvalidOperationException("Anywhere is an event search's - an attribute is searched by its value (searchall).");
            }

            if (Values.Get(p, "tags") is { } tags)
            {
                body["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)tag).ToArray());
            }

            foreach (var name in new[] { "published", "to_ids", "deleted" })
            {
                if (Values.Get(p, name) is { } flag)
                {
                    body[name] = bool.TryParse(flag, out var value) ? value : throw new InvalidOperationException($"'{flag}' is not true or false ({name}).");
                }
            }

            foreach (var name in new[] { "last", "timestamp" })
            {
                if (Values.Get(p, name) is { } within)
                {
                    body[name] = Last.IsMatch(within) ? within : throw new InvalidOperationException($"'{within}' is not a time - 1d, 12h, 30m ({name}).");
                }
            }

            return body;
        }

        // An object as a record: its plain values as they are, a list or an object as JSON - an event without its attributes.
        private static ConnectorRecord Record(JsonObject data) => new(data.Where(pair => pair.Key != "Attribute").ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(),
        }, StringComparer.Ordinal));

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
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give MISP's address - https://misp.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not MISP's address - https://misp.kafe.example, with no query.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The MISP connection has no authentication key.");
                return new Session(egress.CreateHttpClient(timeout), root, key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the key in its Authorization header; MISP's JSON, or its status, message and field errors.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.TryAddWithoutValidation("Authorization", key);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

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
                    throw new InvalidOperationException("MISP answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("MISP answered with nothing - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var said = Text(json?["message"]) ?? Text(json?["name"]);
                var errors = json?["errors"] is JsonObject fields
                    ? string.Join("; ", fields.Select(pair => $"{pair.Key} - {string.Join(", ", (pair.Value as JsonArray)?.Select(Text).Where(text => text is not null) ?? new[] { Text(pair.Value) })}"))
                    : null;
                var words = string.Join(": ", new[] { said, errors }.Where(part => !string.IsNullOrEmpty(part)));
                words = words.Length > 300 ? words[..300] : words;
                throw new InvalidOperationException($"MISP answered HTTP {status}{(words.Length == 0 ? "." : ": " + words + (words.EndsWith('.') ? string.Empty : "."))}"
                    + (said?.StartsWith("Authentication failed", StringComparison.Ordinal) == true ? " - check the authentication key, and that its user may use the API." : string.Empty));
            }
        }
    }
}
