#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Landing;
using Stomme.Landing.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Landing.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Landing.Application
{
    // The Landing module's requests (BL-98). Each carries [Requires] with one of the module's keys (D99), and works through the
    // REQUEST's site: another site's landing page does not exist for it.

    /// <summary>The site's landing page as the caller may see it: published to every viewer, a draft to an administrator only.</summary>
    [Requires(LandingPermissions.View)]
    public sealed record GetLandingPageQuery : IQuery<LandingPageView?>;

    public sealed class GetLandingPageQueryHandler(ILandingPageRepository pages, SiteState siteState, IAccessGuard accessGuard)
        : IQueryHandler<GetLandingPageQuery, LandingPageView?>
    {
        public async Task<LandingPageView?> Handle(GetLandingPageQuery request, CancellationToken ct)
        {
            var page = await pages.FindForSiteAsync(LandingScope.Site(siteState), ct);
            if (page is null || page.Source == LandingSource.None)
            {
                return null;
            }

            var manager = await accessGuard.IsAllowedAsync(null, LandingPermissions.Manage, ct);
            if (page.Status != LandingStatus.Published && !manager)
            {
                return null; // a draft does not exist for a visitor - not forbidden, just not there
            }

            if (page.Source == LandingSource.Dashboard && !manager && !await accessGuard.IsAllowedAsync(null, DashboardPermissions.View, ct))
            {
                return null; // the board would show this viewer nothing: dashboards:view is the Dashboards module's to grant
            }

            return LandingScope.ToView(page);
        }
    }

    /// <summary>The predefined template's starting content, for an administrator filling it in.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record GetLandingTemplateQuery : IQuery<PredefinedLandingContent>;

    public sealed class GetLandingTemplateQueryHandler : IQueryHandler<GetLandingTemplateQuery, PredefinedLandingContent>
    {
        public Task<PredefinedLandingContent> Handle(GetLandingTemplateQuery request, CancellationToken ct) => Task.FromResult(LandingTemplate.Default);
    }

    /// <summary>Makes (or remakes) the site's landing page from the predefined template. An edit to a published page is live.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record ConfigurePredefinedLandingCommand(PredefinedLandingContent Content) : ICommand<LandingPageView>;

    public sealed class ConfigurePredefinedLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock)
        : ICommandHandler<ConfigurePredefinedLandingCommand, LandingPageView>
    {
        public async Task<LandingPageView> Handle(ConfigurePredefinedLandingCommand request, CancellationToken ct)
        {
            var siteId = LandingScope.Site(siteState);
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await pages.FindForSiteAsync(siteId, ct);
            var page = existing ?? LandingPageAggregate.Create(LandingScope.Tenant(tenantContext), siteId, now);
            page.ConfigurePredefined(request.Content, now);
            if (existing is null)
            {
                await pages.AddAsync(page, ct);
            }
            else
            {
                await pages.UpdateAsync(page, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return LandingScope.ToView(page);
        }
    }

    /// <summary>
    /// Makes (or remakes) the site's landing page as one of its dashboards. The dashboard is read through the Dashboards
    /// module's own request, so it must be the request's site's, published, and readable by the administrator making the page
    /// (<c>dashboards:view</c>, decided again by that request).
    /// </summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record ConfigureDashboardLandingCommand(Guid DashboardId) : ICommand<LandingPageView>;

    public sealed class ConfigureDashboardLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock, IMediator mediator)
        : ICommandHandler<ConfigureDashboardLandingCommand, LandingPageView>
    {
        public async Task<LandingPageView> Handle(ConfigureDashboardLandingCommand request, CancellationToken ct)
        {
            if (request.DashboardId == Guid.Empty)
            {
                throw new ArgumentException("A dashboard landing page needs a dashboard.", nameof(request));
            }

            var dashboard = await mediator.Send(new GetDashboardQuery(request.DashboardId), ct)
                ?? throw new KeyNotFoundException("This site has no such dashboard.");
            if (dashboard.Status != DashboardStatus.Published)
            {
                throw new InvalidOperationException($"The dashboard '{dashboard.Name}' is a draft; publish it before a landing page shows it.");
            }

            var siteId = LandingScope.Site(siteState);
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await pages.FindForSiteAsync(siteId, ct);
            var page = existing ?? LandingPageAggregate.Create(LandingScope.Tenant(tenantContext), siteId, now);
            page.ConfigureDashboard(dashboard.Id, now);
            if (existing is null)
            {
                await pages.AddAsync(page, ct);
            }
            else
            {
                await pages.UpdateAsync(page, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return LandingScope.ToView(page);
        }
    }

    /// <summary>Shows the site's landing page to its visitors.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record PublishLandingCommand : ICommand<LandingPageView>;

    public sealed class PublishLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock)
        : ICommandHandler<PublishLandingCommand, LandingPageView>
    {
        public async Task<LandingPageView> Handle(PublishLandingCommand request, CancellationToken ct)
        {
            var page = await LandingScope.RequireAsync(pages, siteState, ct);
            page.Publish(clock.GetUtcNow().UtcDateTime);
            await pages.UpdateAsync(page, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return LandingScope.ToView(page);
        }
    }

    /// <summary>Hides the site's landing page from its visitors; its content stays.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record UnpublishLandingCommand : ICommand<LandingPageView>;

    public sealed class UnpublishLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock)
        : ICommandHandler<UnpublishLandingCommand, LandingPageView>
    {
        public async Task<LandingPageView> Handle(UnpublishLandingCommand request, CancellationToken ct)
        {
            var page = await LandingScope.RequireAsync(pages, siteState, ct);
            page.Unpublish(clock.GetUtcNow().UtcDateTime);
            await pages.UpdateAsync(page, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return LandingScope.ToView(page);
        }
    }

    /// <summary>Forgets the site's landing page; administrators are offered the chooser again.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record ResetLandingCommand : ICommand<Unit>;

    public sealed class ResetLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock)
        : ICommandHandler<ResetLandingCommand, Unit>
    {
        public async Task<Unit> Handle(ResetLandingCommand request, CancellationToken ct)
        {
            var page = await pages.FindForSiteAsync(LandingScope.Site(siteState), ct);
            if (page is not null)
            {
                page.Reset(clock.GetUtcNow().UtcDateTime);
                await pages.UpdateAsync(page, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }

            return Unit.Value;
        }
    }

    /// <summary>The predefined template's starting content: a welcome, and three sections an administrator rewrites.</summary>
    public static class LandingTemplate
    {
        public static readonly PredefinedLandingContent Default = new(
            "Welcome",
            "This is the landing page of this site. Say in a sentence or two who you are and what a visitor can do here.",
            new List<LandingSectionView>
            {
                new("Get started", "Point a first-time visitor at the one thing they came for.", "Learn more", "/"),
                new("What we offer", "Describe what the site offers, in the words its visitors use.", null, null),
                new("Contact", "Tell a visitor how to reach you.", null, null),
            });
    }

    internal static class LandingScope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0 ? siteId : throw new InvalidOperationException("The landing page belongs to the request's site; none is resolved.");

        public static Stomme.Tenancy.Domain.TenantId Tenant(ITenantContext tenantContext) =>
            tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A landing page belongs to a tenant; none is resolved.");

        public static async Task<LandingPageAggregate> RequireAsync(ILandingPageRepository pages, SiteState siteState, CancellationToken ct) =>
            await pages.FindForSiteAsync(Site(siteState), ct) ?? throw new KeyNotFoundException("This site has no landing page.");

        public static LandingPageView ToView(LandingPageAggregate page) =>
            new(page.Id, page.Source, page.Status, page.Predefined(), page.DashboardId, page.UpdatedUtc, page.PublishedUtc,
                page.GeneratedHtml, page.GeneratedHtml is { Length: > 0 } html ? LandingSections.Names(html) : null);
    }
}
