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

namespace Stomme.Modules.Connectors.GoogleContacts
{
    /// <summary>
    /// Google Contacts (n8n's Google Contacts node: a contact created, got, read, updated and deleted): a Google account's
    /// contacts over the People API (<c>https://people.googleapis.com/v1</c>) and the OAuth2 REFRESH-TOKEN scheme - a Google Cloud
    /// OAuth client's id and secret and a refresh token granted the contacts scope - exchanged at Google's token endpoint (the
    /// <c>token_url</c> setting) through the shared <see cref="OAuth2TokenProvider"/>, as Google Calendar's is. A test names the
    /// account's contact groups.
    /// </summary>
    /// <remarks>
    /// A contact is its id (<c>c123...</c>, or <c>people/c123...</c>), a group its resource name (<c>contactGroups/...</c> or the
    /// bare id), each held to Google's shape before it reaches the path. <c>contacts</c>: the account's contacts page by page by
    /// Google's token (a sort order kept), or those a search finds (Google's search answers at most 30, and is warmed first as
    /// Google asks) - up to <c>max</c> (1,000); <c>get_contact</c>; one record each: its id, names, e-mail addresses and phone
    /// numbers (each <c>type: value</c>), company and job title, notes, birthday, addresses, groups and when it changed - the fields
    /// read are <c>fields</c> (Google's person fields, comma-separated, or <c>*</c>). <c>create_contact</c>: names, e-mail
    /// addresses and phone numbers (<c>work: ada@example.com, home: ...</c>), a company and a job title, notes, a birthday
    /// (<c>1815-12-10</c>, or <c>--12-10</c> with no year), an address, groups, custom fields (a JSON object); <c>update_contact</c>
    /// changes those given - its etag read first, a name part merged into the name it has - with no default (something, or
    /// refused); <c>delete_contact</c>; <c>groups</c>: the contact groups. A throttled call waits as Google says; a failure is
    /// thrown with its status, Google's status word and its message.
    /// </remarks>
    public sealed class GoogleContactsConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://people.googleapis.com/v1";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string CreateContactOperation = "create_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const string GroupsOperation = "groups";

        public const int DefaultMax = 1000;

        public const string DefaultFields = "names,emailAddresses,phoneNumbers,organizations,biographies,birthdays,addresses,memberships,metadata";

        private const int PageSize = 100;

        private const int SearchPageSize = 30;

        private static readonly string[] PersonFields =
        {
            "addresses", "ageRanges", "biographies", "birthdays", "calendarUrls", "clientData", "coverPhotos", "emailAddresses", "events", "externalIds", "genders",
            "imClients", "interests", "locales", "locations", "memberships", "metadata", "miscKeywords", "names", "nicknames", "occupations", "organizations",
            "phoneNumbers", "photos", "relations", "sipAddresses", "skills", "urls", "userDefined",
        };

        private static readonly string[] SortOrders = { "LAST_MODIFIED_ASCENDING", "LAST_MODIFIED_DESCENDING", "FIRST_NAME_ASCENDING", "LAST_NAME_ASCENDING" };

        private static readonly Regex Person = new("^(people/)?(c[0-9]{1,30}|[0-9]{1,30})$", RegexOptions.CultureInvariant);

