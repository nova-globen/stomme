using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Domain
{
    public sealed record CategoryCreatedEvent(Guid CategoryId, int SiteId, string Name) : DomainEvent;

    public sealed record ArticleDraftedEvent(Guid ArticleId, int SiteId, string Title, int AuthorUserId) : DomainEvent;

    public sealed record ArticlePublishedEvent(Guid ArticleId, int SiteId, string Title, string Slug, int AuthorUserId) : DomainEvent;

    public sealed record ArticleEditedEvent(Guid ArticleId, int SiteId, string Title) : DomainEvent;

    /// <summary>
    /// A category of articles, per SITE of its tenant.
    /// </summary>
    /// <remarks>
    /// <b>Why <see cref="ITenantOwned"/>:</b> the host keeps ONE module store per installation and a site id repeats
    /// across tenants, so a storage-blind aggregate is tenant-owned by contract: the store stamps and filters by
    /// the tenant, and the handler that creates the aggregate stamps it from the ambient tenant. A module whose aggregate
    /// is not tenant-owned is NAMED at the host's startup.
    /// </remarks>
    public sealed class Category : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxNameLength = 80;

        // Materialisation constructor - every store builds the aggregate through it. Not for application use.
        private Category()
        {
            TenantId = null!;
            Name = string.Empty;
            Slug = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Name { get; private set; }

        /// <summary>Normalised (see <see cref="Domain.Slug"/>); unique per site.</summary>
        public string Slug { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public static Category Create(Guid id, TenantId tenantId, int siteId, string name, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A category belongs to a site.");
            }

            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
            {
                throw new ArgumentException($"A category name is 1 to {MaxNameLength} characters.", nameof(name));
            }

            var category = new Category
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Name = trimmed,
                Slug = Domain.Slug.Create(trimmed),
                CreatedUtc = nowUtc,
            };
            category.RaiseDomainEvent(new CategoryCreatedEvent(category.Id, category.SiteId, category.Name));
            return category;
        }
    }

    /// <summary>
    /// An article: drafted by a site member, then published. Per SITE of its tenant (see <see cref="Category"/>).
    /// </summary>
    public sealed class Article : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxBodyLength = 50_000;

        // Materialisation constructor - every store builds the aggregate through it. Not for application use.
        private Article()
        {
            TenantId = null!;
            Title = null!;
            Slug = string.Empty;
            Body = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public Guid? CategoryId { get; private set; }

        public ArticleTitle Title { get; private set; }

        /// <summary>Normalised (see <see cref="Domain.Slug"/>); unique per site.</summary>
        public string Slug { get; private set; }

        public string Body { get; private set; }

        public ArticleStatus Status { get; private set; }

        /// <summary>The site user who drafted it - <see cref="NoAuthor"/> once that user's personal data was erased.</summary>
        public int AuthorUserId { get; private set; }

        /// <summary>The author of an article whose author was erased: the article stays the site's, attributed to nobody.</summary>
        public const int NoAuthor = 0;

        public DateTime CreatedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public DateTime? PublishedUtc { get; private set; }

        public static Article Draft(Guid id, TenantId tenantId, int siteId, Guid? categoryId, string title, string? slug, string body, int authorUserId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An article belongs to a site.");
            }

            if (authorUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorUserId), "An article has an author.");
            }

            var articleTitle = ArticleTitle.Create(title);
            var article = new Article
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                CategoryId = categoryId,
                Title = articleTitle,
                Slug = string.IsNullOrWhiteSpace(slug) ? Domain.Slug.Create(articleTitle.Value) : Domain.Slug.Create(slug),
                Body = RequireBody(body),
                Status = ArticleStatus.Draft,
                AuthorUserId = authorUserId,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            };
            article.RaiseDomainEvent(new ArticleDraftedEvent(article.Id, article.SiteId, article.Title.Value, article.AuthorUserId));
            return article;
        }

        /// <summary>
        /// A demo article the host seeds on a new site (<c>DemoContent:Enabled</c>, increment 6): PUBLISHED at once, attributed
        /// to nobody, and raising NO event - a seed is not a publish, so nothing is metered, counted against the site's quota
        /// or announced.
        /// </summary>
        public static Article Seed(Guid id, TenantId tenantId, int siteId, Guid? categoryId, string title, string slug, string body, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An article belongs to a site.");
            }

            return new Article
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                CategoryId = categoryId,
                Title = ArticleTitle.Create(title),
                Slug = Domain.Slug.Create(slug),
                Body = RequireBody(body),
                Status = ArticleStatus.Published,
                AuthorUserId = NoAuthor,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                PublishedUtc = nowUtc,
            };
        }

        /// <summary>A new title, body and category; the slug stays, so the article's address never moves.</summary>
        public void Edit(string title, string body, Guid? categoryId, DateTime nowUtc)
        {
            Title = ArticleTitle.Create(title);
            Body = RequireBody(body);
            CategoryId = categoryId;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new ArticleEditedEvent(Id, SiteId, Title.Value));
        }

        /// <summary>Publishes a draft; publishing twice is refused.</summary>
        public void Publish(DateTime nowUtc)
        {
            if (Status == ArticleStatus.Published)
            {
                throw new InvalidOperationException("The article is already published.");
            }

            Status = ArticleStatus.Published;
            PublishedUtc = nowUtc;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new ArticlePublishedEvent(Id, SiteId, Title.Value, Slug, AuthorUserId));
        }

        /// <summary>
        /// Anonymises a PUBLISHED article whose author asked to be erased: the article is the site's content and
        /// stays, attributed to nobody. A draft is its author's unpublished work and is deleted instead - refused here.
        /// </summary>
        public void EraseAuthor()
        {
            if (Status != ArticleStatus.Published)
            {
                throw new InvalidOperationException("A draft is deleted with its author's data, not anonymised.");
            }

            AuthorUserId = NoAuthor;
        }

        private static string RequireBody(string body)
        {
            var trimmed = (body ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxBodyLength)
            {
                throw new ArgumentException($"An article body is 1 to {MaxBodyLength} characters.", nameof(body));
            }

            return trimmed;
        }
    }
}
