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

namespace Stomme.Modules.Connectors.Webflow
{
    /// <summary>
    /// Webflow (BL-229, n8n's Webflow node: a collection's items created, got, read, updated and deleted): a workspace's or a
    /// site's CMS over Webflow's Data API v2 (<c>https://api.webflow.com/v2</c>), its API token - a site token, a workspace token
    /// or an app's - sent as a bearer. A test names the sites the token reaches and its scopes.
    /// </summary>
    /// <remarks>
    /// A site, a collection, an item and a custom domain are Webflow's ids - 24 hexadecimal digits - each held to its shape
    /// before anything is sent. <c>sites</c>; <c>collections</c> (a site's); <c>fields</c> (a collection's - each its slug, name,
    /// type, whether it is required and editable); <c>items</c> - a collection's staged items, or its live ones, by a name or a
    /// slug, in an order, in a locale, page by page up to <c>max</c> (1,000); <c>get_item</c>, <c>create_item</c> and
    /// <c>update_item</c> - its fields a JSON object by their slugs, staged or live, a draft or archived - <c>delete_item</c>,
    /// <c>unpublish_item</c> (taken off the live site, kept staged), <c>publish_items</c> and <c>publish_site</c> (to its
    /// custom domains, its Webflow subdomain or both). Each item a record of its id, times, draft and archived flags and locale,
    /// then its fields by their slugs - a value that is not one (an image, a reference) as its JSON. A throttled call waits as
    /// Webflow says; a failure is thrown with its status, Webflow's code, its message and what it said of each parameter.
    /// </remarks>
    public sealed class WebflowConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.webflow.com/v2";

        public const string SitesOperation = "sites";

        public const string CollectionsOperation = "collections";

        public const string FieldsOperation = "fields";

        public const string ItemsOperation = "items";

        public const string GetItemOperation = "get_item";

        public const string CreateItemOperation = "create_item";

        public const string UpdateItemOperation = "update_item";

        public const string DeleteItemOperation = "delete_item";

        public const string UnpublishItemOperation = "unpublish_item";

        public const string PublishItemsOperation = "publish_items";

        public const string PublishSiteOperation = "publish_site";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const int MostIds = 100;

