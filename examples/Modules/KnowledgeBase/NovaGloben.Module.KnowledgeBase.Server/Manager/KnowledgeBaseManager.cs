#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Interfaces;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Repository;
using Stomme.Shared;
using NovaGloben.Module.KnowledgeBase.Application;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Models;
using NovaGloben.Module.KnowledgeBase.Persistence;
using ModuleModel = Stomme.Models.Module;

namespace NovaGloben.Module.KnowledgeBase.Manager
{
    /// <summary>
    /// The module's SERVER MANAGER: the type <c>ModuleDefinition.ServerManagerType</c> names, which the host creates with
    /// <c>ActivatorUtilities</c> wherever it asks a placed module for something - the search index job
    /// (<see cref="ISearchable"/>), the sitemap (<see cref="ISitemap"/>) and the module content export and import
    /// (<see cref="IPortable"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both see the PUBLISHED articles only.</b> A draft is an editor's, so it never reaches the index, the
    /// sitemap or a search result - the host's search trims results by the module's view permission, which every visitor
    /// holds, so the only thing that keeps a draft out is that the manager never offers it.
    /// </para>
    /// <para>
    /// <b>The host has resolved the site before it asks</b>: the index job sets its alias per site, and the sitemap is a
    /// request. The module store filters by the resolved tenant, and the repository by the placed module's site, so
    /// the manager reads through the repository alone and carries no scope of its own.
    /// </para>
    /// <para>
    /// An article's address is its page's own URL with the article after <c>/!/</c> - the address the module's pages
    /// navigate to - so a search result and a sitemap entry open exactly what a visitor would have clicked.
    /// </para>
    /// </remarks>
    public sealed class KnowledgeBaseManager(IArticleRepository articles, ICategoryRepository categories, IPageRepository pages, IMediator mediator)
        : ISearchable, ISitemap, IPortable
    {
        /// <summary>The shape of the module's exported content; an import refuses any other.</summary>
        public const int ContentSchema = 1;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>The entity name the site's search index files an article under (the index's own <c>Page</c> and <c>Module</c> beside it).</summary>
        public const string SearchEntityName = "KnowledgeBaseArticle";

        /// <summary>A search result's description is the start of the body, at most this many characters.</summary>
        public const int DescriptionLength = 200;

