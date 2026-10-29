using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging;
using NovaGloben.Module.KnowledgeBase.Domain;

namespace NovaGloben.Module.KnowledgeBase.Application
{
    /// <summary>
    /// What the knowledge base tells OTHER contexts when an article is published - an integration event on the host's bus,
    /// published after the commit. The bus is opt-in (<c>Messaging:Enabled</c>): with it off no <c>IEventBus</c> is
    /// registered and nothing is sent. Carries only what a subscriber needs to link to the article.
    /// </summary>
    public sealed record ArticlePublishedIntegrationEvent(Guid ArticleId, int SiteId, string Title, string Slug) : IntegrationEvent;

    /// <summary>
    /// The IN-PROCESS reaction to the domain event, dispatched by the host's unit of work in the same scope when the
    /// publish is saved. A domain event handler STAGES work and never manages the unit of work; this one only logs,
    /// which is all a same-transaction reaction of this module needs.
    /// </summary>
    public sealed class ArticlePublishedDomainEventHandler(ILogger<ArticlePublishedDomainEventHandler> logger) : IDomainEventHandler<ArticlePublishedEvent>
    {
        public Task HandleAsync(ArticlePublishedEvent domainEvent, CancellationToken ct = default)
        {
            logger.LogInformation("Knowledge base: article {ArticleId} ('{Slug}') published on site {SiteId}.", domainEvent.ArticleId, domainEvent.Slug, domainEvent.SiteId);
            return Task.CompletedTask;
        }
    }

    /// <summary>The metric a publish is counted under - the key a site's quota names to cap its published articles.</summary>
    public static class KnowledgeBaseMetrics
    {
        public const string ArticlesPublished = "knowledge-base.articles-published";

        public const string Source = "knowledge-base";
    }

    /// <summary>
    /// The notification the module sends when an article is published: a TEMPLATE the module contributes to every
    /// site through the host's per-site template reconciliation, and the parameters the publish fills it with. The
    /// recipients are the site's administrators by user id - never an address from configuration.
    /// </summary>
    public static class KnowledgeBaseNotifications
    {
        public const string ArticlePublishedTemplateKey = "knowledge-base.article-published";

        public const string Topic = "knowledge-base";

        public static IReadOnlyList<NotificationTemplateAggregate> Templates(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, ArticlePublishedTemplateKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Published: {{ title }}",
                            "The article \"{{ title }}\" was published in the knowledge base at /knowledge-base/!/article/{{ slug }}.")),
                    },
                    placeholders: new PlaceholderSchema(new[] { "title", "slug" })),
            };
        }
    }

    /// <summary>The module's contribution to the host's per-site template reconciliation (registered by its startup).</summary>
    public sealed class KnowledgeBaseTemplateSource : INotificationTemplateSource
    {
        public string Name => "Knowledge base templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) => KnowledgeBaseNotifications.Templates(tenantId, siteId);
    }

    /// <summary>A publish the site's quota refuses - the API answers it 429.</summary>
    public sealed class KnowledgeBaseQuotaExceededException(string message) : InvalidOperationException(message);
}
