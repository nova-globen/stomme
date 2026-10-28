using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Mediator;
using Stomme.Metering;
using Stomme.Models.Notifications;
using Stomme.Notifications;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Models;
using NovaGloben.Module.KnowledgeBase.Persistence;

namespace NovaGloben.Module.KnowledgeBase.Application
{
    // ================================================================================================================
    // The module's commands and queries. Each carries [Requires] with one of the module's keys (D99): the host's
    // authorization behaviour refuses a caller without it before the handler runs, whether the request came from the
    // API, a page or a job's scope. Every one works through the REQUEST's site - another site's rows do not exist for it.
    // ================================================================================================================

    [Requires(KnowledgeBasePermissions.Edit)]
    public sealed record CreateCategoryCommand(string Name) : ICommand<CategoryModel>;

    public sealed class CreateCategoryCommandHandler(ICategoryRepository categories, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock)
        : ICommandHandler<CreateCategoryCommand, CategoryModel>
    {
        public async Task<CategoryModel> Handle(CreateCategoryCommand request, CancellationToken ct)
        {
            var (tenantId, siteId) = KnowledgeBaseScope.Resolve(tenantContext, siteState);
            var category = Category.Create(Guid.NewGuid(), tenantId, siteId, request.Name, clock.GetUtcNow().UtcDateTime);
            if (await categories.FindBySlugAsync(siteId, category.Slug, ct) is not null)
            {
                throw new InvalidOperationException($"A category '{category.Slug}' already exists on this site.");
            }

            await categories.AddAsync(category, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return KnowledgeBaseMapping.ToModel(category);
        }
    }

    [Requires(KnowledgeBasePermissions.View)]
    public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategoryModel>>;

    public sealed class ListCategoriesQueryHandler(ICategoryRepository categories, SiteState siteState) : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryModel>>
    {
        public async Task<IReadOnlyList<CategoryModel>> Handle(ListCategoriesQuery request, CancellationToken ct) =>
            (await categories.ListForSiteAsync(KnowledgeBaseScope.Site(siteState), ct)).Select(KnowledgeBaseMapping.ToModel).ToList();
    }

    [Requires(KnowledgeBasePermissions.Edit)]
    public sealed record DraftArticleCommand(string Title, string? Slug, string Body, Guid? CategoryId) : ICommand<ArticleModel>;

    public sealed class DraftArticleCommandHandler(
        IArticleRepository articles,
        ICategoryRepository categories,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        ICurrentUser currentUser,
        TimeProvider clock) : ICommandHandler<DraftArticleCommand, ArticleModel>
    {
        public async Task<ArticleModel> Handle(DraftArticleCommand request, CancellationToken ct)
        {
            var (tenantId, siteId) = KnowledgeBaseScope.Resolve(tenantContext, siteState);
            var author = currentUser.UserId ?? throw new InvalidOperationException("An article is drafted by a signed-in user.");
            if (request.CategoryId is { } categoryId && (await categories.GetByIdAsync(categoryId, ct))?.SiteId != siteId)
            {
                throw new InvalidOperationException($"Category '{categoryId}' is not this site's.");
            }

            var article = Article.Draft(Guid.NewGuid(), tenantId, siteId, request.CategoryId, request.Title, request.Slug, request.Body, author, clock.GetUtcNow().UtcDateTime);
            if (await articles.FindBySlugAsync(siteId, article.Slug, ct) is not null)
            {
                throw new InvalidOperationException($"An article '{article.Slug}' already exists on this site.");
            }

            await articles.AddAsync(article, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return KnowledgeBaseMapping.ToModel(article);
        }
    }

    [Requires(KnowledgeBasePermissions.View)]
    public sealed record ListArticlesQuery(Guid? CategoryId = null) : IQuery<IReadOnlyList<ArticleModel>>;

    /// <summary>A visitor reads the published articles; a holder of the edit key reads the drafts too.</summary>
    public sealed class ListArticlesQueryHandler(IArticleRepository articles, SiteState siteState, IAccessGuard accessGuard) : IQueryHandler<ListArticlesQuery, IReadOnlyList<ArticleModel>>
    {
        public async Task<IReadOnlyList<ArticleModel>> Handle(ListArticlesQuery request, CancellationToken ct)
        {
            var drafts = await accessGuard.IsAllowedAsync(null, KnowledgeBasePermissions.Edit, ct);
            return (await articles.ListForSiteAsync(KnowledgeBaseScope.Site(siteState), request.CategoryId, ct))
                .Where(article => drafts || article.Status == ArticleStatus.Published)
                .Select(KnowledgeBaseMapping.ToModel)
                .ToList();
        }
    }

    [Requires(KnowledgeBasePermissions.View)]
    public sealed record GetArticleQuery(string Slug) : IQuery<ArticleModel?>;

    /// <summary>A draft does not exist for a visitor: it reads as not found, never as forbidden.</summary>
    public sealed class GetArticleQueryHandler(IArticleRepository articles, SiteState siteState, IAccessGuard accessGuard) : IQueryHandler<GetArticleQuery, ArticleModel?>
    {
        public async Task<ArticleModel?> Handle(GetArticleQuery request, CancellationToken ct)
        {
            var article = await articles.FindBySlugAsync(KnowledgeBaseScope.Site(siteState), request.Slug, ct);
            if (article is null || (article.Status != ArticleStatus.Published && !await accessGuard.IsAllowedAsync(null, KnowledgeBasePermissions.Edit, ct)))
            {
                return null;
            }

            return KnowledgeBaseMapping.ToModel(article);
        }
    }

    [Requires(KnowledgeBasePermissions.Edit)]
    public sealed record EditArticleCommand(Guid ArticleId, string Title, string Body, Guid? CategoryId) : ICommand<ArticleModel>;

    public sealed class EditArticleCommandHandler(IArticleRepository articles, ICategoryRepository categories, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock)
        : ICommandHandler<EditArticleCommand, ArticleModel>
    {
        public async Task<ArticleModel> Handle(EditArticleCommand request, CancellationToken ct)
        {
            var siteId = KnowledgeBaseScope.Site(siteState);
            var article = await KnowledgeBaseScope.SitesArticleAsync(articles, siteId, request.ArticleId, ct);
            if (request.CategoryId is { } categoryId && (await categories.GetByIdAsync(categoryId, ct))?.SiteId != siteId)
            {
                throw new InvalidOperationException($"Category '{categoryId}' is not this site's.");
            }

            // A read, then an update of THAT instance: the module store hands out non-tracking repositories (D79).
            article.Edit(request.Title, request.Body, request.CategoryId, clock.GetUtcNow().UtcDateTime);
            await articles.UpdateAsync(article, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return KnowledgeBaseMapping.ToModel(article);
        }
    }

    /// <summary>
    /// Publishes a draft - the exemplar's showcase of what a write can take part in, in this order:
    /// <list type="number">
    ///   <item>the site's QUOTA is asked first (D102, <see cref="IQuotaInspector"/>): a publish past the limit is refused
    ///     before anything changes; no quota defined is permissive;</item>
    ///   <item>the aggregate publishes and the unit of work saves - which dispatches its DOMAIN EVENT in this scope (D40);</item>
    ///   <item>the publish is METERED (<see cref="IUsageRecorder"/>), idempotent per article (the article's id is the
    ///     operation id);</item>
    ///   <item>the site's operators - its Administrators and the Host Users - are NOTIFIED through the host's dispatcher and
    ///     the module's contributed template (D100), by user id;</item>
    ///   <item>an INTEGRATION EVENT goes out on the host's bus when messaging is on.</item>
    /// </list>
    /// The publish is committed at step 2, so a meter, a notification or a bus that fails afterwards is logged and does not
    /// undo it - the producer's decision D102 leaves to it.
    /// </summary>
    [Requires(KnowledgeBasePermissions.Publish)]
    public sealed record PublishArticleCommand(Guid ArticleId) : ICommand<ArticleModel>;

    public sealed class PublishArticleCommandHandler(
        IArticleRepository articles,
        IUnitOfWork unitOfWork,
        SiteState siteState,
        TimeProvider clock,
        IQuotaInspector quotas,
        IUsageRecorder usage,
        IUserRoleRepository userRoles,
        ILogger<PublishArticleCommandHandler> logger,
        IEnumerable<IEventBus> bus,
        INotificationDispatcher? dispatcher = null) : ICommandHandler<PublishArticleCommand, ArticleModel>
    {
        public async Task<ArticleModel> Handle(PublishArticleCommand request, CancellationToken ct)
        {
            var siteId = KnowledgeBaseScope.Site(siteState);
            var article = await KnowledgeBaseScope.SitesArticleAsync(articles, siteId, request.ArticleId, ct);
            if (article.Status == ArticleStatus.Published)
            {
                throw new InvalidOperationException("The article is already published.");
            }

            // 1. The quota, before anything changes.
            var decision = await quotas.EvaluateAsync(KnowledgeBaseMetrics.ArticlesPublished, 1, ct);
            if (!decision.Allowed)
            {
                throw new KnowledgeBaseQuotaExceededException($"This site's quota of published articles is reached ({decision.Consumed} of {decision.Limit}).");
            }

            // 2. The publish, committed; the domain event is dispatched in this scope.
            article.Publish(clock.GetUtcNow().UtcDateTime);
            await articles.UpdateAsync(article, ct);
            await unitOfWork.SaveChangesAsync(ct);

            // 3. The meter - idempotent per article.
            try
            {
                await usage.RecordAsync(new UsageEvent(KnowledgeBaseMetrics.ArticlesPublished, 1, article.Id, KnowledgeBaseMetrics.Source), ct);
            }
            catch (MeteringUnavailableException ex)
            {
                logger.LogWarning(ex, "Knowledge base: the publish of {ArticleId} was not metered.", article.Id);
            }

            // 4. The site's administrators, told through the contributed template.
            await NotifyAsync(siteId, article, ct);

            // 5. Other contexts, told over the bus when messaging is on.
            if (bus.FirstOrDefault() is { } eventBus)
            {
                await eventBus.PublishAsync(new ArticlePublishedIntegrationEvent(article.Id, siteId, article.Title.Value, article.Slug), ct);
            }

            return KnowledgeBaseMapping.ToModel(article);
        }

        private async Task NotifyAsync(int siteId, Article article, CancellationToken ct)
        {
            // The site's OPERATORS: its Administrators and the Host Users, who administer every site. A fresh install's site has
            // no Administrators member - its one user is the host - so the Administrators alone would be nobody (measured, BL-177).
            var administrators = (await userRoles.GetUserRolesAsync(RoleNames.Admin, siteId))
                .Concat(await userRoles.GetUserRolesAsync(RoleNames.Host, siteId))
                .Select(role => role.UserId).Distinct().OrderBy(id => id).ToList();
            if (dispatcher is null || administrators.Count == 0)
            {
                return;
            }

            try
            {
                await dispatcher.DispatchAsync(new NotificationDispatchRequest(
                    KnowledgeBaseNotifications.ArticlePublishedTemplateKey,
                    administrators.Select(id => new DispatchRecipient(NotificationRecipientKind.User, id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(),
                    new[] { NotificationChannelKind.InApp },
                    new[] { new DispatchParameter("title", article.Title.Value), new DispatchParameter("slug", article.Slug) },
                    NotificationSeverity.Information,
                    KnowledgeBaseNotifications.Topic), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Knowledge base: the administrators of site {SiteId} were not told of {ArticleId}.", siteId, article.Id);
            }
        }
    }

    /// <summary>The request's site and tenant - a request with neither is refused rather than written to a guess.</summary>
    internal static class KnowledgeBaseScope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0 ? siteId : throw new InvalidOperationException("The knowledge base belongs to the request's site; none is resolved.");

        public static (Stomme.Tenancy.Domain.TenantId TenantId, int SiteId) Resolve(ITenantContext tenantContext, SiteState siteState) =>
            (tenantContext.CurrentTenantId ?? throw new InvalidOperationException("The knowledge base belongs to a tenant; none is resolved."), Site(siteState));

        /// <summary>The site's article with this id; another site's id is not found, as an unknown one is.</summary>
        public static async Task<Article> SitesArticleAsync(IArticleRepository articles, int siteId, Guid articleId, CancellationToken ct) =>
            await articles.GetByIdAsync(articleId, ct) is { } article && article.SiteId == siteId
                ? article
                : throw new KeyNotFoundException($"Article '{articleId}' was not found.");
    }

    internal static class KnowledgeBaseMapping
    {
        public static CategoryModel ToModel(Category category) => new(category.Id, category.Name, category.Slug, category.CreatedUtc);

        public static ArticleModel ToModel(Article article) =>
            new(article.Id, article.CategoryId, article.Title.Value, article.Slug, article.Body, article.Status, article.AuthorUserId, article.CreatedUtc, article.PublishedUtc);
    }
}
