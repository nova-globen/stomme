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

namespace Stomme.Modules.Connectors.Reddit
{
    /// <summary>
    /// Reddit (BL-229, n8n's Reddit node: posts, comments, the profile, subreddits and users): an account over Reddit's OAuth API
    /// (<c>https://oauth.reddit.com</c>) through an app's client and a permanent refresh token, exchanged at Reddit's token
    /// endpoint (the client as HTTP Basic) through the shared <see cref="OAuth2TokenProvider"/>, the access token a bearer and the
    /// <c>user_agent</c> setting on every call, as Reddit asks. A test names the account.
    /// </summary>
    /// <remarks>
    /// A subreddit is its name (r/ dropped), a post <c>t3_</c> and its id or the id alone, a comment <c>t1_</c> and its id, a
    /// user a name - each held to its shape before anything is sent. <c>me</c>; <c>submit</c> (a text or link post, a title,
    /// NSFW, a spoiler, replies to the inbox, a flair, a resubmission); <c>posts</c> (a subreddit's hot, new, top, rising or
    /// controversial over a time) and <c>search</c> (all of Reddit or one subreddit, by relevance, hot, top, new or comments);
    /// <c>get_post</c>; <c>comments</c> (a post's, depth first, the "more" stubs left out); <c>comment</c> (on a post or as a
    /// reply), <c>edit</c> and <c>delete</c>; <c>subreddit</c> and <c>rules</c>; <c>subreddits</c> (found by words, or the
    /// popular ones); <c>user</c> and <c>user_activity</c> (overview, submitted or comments). Lists page by page by Reddit's
    /// after up to <c>max</c> (1,000). Each a record - times in UTC. A throttled call waits as Reddit says; a failure is thrown
    /// with its status and Reddit's word; a refused submission or comment with Reddit's own errors.
    /// </remarks>
    public sealed class RedditConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://oauth.reddit.com";

        public const string DefaultTokenUrl = "https://www.reddit.com/api/v1/access_token";

        public const string DefaultUserAgent = "web:stomme-pipelines:1.0";

        public const string MeOperation = "me";

        public const string SubmitOperation = "submit";

        public const string PostsOperation = "posts";

        public const string SearchOperation = "search";

        public const string GetPostOperation = "get_post";

        public const string CommentsOperation = "comments";

        public const string CommentOperation = "comment";

        public const string EditOperation = "edit";

        public const string DeleteOperation = "delete";

        public const string SubredditOperation = "subreddit";

        public const string RulesOperation = "rules";

        public const string SubredditsOperation = "subreddits";

        public const string UserOperation = "user";

