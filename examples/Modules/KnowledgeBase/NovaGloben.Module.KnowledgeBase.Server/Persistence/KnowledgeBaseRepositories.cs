using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using NovaGloben.Module.KnowledgeBase.Domain;

namespace NovaGloben.Module.KnowledgeBase.Persistence
{
    /// <summary>
    /// The articles' closed repository over the host's storage-blind store: the module never names a provider.
    /// Every read is a SITE's. A filter is <c>FindAsync</c> on non-string scalars (the site id, the category id) - the
    /// safe set every store answers alike - and a slug lookup compares a value normalised on both sides.
    /// </summary>
    public interface IArticleRepository : IRepository<Article, Guid>
    {
        Task<IReadOnlyList<Article>> ListForSiteAsync(int siteId, Guid? categoryId = null, CancellationToken ct = default);

        Task<Article?> FindBySlugAsync(int siteId, string slug, CancellationToken ct = default);
    }

    /// <summary>The categories' closed repository (see <see cref="IArticleRepository"/>).</summary>
    public interface ICategoryRepository : IRepository<Category, Guid>
    {
        Task<IReadOnlyList<Category>> ListForSiteAsync(int siteId, CancellationToken ct = default);

        Task<Category?> FindBySlugAsync(int siteId, string slug, CancellationToken ct = default);
    }

    public sealed class ArticleRepository(IRepository<Article, Guid> inner) : IArticleRepository
    {
        public Task<Article?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<Article>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(Article aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(Article aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(Article aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<Article>> FindAsync(Expression<Func<Article, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<Article>> ListForSiteAsync(int siteId, Guid? categoryId = null, CancellationToken ct = default)
        {
            var found = categoryId is { } category
                ? await inner.FindAsync(article => article.SiteId == siteId && article.CategoryId == category, ct)
                : await inner.FindAsync(article => article.SiteId == siteId, ct);
            return found.OrderByDescending(article => article.UpdatedUtc).ThenBy(article => article.Slug, StringComparer.Ordinal).ToList();
        }

        public async Task<Article?> FindBySlugAsync(int siteId, string slug, CancellationToken ct = default)
        {
            var normalised = Slug.From(slug);
            return (await inner.FindAsync(article => article.SiteId == siteId && article.Slug == normalised, ct)).SingleOrDefault();
        }
    }

    public sealed class CategoryRepository(IRepository<Category, Guid> inner) : ICategoryRepository
    {
        public Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(Category aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(Category aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(Category aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<Category>> FindAsync(Expression<Func<Category, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<Category>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(category => category.SiteId == siteId, ct)).OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public async Task<Category?> FindBySlugAsync(int siteId, string slug, CancellationToken ct = default)
        {
            var normalised = Slug.From(slug);
            return (await inner.FindAsync(category => category.SiteId == siteId && category.Slug == normalised, ct)).SingleOrDefault();
        }
    }
}
