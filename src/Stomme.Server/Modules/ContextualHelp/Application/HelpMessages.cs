#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Mediator;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.ContextualHelp.Application
{
    /// <summary>
    /// The gated surface. Every request carries <c>[Requires]</c>, and none of them names a site, a
    /// tenant or a user - all three are resolved server-side, so there is no parameter to forge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three keys, split by what a caller can DO, not by what they can see</b>: <c>help:view</c> reads published help
    /// (Everyone - a reader of a public page may have no account), <c>help:author</c> writes, publishes and previews,
    /// <c>help:manage</c> lists drafts, deletes and resets to seed. The uniform <c>Declare</c> would
    /// have seeded <c>help:manage</c> to All Users, which admits an ANONYMOUS caller - the per-key declaration is why an
    /// anonymous principal holds <c>help:view</c> and nothing else.
    /// </para>
    /// <para>
    /// <b>The seed path is not here.</b> Contributed topics reach a site through <see cref="HelpTopicReconciler"/> on
    /// the boot path, which is not a mediator message and so cannot be sent by anyone.
    /// </para>
    /// </remarks>
    internal static class HelpWire
    {
        /// <summary>The caller's site, or a refusal. A request that cannot tell which site it is for must not guess.</summary>
        public static int RequireSiteId(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0
                ? siteId
                : throw new InvalidOperationException("The help API requires a resolved site.");

        /// <summary>The tenant the scope resolved - the SAME answer the provider's tenant filter and the id derivation use.</summary>
        public static TenantId CurrentTenant(ITenantContext tenantContext) => tenantContext.CurrentTenantId ?? TenantId.Default;

        public static HelpTopicEditView ToEditView(HelpTopicAggregate topic, HelpTopicContributorRegistry? registry) => new(
            topic.Id, topic.Anchor, topic.Locale, topic.Title, topic.Markdown, topic.IsPublished, topic.IsSeeded,
            string.IsNullOrEmpty(topic.SeedContributorId) ? null : topic.SeedContributorId,
            topic.ContentVersion,
            registry is not null && registry.TryGet(topic.Anchor, topic.Locale, out _));

        public static IReadOnlyList<HelpTopicSummary> Summaries(IEnumerable<HelpTopicAggregate> topics) =>
            topics
                .OrderBy(topic => topic.Anchor, StringComparer.Ordinal)
                .ThenBy(topic => topic.Locale, StringComparer.Ordinal)
                .Select(HelpTopicProvider.ToSummary)
                .ToList();
    }

    // ---- help:view ---------------------------------------------------------------------------------------------------

    /// <summary>The published topic for an anchor on this site, through the locale chain - or null. Wire input: never throws on it.</summary>
    [Requires(HelpPermissions.View)]
    public sealed record ResolveHelpTopicQuery(string? Anchor, string? Locale) : IQuery<HelpTopicView?>;

    public sealed class ResolveHelpTopicQueryHandler(IHelpTopicProvider provider) : IQueryHandler<ResolveHelpTopicQuery, HelpTopicView?>
    {
        public Task<HelpTopicView?> Handle(ResolveHelpTopicQuery request, CancellationToken ct) =>
            provider.GetAsync(request.Anchor ?? string.Empty, request.Locale, ct);
    }

    /// <summary>Published topics of this site matching a text - drafts never, by construction of the provider.</summary>
    [Requires(HelpPermissions.View)]
    public sealed record ListPublishedHelpTopicsQuery(string? Query) : IQuery<IReadOnlyList<HelpTopicSummary>>;

    public sealed class ListPublishedHelpTopicsQueryHandler(IHelpTopicProvider provider)
        : IQueryHandler<ListPublishedHelpTopicsQuery, IReadOnlyList<HelpTopicSummary>>
    {
        public Task<IReadOnlyList<HelpTopicSummary>> Handle(ListPublishedHelpTopicsQuery request, CancellationToken ct) =>
            provider.SearchAsync(request.Query ?? string.Empty, ct);
    }

    // ---- help:author -------------------------------------------------------------------------------------------------

    /// <summary>One topic of this site with its Markdown, for the editor. Another site's id answers exactly like no id.</summary>
    [Requires(HelpPermissions.Author)]
    public sealed record GetHelpTopicForEditQuery(Guid Id) : IQuery<HelpTopicEditView?>;

    public sealed class GetHelpTopicForEditQueryHandler(IHelpTopicRepository topics, HelpTopicContributorRegistry registry, SiteState siteState)
        : IQueryHandler<GetHelpTopicForEditQuery, HelpTopicEditView?>
    {
        public async Task<HelpTopicEditView?> Handle(GetHelpTopicForEditQuery request, CancellationToken ct)
        {
            HelpWire.RequireSiteId(siteState);
            var topic = await topics.GetByIdForSiteAsync(request.Id, ct);
            return topic is null ? null : HelpWire.ToEditView(topic, registry);
        }
    }

    /// <summary>
    /// Writes a topic. A new (anchor, locale) becomes a DRAFT; an existing one - a contributed topic included - is edited,
    /// and the edit makes it the SITE's, so no contributor updates it again.
    /// </summary>
    [Requires(HelpPermissions.Author)]
    public sealed record SaveHelpTopicCommand(Guid? Id, string Anchor, string Locale, string Title, string Markdown) : ICommand<Guid>;

    public sealed class SaveHelpTopicCommandHandler(IHelpTopicRepository topics, SiteState siteState, ITenantContext tenantContext)
        : ICommandHandler<SaveHelpTopicCommand, Guid>
    {
        public async Task<Guid> Handle(SaveHelpTopicCommand request, CancellationToken ct)
        {
            var siteId = HelpWire.RequireSiteId(siteState);
            var now = DateTime.UtcNow;

            // An edit by id: the anchor and locale are the row's KEY and do not move - the id is derived from them.
            var existing = request.Id is Guid id
                ? await topics.GetByIdForSiteAsync(id, ct) ?? throw new InvalidOperationException("No such help topic on this site.")
                : await topics.GetAsync(request.Anchor, request.Locale, ct);

            if (existing is not null)
            {
                existing.UpdateContent(request.Title, request.Markdown, now);
                await topics.UpdateAsync(existing, ct);
                return existing.Id;
            }

            // New: the domain validates (anchor, locale, title, body bounds) and throws ArgumentException, which the
            // controller answers as 400 - never a 500 on author input.
            var topic = HelpTopicAggregate.Author(HelpWire.CurrentTenant(tenantContext), siteId, request.Anchor, request.Locale, request.Title, request.Markdown, now);
            await topics.AddAsync(topic, ct);
            return topic.Id;
        }
    }

    /// <summary>Publishes or unpublishes a topic of this site. A draft is invisible to every read but the manage list.</summary>
    [Requires(HelpPermissions.Author)]
    public sealed record PublishHelpTopicCommand(Guid Id, bool Published) : ICommand<Unit>;

    public sealed class PublishHelpTopicCommandHandler(IHelpTopicRepository topics, SiteState siteState)
        : ICommandHandler<PublishHelpTopicCommand, Unit>
    {
        public async Task<Unit> Handle(PublishHelpTopicCommand request, CancellationToken ct)
        {
            HelpWire.RequireSiteId(siteState);
            var topic = await topics.GetByIdForSiteAsync(request.Id, ct) ?? throw new InvalidOperationException("No such help topic on this site.");
            if (request.Published)
            {
                topic.Publish(DateTime.UtcNow);
            }
            else
            {
                topic.Unpublish(DateTime.UtcNow);
            }

            await topics.UpdateAsync(topic, ct);
            return Unit.Value;
        }
    }

    /// <summary>
    /// Renders UNSAVED Markdown through the reader's pipeline and allowlist. Server-side, so the client never references
    /// Markdig, and author-gated, so it is not an anonymous rendering service.
    /// </summary>
    [Requires(HelpPermissions.Author)]
    public sealed record PreviewHelpMarkdownQuery(string? Markdown) : IQuery<string>;

    public sealed class PreviewHelpMarkdownQueryHandler(HelpMarkdownRenderer renderer) : IQueryHandler<PreviewHelpMarkdownQuery, string>
    {
        public Task<string> Handle(PreviewHelpMarkdownQuery request, CancellationToken ct) =>
            Task.FromResult(renderer.Render(request.Markdown));
    }

    // ---- help:manage -------------------------------------------------------------------------------------------------

    /// <summary>Every topic of this site, DRAFTS included - the one read that sees them.</summary>
    [Requires(HelpPermissions.Manage)]
    public sealed record ListAllHelpTopicsQuery : IQuery<IReadOnlyList<HelpTopicSummary>>;

    public sealed class ListAllHelpTopicsQueryHandler(IHelpTopicRepository topics, SiteState siteState)
        : IQueryHandler<ListAllHelpTopicsQuery, IReadOnlyList<HelpTopicSummary>>
    {
        public async Task<IReadOnlyList<HelpTopicSummary>> Handle(ListAllHelpTopicsQuery request, CancellationToken ct)
        {
            HelpWire.RequireSiteId(siteState);
            return HelpWire.Summaries(await topics.ListAsync(ct));
        }
    }

    /// <summary>What the contributor registry rejected - nothing was served for it, so this is how an operator finds it.</summary>
    [Requires(HelpPermissions.Manage)]
    public sealed record ListHelpContributionConflictsQuery : IQuery<IReadOnlyList<HelpConflictView>>;

    public sealed class ListHelpContributionConflictsQueryHandler(HelpTopicContributorRegistry registry)
        : IQueryHandler<ListHelpContributionConflictsQuery, IReadOnlyList<HelpConflictView>>
    {
        public Task<IReadOnlyList<HelpConflictView>> Handle(ListHelpContributionConflictsQuery request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HelpConflictView>>(registry.Conflicts
                .Select(conflict => new HelpConflictView(conflict.Kind, conflict.Name, conflict.Contributors, conflict.Reason))
                .ToList());
    }

    /// <summary>
    /// Deletes a topic of this site. A CONTRIBUTED topic comes back on the next boot - the reconciler creates what is
    /// absent - so the way to hide one for good is to unpublish it; the admin page says so.
    /// </summary>
    [Requires(HelpPermissions.Manage)]
    public sealed record DeleteHelpTopicCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteHelpTopicCommandHandler(IHelpTopicRepository topics, SiteState siteState)
        : ICommandHandler<DeleteHelpTopicCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteHelpTopicCommand request, CancellationToken ct)
        {
            HelpWire.RequireSiteId(siteState);
            var topic = await topics.GetByIdForSiteAsync(request.Id, ct) ?? throw new InvalidOperationException("No such help topic on this site.");
            await topics.DeleteAsync(topic, ct);
            return Unit.Value;
        }
    }

    /// <summary>
    /// Hands an edited topic back to the contributor that ships it now - the ONLY way ownership changes.
    /// Refused when no contributor ships its anchor and locale.
    /// </summary>
    [Requires(HelpPermissions.Manage)]
    public sealed record ResetHelpTopicToSeedCommand(Guid Id) : ICommand<Unit>;

    public sealed class ResetHelpTopicToSeedCommandHandler(IHelpTopicRepository topics, HelpTopicContributorRegistry registry, SiteState siteState)
        : ICommandHandler<ResetHelpTopicToSeedCommand, Unit>
    {
        public async Task<Unit> Handle(ResetHelpTopicToSeedCommand request, CancellationToken ct)
        {
            HelpWire.RequireSiteId(siteState);
            var topic = await topics.GetByIdForSiteAsync(request.Id, ct) ?? throw new InvalidOperationException("No such help topic on this site.");
            if (!registry.TryGet(topic.Anchor, topic.Locale, out var contributed))
            {
                throw new InvalidOperationException("No contributor ships this topic, so there is no seed to reset it to.");
            }

            topic.ResetToSeed(contributed.Contribution, contributed.ContributorId, DateTime.UtcNow);
            await topics.UpdateAsync(topic, ct);
            return Unit.Value;
        }
    }
}