        private static readonly Regex Id = new("^[0-9a-f]{24}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldSlug = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly string[] SortFields = { "createdOn", "lastPublished", "lastUpdated", "name", "slug" };

        // JSON as written: names, rich text and their accents, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SitesOperation, CollectionsOperation, FieldsOperation, ItemsOperation, GetItemOperation, CreateItemOperation, UpdateItemOperation, DeleteItemOperation, UnpublishItemOperation, PublishItemsOperation, PublishSiteOperation };

        // An item's own values; a field whose slug is one of these is kept as field.{slug}.
        private static readonly string[] ItemValues = { "id", "created_on", "last_updated", "last_published", "is_draft", "is_archived", "locale" };

        public string ConnectorType => "webflow";

        public string DisplayName => "Webflow";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://webflow.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.webflow.com/data/reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField SiteField = new("site", "Site", ConnectorConfigFieldType.Text, Required: true, Description: "The site's id - 24 hexadecimal digits, from Get sites.");

        private static readonly ConnectorConfigField CollectionField = new("collection", "Collection", ConnectorConfigFieldType.Text, Required: true, Description: "The collection's id - 24 hexadecimal digits, from Get collections.");

        private static readonly ConnectorConfigField ItemField = new("item", "Item", ConnectorConfigFieldType.Text, Required: true, Description: "The item's id - 24 hexadecimal digits.");

        private static readonly ConnectorConfigField LocaleField = new("locale", "Locale", ConnectorConfigFieldType.Text, Required: false, Description: "A CMS locale's id - the primary locale when left out.");

        private static ConnectorConfigField Live(string description) => new("live", "Live", ConnectorConfigFieldType.Text, Required: false, Description: description) { Default = "false" };

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] ItemWrite(bool create) => new[]
        {
            CollectionField,
            new ConnectorConfigField("item", "Item", ConnectorConfigFieldType.Text, Required: !create, Description: "The item's id - 24 hexadecimal digits."),
            new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: create, Description: "Its fields by their slugs, a JSON object - {\"name\": \"Café\", \"slug\": \"cafe\", \"post-body\": \"<p>Open daily.</p>\"}" + (create ? "; a name and a slug are required." : ".")),
            Live(create ? "true to publish it at once." : "true to change the live item too, at once."),
            new ConnectorConfigField("draft", "Draft", ConnectorConfigFieldType.Text, Required: false, Description: "true to keep it a draft, false to make it ready to publish."),
            new ConnectorConfigField("archived", "Archived", ConnectorConfigFieldType.Text, Required: false, Description: "true to archive it."),
            LocaleField,
        }.Where(field => !create || field.Name != "item").ToArray();

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SitesOperation, "Get sites", "The sites the token reaches - each its id, name, preview address and custom domains.") { Parameters = Schema() },
            new ConnectorOperation(CollectionsOperation, "Get collections", "A site's CMS collections.") { Parameters = Schema(SiteField) },
            new ConnectorOperation(FieldsOperation, "Get fields", "A collection's fields - each its slug, name, type and whether it is required.") { Parameters = Schema(CollectionField) },
            new ConnectorOperation(ItemsOperation, "Get items", "A collection's items, staged or live - n8n's item Get Many.")
            {
                Parameters = Schema(
                    CollectionField,
                    Live("true for the items on the live site."),
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Only the item of this name."),
                    new ConnectorConfigField("slug", "Slug", ConnectorConfigFieldType.Text, Required: false, Description: "Only the item of this slug."),
                    new ConnectorConfigField("sort_by", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: "createdOn, lastPublished, lastUpdated, name or slug."),
                    new ConnectorConfigField("sort_order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
                    LocaleField,
                    Max()),
            },
            new ConnectorOperation(GetItemOperation, "Get an item", "One item, staged or live - n8n's item Get.") { Parameters = Schema(CollectionField, ItemField, Live("true for the item on the live site."), LocaleField) },
            new ConnectorOperation(CreateItemOperation, "Create an item", "Adds an item to a collection, staged or live - n8n's item Create.") { Parameters = Schema(ItemWrite(create: true)) },
            new ConnectorOperation(UpdateItemOperation, "Update an item", "Changes an item's fields, staged or live - n8n's item Update.") { Parameters = Schema(ItemWrite(create: false)) },
            new ConnectorOperation(DeleteItemOperation, "Delete an item", "Deletes an item - n8n's item Delete.") { Parameters = Schema(CollectionField, ItemField) },
            new ConnectorOperation(UnpublishItemOperation, "Unpublish an item", "Takes an item off the live site; it stays, staged, as a draft.") { Parameters = Schema(CollectionField, ItemField) },
            new ConnectorOperation(PublishItemsOperation, "Publish items", "Publishes a collection's staged items to the live site.")
            {
                Parameters = Schema(CollectionField, new ConnectorConfigField("items", "Items", ConnectorConfigFieldType.Text, Required: true, Description: "Their ids, comma-separated - up to 100.")),
            },
            new ConnectorOperation(PublishSiteOperation, "Publish a site", "Publishes a site - Webflow takes one publish a minute.")
            {
                Parameters = Schema(
                    SiteField,
                    new ConnectorConfigField("domains", "Custom domains", ConnectorConfigFieldType.Text, Required: false, Description: "Their ids, comma-separated - from Get sites."),
                    new ConnectorConfigField("subdomain", "Webflow subdomain", ConnectorConfigFieldType.Text, Required: false, Description: "true to publish to the site's webflow.io address - so when no custom domain is given.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var found = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/token/introspect", null, cancellationToken) as JsonObject ?? new JsonObject();
                var authorized = found["authorization"] as JsonObject ?? new JsonObject();
                var sites = (authorized["authorizedTo"]?["siteIds"] as JsonArray ?? new JsonArray()).Count;
                var scope = Text(authorized["scope"]);
                var app = Text(found["application"]?["displayName"]);
                return ConnectionTestResult.Ok($"{(app is null ? "The token" : $"{app}'s token")} reaches {(sites == 1 ? "one site" : $"{sites} sites")}{(string.IsNullOrWhiteSpace(scope) ? "" : $" ({scope.Replace(",", ", ", StringComparison.Ordinal)})")}.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ItemsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Webflow has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var site = operation is CollectionsOperation or PublishSiteOperation ? IdOf(Values.Get(p, "site"), "site") : null;
            var collection = operation is not (SitesOperation or CollectionsOperation or PublishSiteOperation) ? IdOf(Values.Get(p, "collection"), "collection") : null;
            var item = operation is GetItemOperation or UpdateItemOperation or DeleteItemOperation or UnpublishItemOperation ? IdOf(Values.Get(p, "item"), "item") : null;
            var live = operation is ItemsOperation or GetItemOperation or CreateItemOperation or UpdateItemOperation && Values.Get(p, "live") is { } shown && Flag(shown, "live");
            var locale = operation is ItemsOperation or GetItemOperation or CreateItemOperation or UpdateItemOperation && Values.Get(p, "locale") is { } given ? IdOf(given, "locale") : null;
            var query = new List<string>();
            JsonObject? body = null;
            switch (operation)
            {
                case ItemsOperation:
                    foreach (var name in new[] { "name", "slug" })
                    {
                        if (Values.Get(p, name) is { } exact)
                        {
                            query.Add(name + "=" + Uri.EscapeDataString(OneLine(exact, name)));
                        }
                    }

                    if (Values.Get(p, "sort_by") is { } sortBy)
                    {
                        query.Add("sortBy=" + Choice(sortBy, "a field to sort by", SortFields));
                    }

                    if (Values.Get(p, "sort_order") is { } order)
                    {
                        query.Add("sortOrder=" + Choice(order, "an order", "asc", "desc"));
                    }

                    if (locale is not null)
                    {
                        query.Add("cmsLocaleId=" + locale);
                    }

                    break;
                case GetItemOperation:
                    if (locale is not null)
                    {
                        query.Add("cmsLocaleId=" + locale);
                    }

                    break;
                case CreateItemOperation:
                case UpdateItemOperation:
                    {
                        body = new JsonObject();
                        if (Values.Get(p, "fields") is { } fields)
                        {
                            var data = ObjectOf(fields, "fields are", "{\"name\": \"Café\", \"slug\": \"cafe\"}");
                            if (data.Select(field => field.Key).FirstOrDefault(slug => !FieldSlug.IsMatch(slug)) is { } wrong)
                            {
                                throw new InvalidOperationException($"'{wrong}' is not a field's slug - post-body.");
                            }

                            if (operation == CreateItemOperation && (data["name"] is null || data["slug"] is null))
                            {
                                throw new InvalidOperationException("Give the item's name and slug among its fields - {\"name\": \"Café\", \"slug\": \"cafe\"}.");
                            }

                            if (data.Count > 0)
                            {
                                body["fieldData"] = data;
                            }
                        }
                        else if (operation == CreateItemOperation)
                        {
                            throw new InvalidOperationException("Give the item's fields - {\"name\": \"Café\", \"slug\": \"cafe\"}.");
                        }

                        foreach (var (name, key) in new[] { ("draft", "isDraft"), ("archived", "isArchived") })
                        {
                            if (Values.Get(p, name) is { } flag)
                            {
                                body[key] = Flag(flag, name);
                            }
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - its fields, draft or archived.");
                        }

                        if (locale is not null)
                        {
                            body["cmsLocaleId"] = locale;
                        }

                        break;
                    }

                case PublishItemsOperation:
                    {
                        var ids = (Values.Get(p, "items") ?? throw new InvalidOperationException("Give the items - their ids, comma-separated."))
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
                        if (ids.Count == 0 || ids.Count > MostIds)
                        {
                            throw new InvalidOperationException($"Give 1 to {MostIds} items - {ids.Count} given.");
                        }

                        body = new JsonObject { ["itemIds"] = new JsonArray(ids.Select(id => (JsonNode)IdOf(id, "item")).ToArray()) };
                        break;
                    }

                case PublishSiteOperation:
                    {
                        var domains = (Values.Get(p, "domains") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(id => IdOf(id, "custom domain")).Distinct(StringComparer.Ordinal).ToList();
                        var subdomain = Values.Get(p, "subdomain") is { } flag ? Flag(flag, "subdomain") : domains.Count == 0;
                        if (domains.Count == 0 && !subdomain)
                        {
                            throw new InvalidOperationException("Publish somewhere - its custom domains, its Webflow subdomain or both.");
                        }

                        body = new JsonObject { ["publishToWebflowSubdomain"] = subdomain };
                        if (domains.Count > 0)
                        {
                            body["customDomains"] = new JsonArray(domains.Select(id => (JsonNode)id).ToArray());
                        }

                        break;
                    }
            }

            var token = Token(context.Credentials);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            switch (operation)
            {
                case SitesOperation:
                    foreach (var found in (await SendAsync(client, token, HttpMethod.Get, api + "/sites", null, cancellationToken))?["sites"] as JsonArray ?? new JsonArray())
                    {
                        if (found is JsonObject one)
                        {
                            yield return SiteRecord(one);
                        }
                    }

                    yield break;
                case CollectionsOperation:
                    foreach (var found in (await SendAsync(client, token, HttpMethod.Get, $"{api}/sites/{site}/collections", null, cancellationToken))?["collections"] as JsonArray ?? new JsonArray())
                    {
                        if (found is JsonObject one)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["id"] = Text(one["id"]),
                                ["name"] = Text(one["displayName"]),
                                ["singular_name"] = Text(one["singularName"]),
                                ["slug"] = Text(one["slug"]),
                                ["created_on"] = Text(one["createdOn"]),
                                ["last_updated"] = Text(one["lastUpdated"]),
                            });
                        }
                    }

                    yield break;
                case FieldsOperation:
                    foreach (var found in (await SendAsync(client, token, HttpMethod.Get, $"{api}/collections/{collection}", null, cancellationToken))?["fields"] as JsonArray ?? new JsonArray())
                    {
                        if (found is JsonObject one)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["id"] = Text(one["id"]),
                                ["slug"] = Text(one["slug"]),
                                ["name"] = Text(one["displayName"]),
                                ["type"] = Text(one["type"]),
                                ["required"] = Bool(one["isRequired"]),
                                ["editable"] = Bool(one["isEditable"]),
                                ["help_text"] = Text(one["helpText"]),
                            });
                        }
                    }

                    yield break;
                case ItemsOperation:
                    {
                        var read = 0;
                        var offset = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var size = Math.Min(PageSize, max - read);
                            var page = await SendAsync(client, token, HttpMethod.Get, $"{api}/collections/{collection}/items{(live ? "/live" : "")}?{string.Join("&", query.Append("offset=" + offset.ToString(CultureInfo.InvariantCulture)).Append("limit=" + size.ToString(CultureInfo.InvariantCulture)))}", null, cancellationToken);
                            var items = (page?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var one in items)
                            {
                                yield return ItemRecord(one);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            offset += items.Count;
                            var total = Long(page?["pagination"]?["total"]);
                            if (items.Count < size || (total is not null && offset >= total))
                            {
                                yield break;
                            }
                        }
                    }

                case GetItemOperation:
                    yield return ItemRecord(await SendAsync(client, token, HttpMethod.Get, $"{api}/collections/{collection}/items/{item}{(live ? "/live" : "")}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateItemOperation:
                    yield return ItemRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/collections/{collection}/items{(live ? "/live" : "")}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case UpdateItemOperation:
                    yield return ItemRecord(await SendAsync(client, token, HttpMethod.Patch, $"{api}/collections/{collection}/items/{item}{(live ? "/live" : "")}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteItemOperation:
                case UnpublishItemOperation:
                    await SendAsync(client, token, HttpMethod.Delete, $"{api}/collections/{collection}/items/{item}{(operation == UnpublishItemOperation ? "/live" : "")}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["collection"] = collection, ["item"] = item, ["done"] = true });
                    yield break;
                case PublishItemsOperation:
                    {
                        var published = await SendAsync(client, token, HttpMethod.Post, $"{api}/collections/{collection}/items/publish", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["collection"] = collection,
                            ["published"] = string.Join(", ", (published["publishedItemIds"] as JsonArray ?? new JsonArray()).Select(Text).Where(id => id is not null)),
                            ["errors"] = string.Join("; ", (published["errors"] as JsonArray ?? new JsonArray()).Select(error => Text(error) ?? error?.ToJsonString(Wire)).Where(error => error is not null)),
                        });
                        yield break;
                    }

                default:
                    {
                        var published = await SendAsync(client, token, HttpMethod.Post, $"{api}/sites/{site}/publish", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["site"] = site,
                            ["subdomain"] = Bool(published["publishToWebflowSubdomain"]),
                            ["domains"] = string.Join(", ", (published["customDomains"] as JsonArray ?? new JsonArray()).Select(domain => Text(domain?["url"]) ?? Text(domain?["id"])).Where(url => url is not null)),
                            ["scope"] = Text(published["publishScope"]),
                        });
                        yield break;
                    }
            }
        }

        private static ConnectorRecord SiteRecord(JsonObject site) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(site["id"]),
            ["name"] = Text(site["displayName"]),
            ["short_name"] = Text(site["shortName"]),
            ["workspace_id"] = Text(site["workspaceId"]),
            ["preview_url"] = Text(site["previewUrl"]),
            ["time_zone"] = Text(site["timeZone"]),
            ["created_on"] = Text(site["createdOn"]),
            ["last_updated"] = Text(site["lastUpdated"]),
            ["last_published"] = Text(site["lastPublished"]),
            ["domains"] = string.Join(", ", (site["customDomains"] as JsonArray ?? new JsonArray()).Select(domain => Text(domain?["url"])).Where(url => url is not null)),
            ["domain_ids"] = string.Join(", ", (site["customDomains"] as JsonArray ?? new JsonArray()).Select(domain => Text(domain?["id"])).Where(id => id is not null)),
        });

        // An item: its own values, then its fields by their slugs - one that is not a single value (an image, a reference, a list)
        // as its JSON; a field whose slug is one of the item's own values kept as field.{slug}.
        private static ConnectorRecord ItemRecord(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(item["id"]),
                ["created_on"] = Text(item["createdOn"]),
                ["last_updated"] = Text(item["lastUpdated"]),
                ["last_published"] = Text(item["lastPublished"]),
                ["is_draft"] = Bool(item["isDraft"]),
                ["is_archived"] = Bool(item["isArchived"]),
                ["locale"] = Text(item["cmsLocaleId"]),
            };
            foreach (var (slug, value) in item["fieldData"] as JsonObject ?? new JsonObject())
            {
                fields[ItemValues.Contains(slug, StringComparer.Ordinal) ? "field." + slug : slug] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string IdOf(string? given, string what) =>
            given is null ? throw new InvalidOperationException($"Give the {what} - its id.")
                : Id.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not {("aeiou".Contains(what[0], StringComparison.Ordinal) ? "an" : "a")} {what}'s id - 24 hexadecimal digits, 580e63fc8c9a982ac9b8b745.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} not a JSON object - {example}.");
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Webflow connection has no API token.");

        // Webflow's answer as JSON (an empty answer - its 204 - nothing), the token as a bearer, a throttled call waiting as Webflow
        // says; a failure thrown with its status, Webflow's code, its message and what it said of each parameter.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
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
                throw new InvalidOperationException($"Webflow answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["code"]);
                var said = Text(json?["message"]);
                var details = string.Join("; ", (json?["details"] as JsonArray ?? new JsonArray())
                    .Select(detail => detail is JsonObject one
                        ? string.Join(": ", new[] { Text(one["param"]), Text(one["description"]) ?? Text(one["message"]) }.Where(part => part is not null))
                        : Text(detail))
                    .Where(detail => !string.IsNullOrEmpty(detail)));
                var message = said is null ? null : details.Length > 0 ? $"{said} ({details})" : said;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(message is null ? "." : ": " + (message.Length > 300 ? message[..300] : message))}");
            }

            return json;
        }
    }
}