        public const string UserActivityOperation = "user_activity";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Subreddit = new("^[A-Za-z0-9][A-Za-z0-9_]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Base36 = new("^[a-z0-9]{1,13}$", RegexOptions.CultureInvariant);

        private static readonly Regex UserName = new("^[A-Za-z0-9_-]{3,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex FlairId = new("^[0-9a-f-]{36}$", RegexOptions.CultureInvariant);

        private static readonly string[] Times = { "hour", "day", "week", "month", "year", "all" };

        private static readonly string[] Known = { MeOperation, SubmitOperation, PostsOperation, SearchOperation, GetPostOperation, CommentsOperation, CommentOperation, EditOperation, DeleteOperation, SubredditOperation, RulesOperation, SubredditsOperation, UserOperation, UserActivityOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "reddit";

        public string DisplayName => "Reddit";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.reddit.com");

        public Uri ApiDocumentationUrl { get; } = new("https://www.reddit.com/dev/api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("user_agent", "User agent", ConnectorConfigFieldType.Text, Required: false,
                Description: "How Reddit asks an app to name itself - platform:app:version (by /u/you).") { Default = DefaultUserAgent },
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Reddit's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField SubredditField = new("subreddit", "Subreddit", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - fika, or r/fika.");

        private static readonly ConnectorConfigField PostField = new("post", "Post", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - 1abc2de, or t3_1abc2de.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField TimeField() => new("time", "Over", ConnectorConfigFieldType.Text, Required: false, Description: "hour, day, week, month, year or all - for top and controversial.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(MeOperation, "Get my account", "The signed-in account - its name and karma; n8n's profile Get.") { Parameters = Schema() },
            new ConnectorOperation(SubmitOperation, "Submit a post", "A text or link post to a subreddit - n8n's post Create.")
            {
                Parameters = Schema(
                    SubredditField,
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: true, Description: "Up to 300 characters."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "A text post's body, in Markdown."),
                    new ConnectorConfigField("url", "Link", ConnectorConfigFieldType.Text, Required: false, Description: "A link post's address - https://example.com/fika."),
                    new ConnectorConfigField("nsfw", "NSFW", ConnectorConfigFieldType.Text, Required: false, Description: "true to mark it."),
                    new ConnectorConfigField("spoiler", "Spoiler", ConnectorConfigFieldType.Text, Required: false, Description: "true to mark it."),
                    new ConnectorConfigField("send_replies", "Replies to inbox", ConnectorConfigFieldType.Text, Required: false, Description: "false to not be told of replies.") { Default = "true" },
                    new ConnectorConfigField("flair", "Flair", ConnectorConfigFieldType.Text, Required: false, Description: "A flair template's id."),
                    new ConnectorConfigField("resubmit", "Resubmit", ConnectorConfigFieldType.Text, Required: false, Description: "true to post a link already posted there.")),
            },
            new ConnectorOperation(PostsOperation, "Get posts", "A subreddit's posts - n8n's post Get Many.")
            {
                Parameters = Schema(
                    SubredditField,
                    new ConnectorConfigField("sort", "Which", ConnectorConfigFieldType.Text, Required: false, Description: "hot, new, top, rising or controversial.") { Default = "hot" },
                    TimeField(), Max()),
            },
            new ConnectorOperation(SearchOperation, "Search posts", "Posts by words, in all of Reddit or one subreddit - n8n's post Search.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("query", "Words", ConnectorConfigFieldType.Text, Required: true, Description: "What to search for."),
                    new ConnectorConfigField("subreddit", "Subreddit", ConnectorConfigFieldType.Text, Required: false, Description: "Only this one - all of Reddit when left out."),
                    new ConnectorConfigField("sort", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "relevance, hot, top, new or comments.") { Default = "relevance" },
                    TimeField(), Max()),
            },
            new ConnectorOperation(GetPostOperation, "Get a post", "One post - n8n's post Get.") { Parameters = Schema(PostField) },
            new ConnectorOperation(CommentsOperation, "Get comments", "A post's comments, depth first - n8n's comment Get Many.")
            {
                Parameters = Schema(PostField, new ConnectorConfigField("sort", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "confidence, top, new, controversial, old or qa."), Max()),
            },
            new ConnectorOperation(CommentOperation, "Comment", "Comments on a post, or replies to a comment - n8n's comment Create and Reply.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("on", "On", ConnectorConfigFieldType.Text, Required: true, Description: "t3_ and a post's id, or t1_ and a comment's."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "In Markdown.")),
            },
            new ConnectorOperation(EditOperation, "Edit", "Changes a text post's or a comment's text.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("thing", "Post or comment", ConnectorConfigFieldType.Text, Required: true, Description: "t3_ and a post's id, or t1_ and a comment's."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "In Markdown.")),
            },
            new ConnectorOperation(DeleteOperation, "Delete", "Deletes a post or a comment of the account's - n8n's post and comment Delete.")
            {
                Parameters = Schema(new ConnectorConfigField("thing", "Post or comment", ConnectorConfigFieldType.Text, Required: true, Description: "t3_ and a post's id, or t1_ and a comment's.")),
            },
            new ConnectorOperation(SubredditOperation, "Get a subreddit", "A subreddit's about - n8n's subreddit Get.") { Parameters = Schema(SubredditField) },
            new ConnectorOperation(RulesOperation, "Get a subreddit's rules", "A subreddit's rules - n8n's subreddit Get of its rules.") { Parameters = Schema(SubredditField) },
            new ConnectorOperation(SubredditsOperation, "Get subreddits", "Subreddits found by words, or the popular ones - n8n's subreddit Get Many.")
            {
                Parameters = Schema(new ConnectorConfigField("query", "Words", ConnectorConfigFieldType.Text, Required: false, Description: "What to search for - the popular ones when left out."), Max()),
            },
            new ConnectorOperation(UserOperation, "Get a user", "A user's about - n8n's user Get.")
            {
                Parameters = Schema(new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - spez, or u/spez.")),
            },
            new ConnectorOperation(UserActivityOperation, "Get a user's activity", "A user's posts and comments - n8n's user Get of its overview, submitted or comments.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - spez, or u/spez."),
                    new ConnectorConfigField("what", "What", ConnectorConfigFieldType.Text, Required: false, Description: "overview, submitted or comments.") { Default = "overview" },
                    Max()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = await SessionAsync(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/api/v1/me", null, cancellationToken) as JsonObject ?? new JsonObject();
                return ConnectionTestResult.Ok($"Signed in as u/{Text(me["name"]) ?? "?"}{(Long(me["total_karma"]) is { } karma ? $" ({karma.ToString("N0", CultureInfo.InvariantCulture)} karma)" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PostsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Reddit has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent - not even a token asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var subreddit = operation is SubmitOperation or PostsOperation or SubredditOperation or RulesOperation ? SubredditOf(Values.Get(p, "subreddit"))
                : operation == SearchOperation && Values.Get(p, "subreddit") is { } within ? SubredditOf(within) : null;
            var post = operation is GetPostOperation or CommentsOperation ? Fullname(Values.Get(p, "post"), "post", "t3_", allowBare: true) : null;
            var thing = operation switch
            {
                CommentOperation => Fullname(Values.Get(p, "on"), "post or comment", null, allowBare: false),
                EditOperation or DeleteOperation => Fullname(Values.Get(p, "thing"), "post or comment", null, allowBare: false),
                _ => null,
            };
            var user = operation is UserOperation or UserActivityOperation ? UserOf(Values.Get(p, "user")) : null;
            var form = new List<KeyValuePair<string, string>>();
            string? path = null;
            var query = new List<string>();
            switch (operation)
            {
                case SubmitOperation:
                    {
                        var title = Values.Get(p, "title") ?? throw new InvalidOperationException("Give the post's title.");
                        if (title.Length > 300 || title.Contains('\n', StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(title.Length > 300 ? "The title is longer than 300 characters." : "The title has a line break - one line only.");
                        }

                        var url = Values.Get(p, "url");
                        var text = p.TryGetValue("text", out var body) && !string.IsNullOrWhiteSpace(body) ? body : null;
                        if (url is not null && text is not null)
                        {
                            throw new InvalidOperationException("A post is text or a link - not both.");
                        }

                        form.Add(new("api_type", "json"));
                        form.Add(new("sr", subreddit!));
                        form.Add(new("title", title));
                        if (url is not null)
                        {
                            form.Add(new("kind", "link"));
                            form.Add(new("url", Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp) ? url : throw new InvalidOperationException($"'{url}' is not a link - https://example.com/fika.")));
                            form.Add(new("resubmit", Values.Get(p, "resubmit") is { } again && Flag(again, "resubmit") ? "true" : "false"));
                        }
                        else
                        {
                            form.Add(new("kind", "self"));
                            form.Add(new("text", text ?? ""));
                        }

                        foreach (var (name, key) in new[] { ("nsfw", "nsfw"), ("spoiler", "spoiler") })
                        {
                            if (Values.Get(p, name) is { } flag && Flag(flag, name))
                            {
                                form.Add(new(key, "true"));
                            }
                        }

                        form.Add(new("sendreplies", Values.Get(p, "send_replies") is { } replies ? (Flag(replies, "replies to inbox") ? "true" : "false") : "true"));
                        if (Values.Get(p, "flair") is { } flair)
                        {
                            form.Add(new("flair_id", FlairId.IsMatch(flair) ? flair : throw new InvalidOperationException($"'{flair}' is not a flair template's id.")));
                        }

                        break;
                    }

                case PostsOperation:
                    {
                        var sort = Choice(Values.Get(p, "sort") ?? "hot", "a sort", "hot", "new", "top", "rising", "controversial");
                        path = $"/r/{subreddit}/{sort}";
                        if (Values.Get(p, "time") is { } time)
                        {
                            query.Add("t=" + (sort is "top" or "controversial" ? Choice(time, "a time", Times) : throw new InvalidOperationException("A time is for top and controversial only.")));
                        }

                        break;
                    }

                case SearchOperation:
                    {
                        var words = Values.Get(p, "query") ?? throw new InvalidOperationException("Give the words to search for.");
                        path = subreddit is null ? "/search" : $"/r/{subreddit}/search";
                        query.Add("q=" + Uri.EscapeDataString(OneLine(words, "words")));
                        if (subreddit is not null)
                        {
                            query.Add("restrict_sr=true");
                        }

                        query.Add("type=link");
                        query.Add("sort=" + Choice(Values.Get(p, "sort") ?? "relevance", "an order", "relevance", "hot", "top", "new", "comments"));
                        if (Values.Get(p, "time") is { } time)
                        {
                            query.Add("t=" + Choice(time, "a time", Times));
                        }

                        break;
                    }

                case CommentsOperation:
                    if (Values.Get(p, "sort") is { } order)
                    {
                        query.Add("sort=" + Choice(order, "an order", "confidence", "top", "new", "controversial", "old", "qa"));
                    }

                    break;
                case CommentOperation:
                case EditOperation:
                    {
                        var text = p.TryGetValue("text", out var body) && !string.IsNullOrWhiteSpace(body) ? body : throw new InvalidOperationException("Give the text.");
                        form.Add(new("api_type", "json"));
                        form.Add(new("thing_id", thing!));
                        form.Add(new("text", text.Length <= 10000 ? text : throw new InvalidOperationException("The text is longer than 10,000 characters.")));
                        break;
                    }

                case DeleteOperation:
                    form.Add(new("id", thing!));
                    break;
                case SubredditsOperation:
                    if (Values.Get(p, "query") is { } words2)
                    {
                        path = "/subreddits/search";
                        query.Add("q=" + Uri.EscapeDataString(OneLine(words2, "words")));
                    }
                    else
                    {
                        path = "/subreddits/popular";
                    }

                    break;
                case UserActivityOperation:
                    path = $"/user/{user}/{Choice(Values.Get(p, "what") ?? "overview", "a user's activity", "overview", "submitted", "comments")}";
                    break;
            }

            var session = await SessionAsync(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            using var client = session.Client;
            if (path is not null)
            {
                var read = 0;
                string? after = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var page = await session.SendAsync(HttpMethod.Get, $"{path}?{string.Join("&", query.Append("limit=" + size.ToString(CultureInfo.InvariantCulture)))}{(after is null ? "" : "&after=" + Uri.EscapeDataString(after))}&raw_json=1", null, cancellationToken);
                    var children = (page?["data"]?["children"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var child in children)
                    {
                        if (Record(child) is { } record)
                        {
                            yield return record;
                            if (++read >= max)
                            {
                                yield break;
                            }
                        }
                    }

                    after = children.Count > 0 && Text(page?["data"]?["after"]) is { Length: > 0 } next ? next : null;
                }
                while (after is not null);

                yield break;
            }

            switch (operation)
            {
                case MeOperation:
                    yield return UserRecord(await session.SendAsync(HttpMethod.Get, "/api/v1/me?raw_json=1", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case UserOperation:
                    yield return UserRecord((await session.SendAsync(HttpMethod.Get, $"/user/{user}/about?raw_json=1", null, cancellationToken))?["data"] as JsonObject ?? new JsonObject());
                    yield break;
                case SubredditOperation:
                    yield return SubredditRecord((await session.SendAsync(HttpMethod.Get, $"/r/{subreddit}/about?raw_json=1", null, cancellationToken))?["data"] as JsonObject ?? new JsonObject());
                    yield break;
                case RulesOperation:
                    foreach (var rule in ((await session.SendAsync(HttpMethod.Get, $"/r/{subreddit}/about/rules?raw_json=1", null, cancellationToken))?["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["subreddit"] = subreddit,
                            ["priority"] = Long(rule["priority"]),
                            ["name"] = Text(rule["short_name"]),
                            ["description"] = Text(rule["description"]),
                            ["kind"] = Text(rule["kind"]),
                            ["violation_reason"] = Text(rule["violation_reason"]),
                            ["created"] = Time(rule["created_utc"]),
                        });
                    }

                    yield break;
                case GetPostOperation:
                    if (((await session.SendAsync(HttpMethod.Get, $"/by_id/{post}?raw_json=1", null, cancellationToken))?["data"]?["children"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault() is { } found && Record(found) is { } one)
                    {
                        yield return one;
                        yield break;
                    }

                    throw new InvalidOperationException($"No post has the id {post}.");
                case CommentsOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/comments/{post![3..]}?{string.Join("&", query.Append("limit=" + Math.Min(max, 500).ToString(CultureInfo.InvariantCulture)))}&raw_json=1", null, cancellationToken) as JsonArray ?? new JsonArray();
                        var read = 0;
                        foreach (var comment in Flatten(answer.Count > 1 ? answer[1]?["data"]?["children"] as JsonArray : null))
                        {
                            yield return CommentRecord(comment);
                            if (++read >= max)
                            {
                                yield break;
                            }
                        }

                        yield break;
                    }

                case SubmitOperation:
                    {
                        var made = Refused(await session.SendFormAsync("/api/submit", form, cancellationToken));
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = Text(made?["id"]),
                            ["name"] = Text(made?["name"]),
                            ["subreddit"] = subreddit,
                            ["url"] = Text(made?["url"]),
                        });
                        yield break;
                    }

                case CommentOperation:
                case EditOperation:
                    {
                        var made = Refused(await session.SendFormAsync(operation == CommentOperation ? "/api/comment" : "/api/editusertext", form, cancellationToken));
                        var data = made?["things"]?[0]?["data"] as JsonObject ?? new JsonObject();
                        yield return Text(data["name"])?.StartsWith("t3_", StringComparison.Ordinal) == true ? PostRecord(data) : CommentRecord(data);
                        yield break;
                    }

                default:
                    await session.SendFormAsync("/api/del", form, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["thing"] = thing, ["done"] = true });
                    yield break;
            }
        }

        // A comment tree depth first - Reddit's "more" stubs, which name comments not sent, left out.
        private static IEnumerable<JsonObject> Flatten(JsonArray? children)
        {
            foreach (var child in (children ?? new JsonArray()).OfType<JsonObject>())
            {
                if (Text(child["kind"]) != "t1" || child["data"] is not JsonObject data)
                {
                    continue;
                }

                yield return data;

                // A comment with no replies has an empty string for them, as Reddit sends it.
                foreach (var reply in Flatten(data["replies"] is JsonObject replies ? replies["data"]?["children"] as JsonArray : null))
                {
                    yield return reply;
                }
            }
        }

        private static ConnectorRecord? Record(JsonObject child) => Text(child["kind"]) switch
        {
            "t3" => PostRecord(child["data"] as JsonObject ?? new JsonObject()),
            "t1" => CommentRecord(child["data"] as JsonObject ?? new JsonObject()),
            "t5" => SubredditRecord(child["data"] as JsonObject ?? new JsonObject()),
            _ => null,
        };

        private static ConnectorRecord PostRecord(JsonObject post) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "post",
            ["id"] = Text(post["id"]),
            ["name"] = Text(post["name"]),
            ["subreddit"] = Text(post["subreddit"]),
            ["title"] = Text(post["title"]),
            ["author"] = Text(post["author"]),
            ["text"] = Text(post["selftext"]),
            ["url"] = Text(post["url"]),
            ["permalink"] = Text(post["permalink"]) is { } link ? "https://www.reddit.com" + link : null,
            ["score"] = Long(post["score"]),
            ["upvote_ratio"] = post["upvote_ratio"] is JsonValue ratio && ratio.TryGetValue<double>(out var r) ? r : null,
            ["comments"] = Long(post["num_comments"]),
            ["created"] = Time(post["created_utc"]),
            ["nsfw"] = Bool(post["over_18"]),
            ["spoiler"] = Bool(post["spoiler"]),
            ["flair"] = Text(post["link_flair_text"]),
            ["domain"] = Text(post["domain"]),
        });

        private static ConnectorRecord CommentRecord(JsonObject comment) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "comment",
            ["id"] = Text(comment["id"]),
            ["name"] = Text(comment["name"]),
            ["post"] = Text(comment["link_id"]),
            ["parent"] = Text(comment["parent_id"]),
            ["subreddit"] = Text(comment["subreddit"]),
            ["author"] = Text(comment["author"]),
            ["text"] = Text(comment["body"]),
            ["score"] = Long(comment["score"]),
            ["depth"] = Long(comment["depth"]),
            ["created"] = Time(comment["created_utc"]),
            ["permalink"] = Text(comment["permalink"]) is { } link ? "https://www.reddit.com" + link : null,
        });

        private static ConnectorRecord SubredditRecord(JsonObject sub) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = Text(sub["display_name"]),
            ["title"] = Text(sub["title"]),
            ["description"] = Text(sub["public_description"]),
            ["subscribers"] = Long(sub["subscribers"]),
            ["nsfw"] = Bool(sub["over18"]),
            ["created"] = Time(sub["created_utc"]),
            ["url"] = Text(sub["url"]) is { } path ? "https://www.reddit.com" + path : null,
        });

