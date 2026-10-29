#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Mcp;
using Stomme.Mediator;
using NovaGloben.Module.KnowledgeBase.Application;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Discovery
{
    /// <summary>
    /// The module's feature flags, discovered by the host's convention (a public parameterless constructor in a
    /// <c>*.Stomme</c> assembly) and listed on the host's feature page. Declared here so the flag exists - with its
    /// description and its default - before any tenant turns it on.
    /// </summary>
    public sealed class KnowledgeBaseFeatureContributor : IFeatureContributor
    {
        /// <summary>The MCP surface's flag. <b>Default OFF</b>: a site's articles reach an AI client only when an operator says so.</summary>
        public const string Mcp = "knowledge-base.mcp";

        public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
        {
            new FeatureDescriptor(Mcp, "Expose the site's PUBLISHED knowledge-base articles to MCP clients: the article list and one article as resources, and the search_knowledge_base tool.", "MCP", DefaultEnabled: false),
        };
    }

    /// <summary>
    /// The module's MCP contribution: two resources and one read-only tool over the site's PUBLISHED
    /// articles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every entry is gated twice</b>, by the default-off <see cref="KnowledgeBaseFeatureContributor.Mcp"/> flag AND the
    /// <c>knowledge-base:view</c> key - the host's catalog lists and dispatches an entry only when both answer yes for the
    /// caller - and each read goes through the module's own gated queries, so the <c>[Requires]</c> on them is a third
    /// gate the contributor cannot bypass.
    /// </para>
    /// <para>
    /// <b>Drafts never leave through MCP</b>, not even to an editor's client: the queries answer an editor's drafts to the
    /// editor's PAGES, and this contributor keeps only the published ones - an agent reads what the public reads.
    /// </para>
    /// <para>
    /// It needs the mediator, so it is registered in the module's startup rather than discovered, and ONLY while the host's
    /// MCP server half is composed (<c>Mcp:Server:Enabled</c>): with the server half off the host has no catalog, and a
    /// module must not add a contributor to one that does not exist.
    /// </para>
    /// </remarks>
    public sealed class KnowledgeBaseMcpContributor(IMediator mediator) : IMcpContributor, IMcpResourceReader
    {
        public const string ListUri = "stomme://knowledge-base/articles";

        public const string ArticleTemplate = "stomme://knowledge-base/articles/{slug}";

        public const string SearchTool = "search_knowledge_base";

        /// <summary>A search answers at most this many articles, best first.</summary>
        public const int MaximumResults = 10;

        /// <summary>A search query is 1 to this many characters.</summary>
        public const int MaximumQueryLength = 200;

        private static readonly IReadOnlyList<McpAccessGate> ViewGate = new[] { new McpAccessGate(KnowledgeBaseFeatureContributor.Mcp, KnowledgeBasePermissions.View) };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string ContributorId => "nova-globen.knowledge-base";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(ListUri, "Knowledge base articles", "The site's published knowledge-base articles: title, address, category and publication date - no bodies.", "application/json", ViewGate),
            new McpResourceDescriptor(ArticleTemplate, "Knowledge base article", "One published article by its address (slug), with its body.", "application/json", ViewGate),
        };

        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
        {
            new McpToolDescriptor(SearchTool, "Search the site's published knowledge-base articles by words in their title or body; answers the best matches with an excerpt and each article's address.",
                """{"type":"object","properties":{"query":{"type":"string","description":"The words to look for."}},"required":["query"]}""", ViewGate, McpToolApproval.Auto),
        };

        public IMcpResourceReader? ReaderFor(string uriTemplate) => uriTemplate is ListUri or ArticleTemplate ? this : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => toolName == SearchTool ? new SearchInvoker(this) : null;

        public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            if (string.Equals(uri, ListUri, StringComparison.Ordinal))
            {
                return Content(uri, (await PublishedAsync(ct)).Select(Summary).ToList());
            }

            if (arguments.TryGetValue("slug", out var slug) && !string.IsNullOrWhiteSpace(slug))
            {
                var article = await mediator.Send(new GetArticleQuery(slug), ct);
                return article is { Status: ArticleStatus.Published }
                    ? Content(uri, new { title = article.Title, slug = article.Slug, categoryId = article.CategoryId, publishedUtc = article.PublishedUtc, body = article.Body })
                    : null;
            }

            return null;
        }

        /// <summary>
        /// The search: every word of the query must appear in the title or the body (ordinal, ignoring case); a title match
        /// ranks above a body match, then the newest first.
        /// </summary>
        internal async Task<McpToolResult> SearchAsync(string query, CancellationToken ct)
        {
            var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var matches = (await PublishedAsync(ct))
                .Where(article => words.All(word => article.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || article.Body.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(article => words.Count(word => article.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(article => article.PublishedUtc)
                .Take(MaximumResults)
                .Select(article => new { title = article.Title, slug = article.Slug, uri = ListUri + "/" + article.Slug, excerpt = Excerpt(article.Body) })
                .ToList();
            return Ok(new { query, results = matches });
        }

        private async Task<IReadOnlyList<ArticleModel>> PublishedAsync(CancellationToken ct) =>
            (await mediator.Send(new ListArticlesQuery(), ct)).Where(article => article.Status == ArticleStatus.Published).ToList();

        private static object Summary(ArticleModel article) => new
        {
            title = article.Title,
            slug = article.Slug,
            uri = ListUri + "/" + article.Slug,
            categoryId = article.CategoryId,
            publishedUtc = article.PublishedUtc,
        };

        private static string Excerpt(string body)
        {
            var flat = string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= 200 ? flat : flat.Substring(0, 199).TrimEnd() + "…";
        }

        private static McpResourceContent Content(string uri, object payload) => new(uri, "application/json", JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Ok(object payload) => new(JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Json), isError: true);

        /// <summary>The tool's argument handling: a malformed call is an MCP error result, never an exception.</summary>
        private sealed class SearchInvoker(KnowledgeBaseMcpContributor contributor) : IMcpToolInvoker
        {
            public async Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
            {
                string? query = null;
                try
                {
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        query = value.GetString()?.Trim();
                    }
                }
                catch (JsonException)
                {
                    return Error("The arguments must be a JSON object.");
                }

                if (string.IsNullOrEmpty(query) || query.Length > MaximumQueryLength)
                {
                    return Error($"The arguments need a 'query' of 1 to {MaximumQueryLength} characters.");
                }

                return await contributor.SearchAsync(query, ct);
            }
        }
    }
}