        private static readonly Regex Group = new("^(contactGroups/)?[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex Birthday = new("^(?:([0-9]{4})-|--)([0-9]{2})-([0-9]{2})$", RegexOptions.CultureInvariant);

        // JSON as written: names, notes and their accents as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { ContactsOperation, GetContactOperation, CreateContactOperation, UpdateContactOperation, DeleteContactOperation, GroupsOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlecontacts";

        public string DisplayName => "Google Contacts";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://contacts.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/people/api/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField ContactField = new("contact", "Contact", ConnectorConfigFieldType.Text, Required: true, Description: "The contact's id - c123... or people/c123....");

        private static readonly ConnectorConfigField FieldsField = new("fields", "Fields", ConnectorConfigFieldType.Text, Required: false,
            Description: "Google's person fields to read, comma-separated (names, emailAddresses...), or * for all.") { Default = DefaultFields };

        private static ConnectorConfigField[] ContactFields() => new[]
        {
            new ConnectorConfigField("given_name", "Given name", ConnectorConfigFieldType.Text, Required: false, Description: "Ada."),
            new ConnectorConfigField("family_name", "Family name", ConnectorConfigFieldType.Text, Required: false, Description: "Lovelace."),
            new ConnectorConfigField("middle_name", "Middle name", ConnectorConfigFieldType.Text, Required: false, Description: "Augusta."),
            new ConnectorConfigField("prefix", "Prefix", ConnectorConfigFieldType.Text, Required: false, Description: "Countess."),
            new ConnectorConfigField("suffix", "Suffix", ConnectorConfigFieldType.Text, Required: false, Description: "FRS."),
            new ConnectorConfigField("emails", "E-mail addresses", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated, each optionally typed - work: ada@example.com, home: ada@example.org."),
            new ConnectorConfigField("phones", "Phone numbers", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated, each optionally typed - mobile: +46 70 174 06 05."),
            new ConnectorConfigField("company", "Company", ConnectorConfigFieldType.Text, Required: false, Description: "Where they work."),
            new ConnectorConfigField("job_title", "Job title", ConnectorConfigFieldType.Text, Required: false, Description: "What they do there."),
            new ConnectorConfigField("notes", "Notes", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text, as written."),
            new ConnectorConfigField("birthday", "Birthday", ConnectorConfigFieldType.Text, Required: false, Description: "1815-12-10, or --12-10 with no year."),
            new ConnectorConfigField("address", "Street address", ConnectorConfigFieldType.Text, Required: false, Description: "12 Byron Street."),
            new ConnectorConfigField("city", "City", ConnectorConfigFieldType.Text, Required: false, Description: "London."),
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: false, Description: "Greater London."),
            new ConnectorConfigField("postal_code", "Postal code", ConnectorConfigFieldType.Text, Required: false, Description: "W1 1AA."),
            new ConnectorConfigField("country_code", "Country code", ConnectorConfigFieldType.Text, Required: false, Description: "Two letters - GB."),
            new ConnectorConfigField("address_type", "Address type", ConnectorConfigFieldType.Text, Required: false, Description: "home, work or other.") { Default = "home" },
            new ConnectorConfigField("groups", "Groups", ConnectorConfigFieldType.Text, Required: false, Description: "Contact groups, comma-separated - contactGroups/... or their ids."),
            new ConnectorConfigField("custom_fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"Customer id\": \"1001\"}."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "The account's contacts, or those a search finds - one record each: their names, e-mail addresses, phone numbers and company - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words a name, an e-mail address, a phone number or a company starts with. Empty: every contact."),
                    new ConnectorConfigField("sort_order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "LAST_MODIFIED_ASCENDING, LAST_MODIFIED_DESCENDING, FIRST_NAME_ASCENDING or LAST_NAME_ASCENDING - without a search."),
                    FieldsField,
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact - n8n's Get.") { Parameters = new ConnectorConfigSchema(new[] { ContactField, FieldsField }) },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "Adds a contact - n8n's Create.") { Parameters = new ConnectorConfigSchema(ContactFields()) },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes a contact's names, e-mail addresses, phone numbers, company, notes, birthday, address, groups or custom fields - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ContactField }.Concat(ContactFields()).ToArray()),
            },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "Deletes a contact - n8n's Delete.") { Parameters = new ConnectorConfigSchema(new[] { ContactField }) },
            new ConnectorOperation(GroupsOperation, "Get contact groups", "The account's contact groups - their resource names, names and how many each holds.") { Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var groups = await SendAsync(client, token, HttpMethod.Get, api + "/contactGroups?pageSize=100", null, cancellationToken);
                var names = (groups["contactGroups"] as JsonArray ?? new JsonArray()).Select(group => Text(group?["formattedName"]) ?? Text(group?["name"])).Where(name => name is not null).ToList();
                return ConnectionTestResult.Ok(names.Count == 0 ? "Signed in - no contact groups." : $"Signed in - {names.Count} contact group{(names.Count == 1 ? "" : "s")}, {names[0]} first.");
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
            var operation = context.OperationName ?? ContactsOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Google Contacts has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before a token is asked for.
            var contact = Is(operation, GetContactOperation) || Is(operation, UpdateContactOperation) || Is(operation, DeleteContactOperation) ? Contact(Values.Get(parameters, "contact")) : null;
            var fields = Fields(Values.Get(parameters, "fields"));
            var query = Values.Get(parameters, "query");
            var sort = Values.Get(parameters, "sort_order") is { } order ? Choice(order, "an order", SortOrders) : null;
            if (sort is not null && query is not null)
            {
                throw new InvalidOperationException("A search has no order - give a search or an order, not both.");
            }

            var max = Values.Get(parameters, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var body = Is(operation, CreateContactOperation) || Is(operation, UpdateContactOperation) ? PersonBody(parameters, Is(operation, CreateContactOperation)) : null;

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, GroupsOperation))
            {
                var groups = await SendAsync(client, token, HttpMethod.Get, api + "/contactGroups?pageSize=1000", null, cancellationToken);
                foreach (var group in (groups["contactGroups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["resource_name"] = Text(group["resourceName"]),
                        ["name"] = Text(group["formattedName"]) ?? Text(group["name"]),
                        ["type"] = Text(group["groupType"]),
                        ["members"] = group["memberCount"] is JsonValue count && count.TryGetValue<long>(out var members) ? members : 0L,
                    });
                }

                yield break;
            }

            if (Is(operation, ContactsOperation))
            {
                var read = 0;
                if (query is not null)
                {
                    // Google asks a search to be warmed first - an empty one - so the next sees every contact.
                    await SendAsync(client, token, HttpMethod.Get, $"{api}/people:searchContacts?query=&readMask=names", null, cancellationToken);
                    var found = await SendAsync(client, token, HttpMethod.Get, $"{api}/people:searchContacts?query={Uri.EscapeDataString(query)}&readMask={Uri.EscapeDataString(fields)}&pageSize={Math.Min(SearchPageSize, max).ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
                    foreach (var result in (found["results"] as JsonArray ?? new JsonArray()).Select(result => result?["person"]).OfType<JsonObject>())
                    {
                        yield return PersonRecord(result);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    yield break;
                }

                string? pageToken = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = $"{api}/people/me/connections?personFields={Uri.EscapeDataString(fields)}&pageSize={Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)}"
                        + (sort is null ? "" : "&sortOrder=" + sort) + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
                    var page = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var person in (page["connections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return PersonRecord(person);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    pageToken = Text(page["nextPageToken"]);
                }
                while (!string.IsNullOrEmpty(pageToken));

                yield break;
            }

            if (Is(operation, GetContactOperation))
            {
                yield return PersonRecord(await SendAsync(client, token, HttpMethod.Get, $"{api}/{contact}?personFields={Uri.EscapeDataString(fields)}", null, cancellationToken));
                yield break;
            }

            if (Is(operation, CreateContactOperation))
            {
                yield return PersonRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/people:createContact?personFields={Uri.EscapeDataString(fields)}", body, cancellationToken));
                yield break;
            }

            if (Is(operation, UpdateContactOperation))
            {
                // Google takes a change with the etag it gave; a name part given is merged into the name the contact has.
                var current = await SendAsync(client, token, HttpMethod.Get, $"{api}/{contact}?personFields=names", null, cancellationToken);
                body!["etag"] = Text(current["etag"]);
                if (body["names"] is JsonArray { Count: 1 } names && names[0] is JsonObject given && (current["names"] as JsonArray)?.FirstOrDefault() is JsonObject had)
                {
                    foreach (var part in new[] { "givenName", "familyName", "middleName", "honorificPrefix", "honorificSuffix" })
                    {
                        if (!given.ContainsKey(part) && Text(had[part]) is { } kept)
                        {
                            given[part] = kept;
                        }
                    }
                }

                var changed = string.Join(",", body.Select(field => field.Key).Where(key => key != "etag"));
                yield return PersonRecord(await SendAsync(client, token, new HttpMethod("PATCH"), $"{api}/{contact}:updateContact?updatePersonFields={changed}&personFields={Uri.EscapeDataString(fields)}", body, cancellationToken));
                yield break;
            }

            await SendAsync(client, token, HttpMethod.Delete, $"{api}/{contact}:deleteContact", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = contact![(contact.IndexOf('/') + 1)..], ["deleted"] = true });
        }

        // A person as Google takes it: names, e-mail addresses and phone numbers (each optionally typed), a company and a job title,
        // notes, a birthday, an address, groups and custom fields - only those given.
        private static JsonObject PersonBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            var name = new JsonObject();
            foreach (var (parameter, field) in new[] { ("given_name", "givenName"), ("family_name", "familyName"), ("middle_name", "middleName"), ("prefix", "honorificPrefix"), ("suffix", "honorificSuffix") })
            {
                if (Values.Get(parameters, parameter) is { } part)
                {
                    name[field] = OneLine(part, parameter.Replace('_', ' '));
                }
            }

            if (name.Count > 0)
            {
                body["names"] = new JsonArray(name);
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the contact a name - a given name or a family name.");
            }

            if (Values.Get(parameters, "emails") is { } emails)
            {
                body["emailAddresses"] = Typed(emails, "an e-mail address", value => value.Contains('@', StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace) ? value : null);
            }

            if (Values.Get(parameters, "phones") is { } phones)
            {
                body["phoneNumbers"] = Typed(phones, "a phone number", value => value.Any(char.IsDigit) ? value : null);
            }

            if (Values.Get(parameters, "company") is not null || Values.Get(parameters, "job_title") is not null)
            {
                var organization = new JsonObject();
                if (Values.Get(parameters, "company") is { } named)
                {
                    organization["name"] = OneLine(named, "company");
                }

                if (Values.Get(parameters, "job_title") is { } job)
                {
                    organization["title"] = OneLine(job, "job title");
                }

                body["organizations"] = new JsonArray(organization);
            }

            if (parameters.TryGetValue("notes", out var notes) && !string.IsNullOrWhiteSpace(notes))
            {
                body["biographies"] = new JsonArray(new JsonObject { ["value"] = notes, ["contentType"] = "TEXT_PLAIN" });
            }

            if (Values.Get(parameters, "birthday") is { } birthday)
            {
                var match = Birthday.Match(birthday);
                var date = new JsonObject();
                if (match.Success && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is >= 1 and <= 12 && int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) is >= 1 and <= 31)
                {
                    if (match.Groups[1].Success)
                    {
                        date["year"] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    }

                    date["month"] = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    date["day"] = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    body["birthdays"] = new JsonArray(new JsonObject { ["date"] = date });
                }
                else
                {
                    throw new InvalidOperationException($"'{birthday}' is not a birthday - 1815-12-10, or --12-10 with no year.");
                }
            }

            var address = new JsonObject();
            foreach (var (parameter, field) in new[] { ("address", "streetAddress"), ("city", "city"), ("region", "region"), ("postal_code", "postalCode"), ("country_code", "countryCode") })
            {
                if (Values.Get(parameters, parameter) is { } part)
                {
                    address[field] = OneLine(part, parameter.Replace('_', ' '));
                }
            }

            if (address.Count > 0)
            {
                address["type"] = Choice(Values.Get(parameters, "address_type") ?? "home", "an address type", "home", "work", "other");
                body["addresses"] = new JsonArray(address);
            }

            if (Values.Get(parameters, "groups") is { } groups)
            {
                body["memberships"] = new JsonArray(groups.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(group => (JsonNode)new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = GroupName(group) } }).ToArray());
            }

            if (Values.Get(parameters, "custom_fields") is { } custom)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(custom);
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                body["userDefined"] = parsed is JsonObject pairs
                    ? new JsonArray(pairs.Select(pair => (JsonNode)new JsonObject { ["key"] = pair.Key, ["value"] = pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString(Wire) ?? "" }).ToArray())
                    : throw new InvalidOperationException("The custom fields are not a JSON object - {\"Customer id\": \"1001\"}.");
            }