        private static ConnectorRecord UserRecord(JsonObject user) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = Text(user["name"]),
            ["id"] = Text(user["id"]),
            ["link_karma"] = Long(user["link_karma"]),
            ["comment_karma"] = Long(user["comment_karma"]),
            ["total_karma"] = Long(user["total_karma"]),
            ["created"] = Time(user["created_utc"]),
            ["verified"] = Bool(user["has_verified_email"]) ?? Bool(user["verified"]),
            ["is_mod"] = Bool(user["is_mod"]),
        });

        // Reddit's own errors in a submission's or comment's answer: [code, message, field] each.
        private static JsonObject? Refused(JsonNode? answer)
        {
            var errors = (answer?["json"]?["errors"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Reddit refused it: " + string.Join("; ", errors.Select(error => string.Join(" - ", error.Select(Text).Where(part => !string.IsNullOrEmpty(part))))));
            }

            return answer?["json"]?["data"] as JsonObject;
        }

        // Reddit's times are seconds since 1970; here UTC.
        private static string? Time(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;

        private static string SubredditOf(string? given)
        {
            var name = given is null ? throw new InvalidOperationException("Give the subreddit - its name.") : given.Trim().StartsWith("r/", StringComparison.OrdinalIgnoreCase) ? given.Trim()[2..] : given.Trim().StartsWith("/r/", StringComparison.OrdinalIgnoreCase) ? given.Trim()[3..] : given.Trim();
            return Subreddit.IsMatch(name) ? name : throw new InvalidOperationException($"'{given}' is not a subreddit - its name, fika.");
        }

        private static string UserOf(string? given)
        {
            var name = given is null ? throw new InvalidOperationException("Give the user - its name.") : given.Trim().StartsWith("u/", StringComparison.OrdinalIgnoreCase) ? given.Trim()[2..] : given.Trim().StartsWith("/u/", StringComparison.OrdinalIgnoreCase) ? given.Trim()[3..] : given.Trim();
            return UserName.IsMatch(name) ? name : throw new InvalidOperationException($"'{given}' is not a user - its name, spez.");
        }

        // A post's or comment's full name - t3_ or t1_ and its base-36 id; a bare id read as the kind asked for.
        private static string Fullname(string? given, string what, string? bareAs, bool allowBare)
        {
            var value = given?.Trim() ?? throw new InvalidOperationException($"Give the {what} - {(allowBare ? "its id" : "t3_ or t1_ and its id")}.");
            if ((value.StartsWith("t3_", StringComparison.Ordinal) || value.StartsWith("t1_", StringComparison.Ordinal)) && Base36.IsMatch(value[3..]) && (bareAs is null || value.StartsWith(bareAs, StringComparison.Ordinal)))
            {
                return value;
            }

            return allowBare && bareAs is not null && Base36.IsMatch(value) ? bareAs + value
                : throw new InvalidOperationException($"'{given}' is not a {what} - {(allowBare ? "its id, 1abc2de, or " + bareAs + "1abc2de" : "t3_ or t1_ and its id, t3_1abc2de")}.");
        }

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private async Task<Session> SessionAsync(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var api = Values.BaseUrl(endpoint, DefaultEndpoint);
            var agent = Values.Get(settings, "user_agent") ?? DefaultUserAgent;
            if (agent.Contains('\n', StringComparison.Ordinal) || agent.Contains('\r', StringComparison.Ordinal) || agent.Length > 200)
            {
                throw new InvalidOperationException("The user agent is one line of up to 200 characters.");
            }

            var token = await tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                ClientAsBasic = true,
                UserAgent = agent,
                Timeout = timeout,
            }, cancellationToken);
            return new Session(api, token, agent, egress.CreateHttpClient(timeout));
        }

        // One connection's calls: the token as a bearer and the user agent Reddit asks for; a throttled call waiting as Reddit
        // says; a failure thrown with its status and Reddit's word.
        private sealed class Session(string api, string token, string agent, HttpClient client)
        {
            public HttpClient Client { get; } = client;

            public Task<JsonNode?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken) =>
                SendCoreAsync(method, path, () => content, cancellationToken);

            public Task<JsonNode?> SendFormAsync(string path, IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken) =>
                SendCoreAsync(HttpMethod.Post, path, () => new FormUrlEncodedContent(form), cancellationToken);

            private async Task<JsonNode?> SendCoreAsync(HttpMethod method, string path, Func<HttpContent?> content, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path) { Content = content() };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("User-Agent", agent);
                    request.Headers.Accept.ParseAdd("application/json");
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
                    throw new InvalidOperationException($"Reddit answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["reason"]) : null;
                    var reason = json is JsonObject withReason && Text(withReason["reason"]) is { } why && why != said ? why : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + said)}{(reason is null ? "" : $" ({reason})")}");
                }

                return json;
            }
        }
    }
}
