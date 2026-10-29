#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Models;
using NovaGloben.Module.KnowledgeBase.Persistence;
using SharedKernelUnitOfWork = Stomme.SharedKernel.Domain.IUnitOfWork;

namespace NovaGloben.Module.KnowledgeBase.Operations
{
    /// <summary>The module's own settings, read from the host's configuration (the override file a deployed install writes).</summary>
    public static class KnowledgeBaseSettings
    {
        /// <summary>A draft untouched for this many days is STALE - the health probe's threshold. Default 30.</summary>
        public const string StaleDraftDaysKey = "KnowledgeBase:StaleDraftDays";

        public const int DefaultStaleDraftDays = 30;

        public static int StaleDraftDays(IConfiguration? configuration) =>
            int.TryParse(configuration?[StaleDraftDaysKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : DefaultStaleDraftDays;
    }

    /// <summary>
    /// The module's contribution to the host's health monitor: one SITE-scope probe - the site's stale drafts - and
    /// one metric, its draft count. The host runs the probe per site on its schedule (<c>Health:Enabled</c>, on by
    /// default) or on demand (<c>api/Health/run</c>), stores each verdict and shows it on its health page.
    /// </summary>
    /// <remarks>
    /// A stale draft is not an outage - the site serves every published article regardless - so the probe answers
    /// <see cref="HealthState.Degraded"/>, never Unhealthy: it is a signal to an editor, and the host's alerting (off by
    /// default) decides who hears about it. The data carries the count and the oldest draft's address, never a body.
    /// </remarks>
    public sealed class KnowledgeBaseHealthContributor(IArticleRepository articles, TimeProvider clock, int staleDraftDays) : IHealthContributor
    {
        public const string Id = "nova-globen.knowledge-base";

        public const string ProbeName = "knowledge-base.stale-drafts";

        public const string DraftsMetric = "knowledge-base.drafts";

        public string ContributorId => Id;

        public IReadOnlyList<IHealthProbe> Probes => new IHealthProbe[] { new StaleDraftsProbe(articles, clock, staleDraftDays) };

        public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = new[] { new HealthMetricDescriptor(DraftsMetric, "count", "The site's unpublished articles.") };

        public IHealthMetricSource? SourceFor(string metricName) => metricName == DraftsMetric ? new DraftCount(articles) : null;

        private sealed class StaleDraftsProbe(IArticleRepository articles, TimeProvider clock, int staleDraftDays) : IHealthProbe
        {
            public string Name => ProbeName;

            public string Dependency => "Knowledge base";

            public HealthProbeScope Scope => HealthProbeScope.Site;

            public async Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                var watch = Stopwatch.StartNew();
                var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-staleDraftDays);
                var drafts = await DraftsAsync(articles, context.SiteId, cancellationToken);
                var stale = drafts.Where(article => article.UpdatedUtc < cutoff).OrderBy(article => article.UpdatedUtc).ToList();
                var data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["drafts"] = drafts.Count.ToString(CultureInfo.InvariantCulture),
                    ["stale"] = stale.Count.ToString(CultureInfo.InvariantCulture),
                    ["staleAfterDays"] = staleDraftDays.ToString(CultureInfo.InvariantCulture),
                };
                if (stale.Count == 0)
                {
                    return new HealthProbeResult(Name, Dependency, HealthState.Healthy, $"{drafts.Count} draft(s), none untouched for {staleDraftDays} days.", watch.Elapsed, data);
                }

                data["oldest"] = stale[0].Slug;
                return new HealthProbeResult(Name, Dependency, HealthState.Degraded,
                    $"{stale.Count} of {drafts.Count} draft(s) untouched for {staleDraftDays} days or more; the oldest is '{stale[0].Slug}'.", watch.Elapsed, data);
            }
        }

        private sealed class DraftCount(IArticleRepository articles) : IHealthMetricSource
        {
            public HealthMetricDescriptor Descriptor { get; } = new(DraftsMetric, "count", "The site's unpublished articles.");

            public async Task<double?> ReadAsync(HealthProbeContext context, CancellationToken cancellationToken = default) =>
                (await DraftsAsync(articles, context.SiteId, cancellationToken)).Count;
        }

        private static async Task<IReadOnlyList<Article>> DraftsAsync(IArticleRepository articles, int siteId, CancellationToken ct) =>
            (await articles.FindAsync(article => article.SiteId == siteId && article.Status == ArticleStatus.Draft, ct)).ToList();
    }

    /// <summary>
    /// Demo articles for a new site, behind the host's default-off demo switch (<c>DemoContent:Enabled</c>): a category and
    /// three published articles, so a visitor meets a knowledge base with something in it. The host runs it on its boot
    /// path for every site under that site's tenant.
    /// </summary>
    /// <remarks>
    /// <b>Additive, once per site</b> (the host's <see cref="DemoContentStamp"/>): what an operator later edits or deletes is
    /// never put back on a later boot, and an article whose address is already taken is left alone. A demo article is
    /// SEEDED, not published - <see cref="Article.Seed"/> raises no event, so nothing is metered, counted against the site's
    /// quota or announced.
    /// </remarks>
    public sealed class KnowledgeBaseDemoContentReconciler(
        ICategoryRepository categories,
        IArticleRepository articles,
        SharedKernelUnitOfWork unitOfWork,
        DemoContentStamp stamp,
        TimeProvider clock,
        ILogger<KnowledgeBaseDemoContentReconciler> logger) : ISiteContentReconciler
    {
        public const string Module = "KnowledgeBase";

        public const string Version = "1";

        public const string DemoCategory = "Getting started";

        public static readonly IReadOnlyList<(string Title, string Slug, string Body)> Demo = new[]
        {
            ("Welcome to the knowledge base", "welcome-to-the-knowledge-base",
                "This knowledge base answers the questions visitors ask most. Open an article from the list, or choose a category to see only its articles."),
            ("Finding an article", "finding-an-article",
                "Every article has its own address, so a link to it can be shared. The site's search finds articles by the words in their title and body."),
            ("Suggesting a change", "suggesting-a-change",
                "Editors keep these articles current. If an answer is out of date, tell the site's administrators and an editor will update it."),
        };

        public string Name => "Demo knowledge-base articles";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            if (await stamp.IsAppliedAsync(context.TenantId, siteId, Module, Version, ct))
            {
                return;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var category = await categories.FindBySlugAsync(siteId, Slug.From(DemoCategory), ct);
            if (category is null)
            {
                category = Category.Create(Guid.NewGuid(), context.TenantId, siteId, DemoCategory, now);
                await categories.AddAsync(category, ct);
            }

            var created = new List<string>();
            foreach (var (title, slug, body) in Demo)
            {
                if (await articles.FindBySlugAsync(siteId, slug, ct) is not null)
                {
                    continue; // the address is an operator's article now; the demo does not take it
                }

                await articles.AddAsync(Article.Seed(Guid.NewGuid(), context.TenantId, siteId, category.Id, title, slug, body, now), ct);
                created.Add(slug);
            }

            await unitOfWork.SaveChangesAsync(ct);
            await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
            logger.LogInformation("Demo knowledge-base articles on site {SiteId}: created {Created}.", siteId, created.Count == 0 ? "none (all present)" : string.Join(", ", created));
        }
    }
}