            if (!create && body.Count == 0)
            {
                throw new InvalidOperationException("Give something to change - a name, e-mail addresses, phone numbers, a company, notes, a birthday, an address, groups or custom fields.");
            }

            return body;
        }

        // A comma-separated list of values, each optionally "type: value", as Google's typed list; one that is not a value refused.
        private static JsonArray Typed(string given, string what, Func<string, string?> check)
        {
            var list = new JsonArray();
            foreach (var entry in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = entry.IndexOf(':', StringComparison.Ordinal);
                var typed = colon > 0 && entry[..colon].All(char.IsLetter);
                var value = (typed ? entry[(colon + 1)..] : entry).Trim();
                var item = new JsonObject { ["value"] = check(value) ?? throw new InvalidOperationException($"'{value}' is not {what}.") };
                if (typed)
                {
                    item["type"] = entry[..colon].Trim();
                }

                list.Add(item);
            }

            return list;
        }

        private static ConnectorRecord PersonRecord(JsonObject person)
        {
            var name = (person["names"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var organization = (person["organizations"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var birthday = (person["birthdays"] as JsonArray)?.Select(day => day?["date"]).FirstOrDefault(date => date is JsonObject) as JsonObject;
            string Listed(string list) => string.Join(", ", (person[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(item => Text(item["value"]) is { } value ? (Text(item["type"]) is { Length: > 0 } type ? type + ": " + value : value) : null).Where(value => value is not null));
            var resource = Text(person["resourceName"]);
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = resource is null ? null : resource[(resource.IndexOf('/') + 1)..],
                ["resource_name"] = resource,
                ["name"] = Text(name?["displayName"]),
                ["given_name"] = Text(name?["givenName"]),
                ["family_name"] = Text(name?["familyName"]),
                ["emails"] = Listed("emailAddresses"),
                ["phones"] = Listed("phoneNumbers"),
                ["company"] = Text(organization?["name"]),
                ["job_title"] = Text(organization?["title"]),
                ["notes"] = Text((person["biographies"] as JsonArray)?.FirstOrDefault()?["value"]) ?? "",
                ["birthday"] = birthday is null ? null
                    : (birthday["year"] is JsonValue year && year.TryGetValue<int>(out var y) ? y.ToString("0000", CultureInfo.InvariantCulture) : "-")
                        + "-" + Number(birthday["month"]).ToString("00", CultureInfo.InvariantCulture) + "-" + Number(birthday["day"]).ToString("00", CultureInfo.InvariantCulture),
                ["addresses"] = string.Join("; ", (person["addresses"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(address => Text(address["formattedValue"])
                    ?? string.Join(", ", new[] { "streetAddress", "city", "region", "postalCode", "countryCode" }.Select(part => Text(address[part])).Where(part => !string.IsNullOrEmpty(part))))),
                ["groups"] = string.Join(", ", (person["memberships"] as JsonArray ?? new JsonArray()).Select(membership => Text(membership?["contactGroupMembership"]?["contactGroupResourceName"])).Where(group => group is not null)),
                ["updated"] = Text((person["metadata"]?["sources"] as JsonArray)?.FirstOrDefault()?["updateTime"]),
            });
        }

        private static int Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

        private static string Contact(string? given) =>
            given is not null && Person.Match(given.Trim()) is { Success: true } match ? "people/" + match.Groups[2].Value : throw new InvalidOperationException($"'{given}' is not a contact's id - c123... or people/c123....");

        private static string GroupName(string given) =>
            Group.IsMatch(given) ? (given.StartsWith("contactGroups/", StringComparison.Ordinal) ? given : "contactGroups/" + given) : throw new InvalidOperationException($"'{given}' is not a contact group - contactGroups/... or its id.");

        // Google's person fields, held to the ones it has: comma-separated, or * for every one.
        private static string Fields(string? given)
        {
            if (given is null)
            {
                return DefaultFields;
            }

            if (given == "*")
            {
                return string.Join(",", PersonFields);
            }

            var fields = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var unknown = fields.FirstOrDefault(field => !PersonFields.Contains(field, StringComparer.Ordinal));
            return unknown is null && fields.Length > 0 ? string.Join(",", fields) : throw new InvalidOperationException($"'{unknown ?? given}' is not one of Google's person fields - names, emailAddresses, phoneNumbers... or *.");
        }

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer an empty object), a throttled call waiting as Google says; a failure thrown with
        // its status, Google's status word and its message.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                throw new InvalidOperationException($"Google Contacts answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