        /// <summary>
        /// The site's published articles changed since <paramref name="lastIndexedOn"/> - every one when the host re-indexes
        /// the placement (it passes <see cref="DateTime.MinValue"/>). An edit and a publish both move <c>UpdatedUtc</c>.
        /// </summary>
        public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn)
        {
            ArgumentNullException.ThrowIfNull(pageModule);
            var page = pageModule.Page ?? await pages.GetPageAsync(pageModule.PageId);
            if (page is null)
            {
                return new List<SearchContent>();
            }

            return (await PublishedAsync(pageModule.Module.SiteId))
                .Where(article => article.UpdatedUtc >= lastIndexedOn)
                .Select(article => new SearchContent
                {
                    EntityName = SearchEntityName,
                    EntityId = article.Id.ToString(),
                    Title = article.Title.Value,
                    Description = Excerpt(article.Body),
                    Body = article.Body,
                    Url = ArticleUrl(string.Empty, page.Path, article.Slug),
                    ContentModifiedOn = article.UpdatedUtc,
                })
                .ToList();
        }

        /// <summary>One sitemap entry per published article of the placed module's site, under the page it is placed on.</summary>
        public async Task<List<Sitemap>> GetUrlsAsync(string alias, string path, ModuleModel module)
        {
            ArgumentNullException.ThrowIfNull(module);
            return (await PublishedAsync(module.SiteId))
                .Select(article => new Sitemap { Url = ArticleUrl(alias, path, article.Slug), ModifiedOn = article.UpdatedUtc })
                .ToList();
        }

        /// <summary>
        /// The module's content for the host's module export (the page's "Export Content") and, through the host's
        /// <c>module-content</c> contributor, for a site's configuration package: the site's categories and every article,
        /// drafts included - an exporter holds the page's edit permission, so the drafts are theirs to see.
        /// </summary>
        public async Task<string> ExportModuleAsync(ModuleModel module)
        {
            ArgumentNullException.ThrowIfNull(module);
            var siteCategories = await categories.ListForSiteAsync(module.SiteId);
            var slugById = siteCategories.ToDictionary(category => category.Id, category => category.Slug);
            var content = new ModuleContent(
                ContentSchema,
                siteCategories.Select(category => new ContentCategory(category.Name, category.Slug)).ToList(),
                (await articles.ListForSiteAsync(module.SiteId))
                    .OrderBy(article => article.CreatedUtc)
                    .Select(article => new ContentArticle(
                        article.Title.Value,
                        article.Slug,
                        article.Body,
                        article.CategoryId is { } categoryId && slugById.TryGetValue(categoryId, out var slug) ? slug : null,
                        article.Status.ToString()))
                    .ToList());
            return JsonSerializer.Serialize(content, Json);
        }

        /// <summary>
        /// Imports exported content into the placed module's site through the module's OWN gated commands, so the host's
        /// authorization, the author stamp and the slug rules apply exactly as when an editor types it: a missing category is
        /// created, an article whose address is free is drafted, and one whose address is taken is left alone. <b>An import
        /// never publishes</b> - a publish is quota-checked, metered and announced, so it stays an editor's act.
        /// </summary>
        public async Task ImportModuleAsync(ModuleModel module, string content, string version)
        {
            ArgumentNullException.ThrowIfNull(module);
            var package = JsonSerializer.Deserialize<ModuleContent>(content ?? string.Empty, Json);
            if (package is null || package.Schema != ContentSchema)
            {
                throw new InvalidOperationException($"This is not knowledge-base content of schema {ContentSchema}.");
            }

            var categoryIds = (await categories.ListForSiteAsync(module.SiteId)).ToDictionary(category => category.Slug, category => category.Id, StringComparer.Ordinal);
            foreach (var category in package.Categories ?? new List<ContentCategory>())
            {
                if (!categoryIds.ContainsKey(Slug.From(category.Slug)))
                {
                    var created = await mediator.Send(new CreateCategoryCommand(category.Name));
                    categoryIds[Slug.From(category.Slug)] = created.Id;
                }
            }

            foreach (var article in package.Articles ?? new List<ContentArticle>())
            {
                if (await articles.FindBySlugAsync(module.SiteId, article.Slug) is null)
                {
                    Guid? categoryId = article.Category is { } slug && categoryIds.TryGetValue(Slug.From(slug), out var id) ? id : null;
                    await mediator.Send(new DraftArticleCommand(article.Title, article.Slug, article.Body, categoryId));
                }
            }
        }

        /// <summary>An article's address: the page's URL with <c>/!/article/{slug}</c> - what the module's pages navigate to.</summary>
        public static string ArticleUrl(string alias, string pagePath, string slug) =>
            Utilities.NavigateUrl(alias, pagePath, Utilities.AddUrlParameters("article", slug));

        private async Task<IReadOnlyList<Article>> PublishedAsync(int siteId) =>
            (await articles.ListForSiteAsync(siteId)).Where(article => article.Status == ArticleStatus.Published).ToList();

        private static string Excerpt(string body)
        {
            var flat = string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= DescriptionLength ? flat : flat.Substring(0, DescriptionLength - 1).TrimEnd() + "…";
        }

        /// <summary>The exported content: a schema number, the categories and the articles by address.</summary>
        public sealed record ModuleContent(int Schema, List<ContentCategory>? Categories, List<ContentArticle>? Articles);

        public sealed record ContentCategory(string Name, string Slug);

        public sealed record ContentArticle(string Title, string Slug, string Body, string? Category, string Status);
    }
}
