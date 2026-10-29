#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Mailchimp
{
    /// <summary>
    /// Mailchimp (n8n's Mailchimp node: an audience's members found, got, added, updated, deleted and tagged; campaigns
    /// found, got, sent, replicated, resent and deleted): the Marketing API of the key's data center
    /// (<c>https://us21.api.mailchimp.com/3.0</c>), the API key by Mailchimp's Basic pair (any name and the key). A test names the
    /// account.
    /// </summary>
    /// <remarks>
    /// An audience and a campaign are named by their id (letters and digits), a member by its address or Mailchimp's subscriber
    /// hash (the address's MD5), each held to that shape before it reaches the path; the address of an admin page is taken to its
    /// data center's API, and a key from another data center than the address's is refused. <c>members</c>: an audience's
    /// members - a status, changed since a time - page by page by offset up to <c>max</c>; one record each: the hash, the address,
    /// status, full name, merge fields (JSON), tags (comma-separated), VIP, language, email type, rating and its times.
    /// <c>get_member</c> one. <c>add_member</c>: an address and a status, merge fields (a JSON object), tags, a language, VIP and an
    /// email type; <c>update_member</c> changes those given (something, or refused); <c>upsert_member</c> adds or updates by the
    /// address, a status for a new one required; <c>delete_member</c> archives one - or deletes it for good when asked;
    /// <c>tag_member</c> adds and removes tags. <c>lists</c>: the account's audiences. <c>campaigns</c>: its campaigns - a status, a
    /// type, an audience, sent or made since a time, sorted - each its id, title, subject, audience, status, emails sent and times;
    /// <c>get_campaign</c>, <c>send_campaign</c>, <c>replicate_campaign</c>, <c>resend_campaign</c> (to the non-openers, by default,
    /// or another shortcut) and <c>delete_campaign</c>. A throttled call waits as Mailchimp says; a failure is thrown with its
    /// status and Mailchimp's title, detail and first field error.
    /// </remarks>
    public sealed class MailchimpConnector : IConnector, IConnectorExtractor
    {
        public const string MembersOperation = "members";

        public const string GetMemberOperation = "get_member";

        public const string AddMemberOperation = "add_member";

        public const string UpdateMemberOperation = "update_member";

        public const string UpsertMemberOperation = "upsert_member";

        public const string DeleteMemberOperation = "delete_member";

        public const string TagMemberOperation = "tag_member";

        public const string ListsOperation = "lists";

        public const string CampaignsOperation = "campaigns";

        public const string GetCampaignOperation = "get_campaign";

        public const string SendCampaignOperation = "send_campaign";

        public const string ReplicateCampaignOperation = "replicate_campaign";

        public const string ResendCampaignOperation = "resend_campaign";

        public const string DeleteCampaignOperation = "delete_campaign";

        public const int DefaultMax = 1000;

        private const int PageSize = 1000;

        private static readonly Regex Ids = new("^[A-Za-z0-9]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly Regex SubscriberHash = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        private static readonly Regex DataCenter = new("^[a-z]{2,4}[0-9]{1,3}$", RegexOptions.CultureInvariant);

        private static readonly Regex MailchimpHost = new(@"^(?<dc>[a-z]{2,4}[0-9]{1,3})\.(api|admin)\.mailchimp\.com$", RegexOptions.CultureInvariant);

        private static readonly Regex MergeTag = new("^[A-Za-z0-9_]{1,50}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> MemberStatuses = new(StringComparer.Ordinal) { "subscribed", "unsubscribed", "cleaned", "pending", "transactional" };

        private static readonly HashSet<string> MemberFilters = new(StringComparer.Ordinal) { "subscribed", "unsubscribed", "cleaned", "pending", "transactional", "archived" };

        private static readonly HashSet<string> EmailTypes = new(StringComparer.Ordinal) { "html", "text" };

        private static readonly HashSet<string> CampaignStatuses = new(StringComparer.Ordinal) { "save", "paused", "schedule", "sending", "sent" };

        private static readonly HashSet<string> CampaignTypes = new(StringComparer.Ordinal) { "regular", "plaintext", "absplit", "rss", "variate" };

        private static readonly HashSet<string> SortFields = new(StringComparer.Ordinal) { "create_time", "send_time" };

        private static readonly HashSet<string> Resends = new(StringComparer.Ordinal) { "non_openers", "new_subscribers", "non_clickers", "non_purchasers" };

        public string ConnectorType => "mailchimp";

        public string DisplayName => "Mailchimp";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        /// <summary>The key as Mailchimp names it, with where its data center is.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (it ends in its data center - -us21)" };

        public string EndpointExample => "https://us21.api.mailchimp.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://mailchimp.com");

        public Uri ApiDocumentationUrl { get; } = new("https://mailchimp.com/developer/marketing/api/");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ListField = new("list", "Audience", ConnectorConfigFieldType.Text, Required: true, Description: "The audience's id - a1b2c3d4e5 (Audience > Settings > Audience name and defaults).");

        private static readonly ConnectorConfigField EmailField = new("email", "Email", ConnectorConfigFieldType.Text, Required: true, Description: "The member's address - or its subscriber hash, a member's id.");

        private static readonly ConnectorConfigField CampaignField = new("campaign", "Campaign", ConnectorConfigFieldType.Text, Required: true, Description: "The campaign's id.");

        private static ConnectorConfigField[] MemberFields(string statusDescription, bool statusRequired) => new[]
        {
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: statusRequired, Description: statusDescription),
            new ConnectorConfigField("merge_fields", "Merge fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"FNAME\": \"Ada\", \"LNAME\": \"Lovelace\"}."),
            new ConnectorConfigField("language", "Language", ConnectorConfigFieldType.Text, Required: false, Description: "Its code - en, sv."),
            new ConnectorConfigField("vip", "VIP", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
            new ConnectorConfigField("email_type", "Email type", ConnectorConfigFieldType.Text, Required: false, Description: "html or text."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(MembersOperation, "Get members", "An audience's members - one record each: the address, status, name, merge fields and tags - n8n's member Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField,
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "subscribed, unsubscribed, cleaned, pending, transactional or archived."),
                    new ConnectorConfigField("since_last_changed", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetMemberOperation, "Get a member", "One member - n8n's member Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, EmailField }),
            },
            new ConnectorOperation(AddMemberOperation, "Add a member", "Adds a member to an audience - n8n's member Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, EmailField }
                    .Concat(MemberFields("subscribed, unsubscribed, cleaned, pending (double opt-in) or transactional.", statusRequired: true))
                    .Concat(new[] { new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated.") }).ToArray()),
            },
            new ConnectorOperation(UpdateMemberOperation, "Update a member", "Changes a member - n8n's member Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, EmailField }
                    .Concat(MemberFields("subscribed, unsubscribed, cleaned, pending or transactional.", statusRequired: false)).ToArray()),
            },
            new ConnectorOperation(UpsertMemberOperation, "Add or update a member", "Adds the address, or updates it when the audience has it.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField, EmailField,
                    new ConnectorConfigField("status_if_new", "Status if new", ConnectorConfigFieldType.Text, Required: true, Description: "A new member's status - subscribed, pending, ..."),
                }.Concat(MemberFields("An existing member's new status.", statusRequired: false)).ToArray()),
            },
            new ConnectorOperation(DeleteMemberOperation, "Delete a member", "Archives a member - or deletes them for good - n8n's member Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField, EmailField,
                    new ConnectorConfigField("permanently", "For good", ConnectorConfigFieldType.Text, Required: false, Description: "true deletes the member and their history; they cannot be added again. Empty: archived."),
                }),
            },
            new ConnectorOperation(TagMemberOperation, "Tag a member", "Adds and removes a member's tags - n8n's member tag Create and Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField, EmailField,
                    new ConnectorConfigField("add", "Add", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                    new ConnectorConfigField("remove", "Remove", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                }),
            },
            new ConnectorOperation(ListsOperation, "Get audiences", "The account's audiences - their ids, names and member counts.")
            {
                Parameters = new ConnectorConfigSchema(new[] { MaxField }),
            },
            new ConnectorOperation(CampaignsOperation, "Get campaigns", "The account's campaigns - one record each: its id, title, subject, audience, status and emails sent - n8n's campaign Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "save, paused, schedule, sending or sent."),
                    new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "regular, plaintext, absplit, rss or variate."),
                    new ConnectorConfigField("list", "Audience", ConnectorConfigFieldType.Text, Required: false, Description: "Only this audience's - its id."),
                    new ConnectorConfigField("since_send_time", "Sent since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    new ConnectorConfigField("since_create_time", "Made since", ConnectorConfigFieldType.Text, Required: false, Description: "A time."),
                    new ConnectorConfigField("sort_field", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: "create_time or send_time."),
                    new ConnectorConfigField("sort_dir", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetCampaignOperation, "Get a campaign", "One campaign - n8n's campaign Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CampaignField }),
            },
            new ConnectorOperation(SendCampaignOperation, "Send a campaign", "Sends a campaign now - n8n's campaign Send.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CampaignField }),
            },
            new ConnectorOperation(ReplicateCampaignOperation, "Replicate a campaign", "A copy of a campaign - the copy - n8n's campaign Replicate.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CampaignField }),
            },
            new ConnectorOperation(ResendCampaignOperation, "Resend a campaign", "A campaign made to resend one - the new campaign - n8n's campaign Resend.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    CampaignField,
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: false, Description: "non_openers, new_subscribers, non_clickers or non_purchasers. Empty: non_openers."),
                }),
            },
            new ConnectorOperation(DeleteCampaignOperation, "Delete a campaign", "Removes a campaign - n8n's campaign Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CampaignField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var key = Key(context.Credentials);
                var api = Api(context.Endpoint, key);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var account = await SendAsync(client, Authorization(key), HttpMethod.Get, api + "/", null, cancellationToken);
                return Text(account?["account_name"]) is { Length: > 0 } name
                    ? ConnectionTestResult.Ok($"Reached the account {name}{(Text(account?["email"]) is { } email ? $" ({email})" : "")}.")
                    : ConnectionTestResult.Fail("Mailchimp answered without an account - check the address and the API key.");
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
            var key = Key(context.Credentials);
            var api = Api(context.Endpoint, key);
            var authorization = Authorization(key);
            var operation = context.OperationName ?? MembersOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;

            // What could be refused is refused before anything is sent.
            string url;
            HttpMethod method = HttpMethod.Get;
            JsonObject? body = null;
            string? listed = null;
            Func<JsonObject, ConnectorRecord> record = Member;
            ConnectorRecord? done = null;
            if (Is(operation, MembersOperation))
            {
                var query = new List<string>();
                if (Values.Get(parameters, "status") is { } status)
                {
                    query.Add("status=" + Word(status, MemberFilters, "member's status"));
                }

                if (Values.Get(parameters, "since_last_changed") is { } since)
                {
                    query.Add("since_last_changed=" + Uri.EscapeDataString(Time(since)));
                }

                url = $"{api}/lists/{Id(Values.Get(parameters, "list"), "audience")}/members" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
                listed = "members";
            }
            else if (Is(operation, GetMemberOperation))
            {
                url = MemberUrl(api, parameters);
            }
            else if (Is(operation, AddMemberOperation))
            {
                var list = Id(Values.Get(parameters, "list"), "audience");
                var member = new JsonObject
                {
                    ["email_address"] = Email(Values.Get(parameters, "email") ?? throw new InvalidOperationException("Give the member's address.")),
                    ["status"] = Word(Values.Get(parameters, "status") ?? throw new InvalidOperationException("Give the member's status - subscribed, or pending for a double opt-in."), MemberStatuses, "member's status"),
                };
                MemberChange(member, parameters);
                if (Values.Get(parameters, "tags") is { } tags)
                {
                    member["tags"] = List(tags, "tag");
                }

                url = $"{api}/lists/{list}/members";
                method = HttpMethod.Post;
                body = member;
            }
            else if (Is(operation, UpdateMemberOperation))
            {
                url = MemberUrl(api, parameters);
                var member = new JsonObject();
                if (Values.Get(parameters, "status") is { } status)
                {
                    member["status"] = Word(status, MemberStatuses, "member's status");
                }

                MemberChange(member, parameters);
                if (member.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - a status, merge fields, a language, VIP or an email type.");
                }

                method = HttpMethod.Patch;
                body = member;
            }
            else if (Is(operation, UpsertMemberOperation))
            {
                url = MemberUrl(api, parameters);
                var email = Values.Get(parameters, "email")!;
                var member = new JsonObject
                {
                    ["email_address"] = SubscriberHash.IsMatch(email) ? throw new InvalidOperationException("Give the member's address - adding one needs it, not its hash.") : Email(email),
                    ["status_if_new"] = Word(Values.Get(parameters, "status_if_new") ?? throw new InvalidOperationException("Give the status a new member gets - subscribed, or pending for a double opt-in."), MemberStatuses, "member's status"),
                };
                if (Values.Get(parameters, "status") is { } status)
                {
                    member["status"] = Word(status, MemberStatuses, "member's status");
                }

                MemberChange(member, parameters);
                method = HttpMethod.Put;
                body = member;
            }
            else if (Is(operation, DeleteMemberOperation))
            {
                var member = MemberUrl(api, parameters);
                var forGood = Flag(Values.Get(parameters, "permanently") ?? "false", "permanently");
                url = forGood ? member + "/actions/delete-permanent" : member;
                method = forGood ? HttpMethod.Post : HttpMethod.Delete;
                done = Done(parameters, forGood ? "deleted" : "archived");
            }
            else if (Is(operation, TagMemberOperation))
            {
                url = MemberUrl(api, parameters) + "/tags";
                var tags = new JsonArray();
                foreach (var (given, state) in new[] { ("add", "active"), ("remove", "inactive") })
                {
                    if (Values.Get(parameters, given) is { } names)
                    {
                        foreach (var name in List(names, "tag").Select(Text))
                        {
                            tags.Add(new JsonObject { ["name"] = name, ["status"] = state });
                        }
                    }
                }

                if (tags.Count == 0)
                {
                    throw new InvalidOperationException("Give a tag to add or to remove.");
                }

                method = HttpMethod.Post;
                body = new JsonObject { ["tags"] = tags };
                done = new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["email"] = Values.Get(parameters, "email"),
                    ["added"] = Values.Get(parameters, "add") is { } added ? string.Join(", ", List(added, "tag").Select(Text)) : null,
                    ["removed"] = Values.Get(parameters, "remove") is { } removed ? string.Join(", ", List(removed, "tag").Select(Text)) : null,
                });
            }
            else if (Is(operation, ListsOperation))
            {
                url = $"{api}/lists";
                listed = "lists";
                record = Audience;
            }
            else if (Is(operation, CampaignsOperation))
            {
                var query = new List<string>();
                foreach (var (given, words, what) in new[] { ("status", CampaignStatuses, "campaign's status"), ("type", CampaignTypes, "campaign type"), ("sort_field", SortFields, "field to sort by") })
                {
                    if (Values.Get(parameters, given) is { } value)
                    {
                        query.Add(given + "=" + Word(value, words, what));
                    }
                }

                if (Values.Get(parameters, "sort_dir") is { } direction)
                {
                    query.Add("sort_dir=" + Word(direction, new HashSet<string>(StringComparer.Ordinal) { "asc", "desc" }, "sort order").ToUpperInvariant());
                }

                if (Values.Get(parameters, "list") is { } list)
                {
                    query.Add("list_id=" + Id(list, "audience"));
                }

                foreach (var given in new[] { "since_send_time", "since_create_time" })
                {
                    if (Values.Get(parameters, given) is { } since)
                    {
                        query.Add(given + "=" + Uri.EscapeDataString(Time(since)));
                    }
                }

                url = $"{api}/campaigns" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
                listed = "campaigns";
                record = Campaign;
            }
            else if (Is(operation, GetCampaignOperation) || Is(operation, DeleteCampaignOperation))
            {
                url = CampaignUrl(api, parameters);
                record = Campaign;
                if (Is(operation, DeleteCampaignOperation))
                {
                    method = HttpMethod.Delete;
                    done = CampaignDone(parameters, "deleted");
                }
            }
            else if (Is(operation, SendCampaignOperation))
            {
                url = CampaignUrl(api, parameters) + "/actions/send";
                method = HttpMethod.Post;
                done = CampaignDone(parameters, "sent");
            }
            else if (Is(operation, ReplicateCampaignOperation))
            {
                url = CampaignUrl(api, parameters) + "/actions/replicate";
                method = HttpMethod.Post;
                record = Campaign;
            }
            else if (Is(operation, ResendCampaignOperation))
            {
                url = CampaignUrl(api, parameters) + "/actions/create-resend";
                method = HttpMethod.Post;
                if (Values.Get(parameters, "to") is { } to)
                {
                    body = new JsonObject { ["shortcut_type"] = "to_" + Word(to, Resends, "group to resend to") };
                }

                record = Campaign;
            }
            else
            {
                throw new InvalidOperationException($"Mailchimp has no operation '{operation}' - members, get_member, add_member, update_member, upsert_member, delete_member, tag_member, lists, campaigns, get_campaign, send_campaign, replicate_campaign, resend_campaign or delete_campaign.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (listed is null)
            {
                var answer = await SendAsync(client, authorization, method, url, body, cancellationToken);
                yield return done ?? record(answer as JsonObject ?? new JsonObject());
                yield break;
            }

            // Page by page by offset, a page no bigger than what is left, until a short page, Mailchimp's total or the most asked for.
            var read = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = Math.Min(PageSize, max - read);
                var page = await SendAsync(client, authorization, HttpMethod.Get, url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "count=" + size.ToString(CultureInfo.InvariantCulture) + "&offset=" + read.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
                var found = (page?[listed] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var item in found)
                {
                    read++;
                    yield return record(item);
                    if (read >= max)
                    {
                        yield break;
                    }
                }

                if (found.Count < size || (page?["total_items"] is JsonValue total && total.TryGetValue<long>(out var all) && read >= all))
                {
                    yield break;
                }
            }
        }

        // A member's fields as Mailchimp's: merge fields a JSON object of its tags, a language, VIP and an email type.
        private static void MemberChange(JsonObject member, IReadOnlyDictionary<string, string> parameters)
        {
            if (Values.Get(parameters, "merge_fields") is { } merge)
            {
                member["merge_fields"] = MergeFields(merge);
            }

            if (Values.Get(parameters, "language") is { } language)
            {
                member["language"] = OneLine(language, "language");
            }

            if (Values.Get(parameters, "vip") is { } vip)
            {
                member["vip"] = Flag(vip, "vip");
            }

            if (Values.Get(parameters, "email_type") is { } type)
            {
                member["email_type"] = Word(type, EmailTypes, "kind of email");
            }
        }

        private static JsonObject MergeFields(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The merge fields are not JSON - {\"FNAME\": \"Ada\"}.");
            }

            if (parsed is not JsonObject fields || fields.Count == 0)
            {
                throw new InvalidOperationException("The merge fields are a JSON object of the audience's merge tags - {\"FNAME\": \"Ada\"}.");
            }

            foreach (var (tag, _) in fields)
            {
                if (!MergeTag.IsMatch(tag))
                {
                    throw new InvalidOperationException($"'{tag}' is not a merge tag (letters, digits and _) - FNAME.");
                }
            }

            return fields;
        }

        private static string MemberUrl(string api, IReadOnlyDictionary<string, string> parameters) =>
            $"{api}/lists/{Id(Values.Get(parameters, "list"), "audience")}/members/{Hash(Values.Get(parameters, "email") ?? throw new InvalidOperationException("Give the member's address."))}";

        private static string CampaignUrl(string api, IReadOnlyDictionary<string, string> parameters) => $"{api}/campaigns/{Id(Values.Get(parameters, "campaign"), "campaign")}";

        // Mailchimp's subscriber hash: the MD5 of the address in lower case - or a hash given as one.
        private static string Hash(string given) =>
            SubscriberHash.IsMatch(given.Trim()) ? given.Trim() : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Email(given).ToLowerInvariant()))).ToLowerInvariant();

        private static ConnectorRecord Done(IReadOnlyDictionary<string, string> parameters, string what) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["email"] = Values.Get(parameters, "email"),
            [what] = true,
        });

        private static ConnectorRecord CampaignDone(IReadOnlyDictionary<string, string> parameters, string what) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Values.Get(parameters, "campaign"),
            [what] = true,
        });

        private static ConnectorRecord Member(JsonObject member) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(member["id"]),
            ["email"] = Text(member["email_address"]),
            ["status"] = Text(member["status"]),
            ["full_name"] = Text(member["full_name"]),
            ["merge_fields"] = member["merge_fields"]?.ToJsonString() ?? "{}",
            ["tags"] = member["tags"] is JsonArray tags ? string.Join(", ", tags.OfType<JsonObject>().Select(tag => Text(tag["name"])).Where(name => name is not null)) : null,
            ["vip"] = member["vip"] is JsonValue vip && vip.TryGetValue<bool>(out var isVip) && isVip,
            ["language"] = Text(member["language"]),
            ["email_type"] = Text(member["email_type"]),
            ["member_rating"] = Number(member["member_rating"]),
            ["source"] = Text(member["source"]),
            ["list_id"] = Text(member["list_id"]),
            ["web_id"] = Number(member["web_id"]),
            ["timestamp_signup"] = Text(member["timestamp_signup"]),
            ["timestamp_opt"] = Text(member["timestamp_opt"]),
            ["last_changed"] = Text(member["last_changed"]),
        });

        private static ConnectorRecord Audience(JsonObject list) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(list["id"]),
            ["web_id"] = Number(list["web_id"]),
            ["name"] = Text(list["name"]),
            ["member_count"] = Number(list["stats"]?["member_count"]),
            ["unsubscribe_count"] = Number(list["stats"]?["unsubscribe_count"]),
            ["cleaned_count"] = Number(list["stats"]?["cleaned_count"]),
            ["subscribe_url"] = Text(list["subscribe_url_short"]),
            ["date_created"] = Text(list["date_created"]),
        });

        private static ConnectorRecord Campaign(JsonObject campaign) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(campaign["id"]),
            ["web_id"] = Number(campaign["web_id"]),
            ["type"] = Text(campaign["type"]),
            ["status"] = Text(campaign["status"]),
            ["title"] = Text(campaign["settings"]?["title"]),
            ["subject_line"] = Text(campaign["settings"]?["subject_line"]),
            ["from_name"] = Text(campaign["settings"]?["from_name"]),
            ["list_id"] = Text(campaign["recipients"]?["list_id"]),
            ["list_name"] = Text(campaign["recipients"]?["list_name"]),
            ["emails_sent"] = Number(campaign["emails_sent"]),
            ["archive_url"] = Text(campaign["archive_url"]),
            ["create_time"] = Text(campaign["create_time"]),
            ["send_time"] = Text(campaign["send_time"]) is { Length: > 0 } sent ? sent : null,
        });

        // The Marketing API of the key's data center: an admin page's address is taken to it, an address of another data center than
        // the key's refused; any other address (a stand-in's) as given, /3.0 added.
        private static string Api(string endpoint, string key)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Mailchimp API address - https://us21.api.mailchimp.com.");
            }

            var dash = key.LastIndexOf('-');
            var center = dash > 0 && DataCenter.IsMatch(key[(dash + 1)..]) ? key[(dash + 1)..] : null;
            var host = address.Host.ToLowerInvariant();
            if (MailchimpHost.Match(host) is { Success: true } named)
            {
                var dc = named.Groups["dc"].Value;
                if (center is not null && center != dc)
                {
                    throw new InvalidOperationException($"The API key is for the {center} data center and the address for {dc} - use https://{center}.api.mailchimp.com.");
                }

                return $"https://{dc}.api.mailchimp.com/3.0";
            }

            if (host == "mailchimp.com" || host.EndsWith(".mailchimp.com", StringComparison.Ordinal))
            {
                return center is not null ? $"https://{center}.api.mailchimp.com/3.0" : throw new InvalidOperationException($"'{endpoint}' names no data center - use its API's address, https://us21.api.mailchimp.com.");
            }

            return address.GetLeftPart(UriPartial.Authority) + "/3.0";
        }

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("api-key", out var key) && !string.IsNullOrWhiteSpace(key) ? key.Trim() : throw new InvalidOperationException("The Mailchimp connection has no API key.");

        // Mailchimp's Basic pair: any name, and the key.
        private static AuthenticationHeaderValue Authorization(string key) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("stomme:" + key)));

        private static string Id(string? given, string what) =>
            given is not null && Ids.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Mailchimp {what}'s id (letters and digits).");

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words)}.");

        private static string Email(string given) =>
            given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        // A time as Mailchimp takes it: ISO 8601 with its offset.
        private static string Time(string given) =>
            DateTimeOffset.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-01T00:00:00Z.");

        private static JsonArray List(string given, string what) =>
            new(given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => (JsonNode)JsonValue.Create(OneLine(item, what))!).ToArray());

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Mailchimp's answer as JSON (none for a 204); a throttled call waits as Mailchimp says; a failure thrown with its status,
        // Mailchimp's title and detail, and its first field error.
        private static async Task<JsonNode?> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
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
                throw new InvalidOperationException($"Mailchimp answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var field = (json?["errors"] as JsonArray)?.OfType<JsonObject>().Select(error => (Text(error["field"]), Text(error["message"]))).FirstOrDefault(error => error.Item2 is not null);
                var fieldError = field is { } named ? (string.IsNullOrEmpty(named.Item1) ? named.Item2 : named.Item1 + ": " + named.Item2) : null;
                var words = string.Join(" - ", new[] { Text(json?["title"]), Text(json?["detail"]), fieldError }.Where(part => !string.IsNullOrEmpty(part)).Distinct());
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return json;
        }
    }
}
