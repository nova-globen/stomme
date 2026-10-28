#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Export;
using Stomme.Portability;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Models;
using NovaGloben.Module.KnowledgeBase.Persistence;

namespace NovaGloben.Module.KnowledgeBase.DataDuties
{
    /// <summary>
    /// The author's articles in their personal data package (D107, <c>api/Portability/subject</c> and <c>mine</c>): every
    /// article the subject drafted on the site, drafts included - it is their data, whoever else may read it.
    /// </summary>
    public sealed class KnowledgeBaseSubjectDataContributor(IArticleRepository articles) : ISubjectDataContributor
    {
        public const string DataType = "knowledge-base-articles";

        public const string EntityType = "knowledge-base-article";

        public string SubjectDataType => DataType;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var siteId = scope.SiteId;
            var userId = scope.UserId;
            return (await articles.FindAsync(article => article.SiteId == siteId && article.AuthorUserId == userId, cancellationToken))
                .OrderBy(article => article.CreatedUtc)
                .Select(article => new PortableEntity(
                    new PortableEntityRef(EntityType, article.Id.ToString()),
                    article.Title.Value,
                    1,
                    JsonSerializer.Serialize(new
                    {
                        title = article.Title.Value,
                        slug = article.Slug,
                        status = article.Status.ToString(),
                        body = article.Body,
                        createdUtc = article.CreatedUtc,
                        publishedUtc = article.PublishedUtc,
                    })))
                .ToList();
        }
    }

    /// <summary>
    /// The author's articles when their personal data is erased (D112), declared per store so the host's receipt says what
    /// happened to each: a DRAFT is the author's unpublished work and is deleted; a PUBLISHED article is the site's content
    /// and stays, anonymised - attributed to nobody. Idempotent: a second run finds nothing left of the author's.
    /// </summary>
    public sealed class KnowledgeBaseErasureParticipant(IArticleRepository articles, IUnitOfWork unitOfWork, ILogger<KnowledgeBaseErasureParticipant> logger)
        : ISubjectErasureParticipant
    {
        public const string DraftsStore = "knowledge-base article drafts";

        public const string PublishedStore = "knowledge-base published articles";

        public string Name => "Knowledge base authorship";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete(DraftsStore),
            ErasureDeclaration.Anonymise(PublishedStore),
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            var siteId = subject.SiteId;
            var userId = subject.UserId;
            if (siteId <= 0 || userId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var deleted = 0;
            var anonymised = 0;
            foreach (var article in await articles.FindAsync(article => article.SiteId == siteId && article.AuthorUserId == userId, ct))
            {
                if (article.Status == ArticleStatus.Published)
                {
                    article.EraseAuthor();
                    await articles.UpdateAsync(article, ct);
                    anonymised++;
                }
                else
                {
                    await articles.DeleteAsync(article, ct);
                    deleted++;
                }
            }

            if (deleted + anonymised > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Erased user {UserId}'s authorship on site {SiteId}: {Deleted} draft(s) deleted, {Anonymised} published article(s) anonymised.", userId, siteId, deleted, anonymised);
            }

            return new[]
            {
                new ErasureOutcome(DraftsStore, ErasureHandling.Delete, deleted),
                new ErasureOutcome(PublishedStore, ErasureHandling.Anonymise, anonymised),
            };
        }
    }

    /// <summary>
    /// The site's published articles as a table for the host's data export (D108): CSV, JSON, XLSX or PDF, rendered by the
    /// export queue job, never in a request. A requester needs <c>knowledge-base:view</c> on top of the export key. A draft is
    /// not exported - an export is the site's knowledge base as its visitors read it. The source takes no arguments.
    /// </summary>
    public sealed class KnowledgeBaseArticlesExportSource(IArticleRepository articles, ICategoryRepository categories) : IExportSource
    {
        public const string SourceKey = "knowledge-base.articles";

        private static readonly ExportColumn[] ColumnSet =
        {
            new("Title", ExportValueKind.Text),
            new("Address", ExportValueKind.Text),
            new("Category", ExportValueKind.Text),
            new("PublishedUtc", ExportValueKind.DateTimeUtc),
            new("UpdatedUtc", ExportValueKind.DateTimeUtc),
            new("Words", ExportValueKind.Integer),
        };

        public string Key => SourceKey;

        public string RequiredPermission => KnowledgeBasePermissions.View;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) =>
            string.IsNullOrWhiteSpace(context?.ArgumentsJson) || context.ArgumentsJson.Trim() is "{}" or "null"
                ? null
                : "The knowledge base's export takes no arguments.";

        public async Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var byId = (await categories.ListForSiteAsync(context.SiteId, ct)).ToDictionary(category => category.Id, category => category.Name);
            var published = (await articles.ListForSiteAsync(context.SiteId, null, ct))
                .Where(article => article.Status == ArticleStatus.Published)
                .OrderBy(article => article.Title.Value, StringComparer.OrdinalIgnoreCase)
                .Select(article => new object?[]
                {
                    article.Title.Value,
                    article.Slug,
                    article.CategoryId is { } categoryId && byId.TryGetValue(categoryId, out var name) ? name : null,
                    article.PublishedUtc,
                    article.UpdatedUtc,
                    (long)article.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                })
                .ToList();
            return new Document(new Table("Articles", ColumnSet, published));
        }

        private sealed class Document(ITabularTable table) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                yield return table;
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Table(string name, IReadOnlyList<ExportColumn> columns, IReadOnlyList<object?[]> rows) : ITabularTable
        {
            public string Name => name;

            public IReadOnlyList<ExportColumn> Columns => columns;

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return row;
                }

                await Task.CompletedTask;
            }
        }
    }
}
