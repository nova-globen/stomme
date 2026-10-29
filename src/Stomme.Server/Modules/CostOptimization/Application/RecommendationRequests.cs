#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Mediator;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.CostOptimization.Application
{
    // ============================================================================================================
    // CostOptimization's commands and queries: one advisor pass, apply, dismiss,
    // and the list - every one through the REQUEST's site, so another site's recommendations do not exist for it.
    // ============================================================================================================

    /// <summary>
    /// Runs every registered analyzer over the site's signals and raises the drafts (the predecessor platform's). A draft whose key has an
    /// open recommendation, or one still inside its dismissal cooldown, or that was drafted twice in this pass, is
    /// suppressed rather than re-raised.
    /// </summary>
    [Requires(CostOptimizationPermissions.Manage)]
    public sealed record RaiseRecommendationsCommand : ICommand<AdvisorRunView>;

    public sealed class RaiseRecommendationsCommandHandler(
        IEnumerable<IRecommendationAnalyzer> analyzers,
        IOptimizationRecommendationRepository recommendations,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        TimeProvider clock) : ICommandHandler<RaiseRecommendationsCommand, AdvisorRunView>
    {
        public async Task<AdvisorRunView> Handle(RaiseRecommendationsCommand request, CancellationToken ct)
        {
            var siteId = CostOptimizationScope.Site(siteState);

            // CHANGED from the predecessor platform, which fell back to the default tenant: a recommendation is tenant-owned, so a
            // pass with no resolved tenant is refused rather than written to a guess.
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A recommendation belongs to a tenant; none is resolved in this scope.");
            var now = clock.GetUtcNow().UtcDateTime;
            var seenKeys = (await recommendations.ListForSiteAsync(siteId, ct))
                .Where(r => r.Status == RecommendationStatus.Open || r.IsInDismissCooldown(now))
                .Select(r => r.RecommendationKey)
                .ToHashSet(StringComparer.Ordinal);

            var raisedKeys = new List<string>();
            var suppressed = 0;
            foreach (var analyzer in analyzers)
            {
                foreach (var draft in await analyzer.AnalyzeAsync(ct))
                {
                    if (!seenKeys.Add(draft.RecommendationKey))
                    {
                        suppressed++;
                        continue;
                    }

                    var recommendation = OptimizationRecommendationAggregate.Raise(
                        Guid.NewGuid(),
                        tenantId,
                        siteId,
                        draft.Category,
                        draft.RecommendationKey,
                        draft.Summary,
                        draft.Evidence,
                        draft.EstimatedSaving,
                        now);
                    await recommendations.AddAsync(recommendation, ct);
                    raisedKeys.Add(recommendation.RecommendationKey);
                }
            }

            if (raisedKeys.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new AdvisorRunView(raisedKeys.Count, raisedKeys, suppressed);
        }
    }

    /// <summary>Applies an open recommendation of the site, recording the action taken (the predecessor platform's).</summary>
    [Requires(CostOptimizationPermissions.Manage)]
    public sealed record ApplyRecommendationCommand(Guid RecommendationId, string ActionTaken) : ICommand<RecommendationDecisionView>;

    public sealed class ApplyRecommendationCommandValidator : AbstractValidator<ApplyRecommendationCommand>
    {
        public ApplyRecommendationCommandValidator()
        {
            RuleFor(command => command.ActionTaken).NotEmpty().MaximumLength(2000);
        }
    }

    public sealed class ApplyRecommendationCommandHandler(
        IOptimizationRecommendationRepository recommendations,
        IUnitOfWork unitOfWork,
        SiteState siteState,
        TimeProvider clock) : ICommandHandler<ApplyRecommendationCommand, RecommendationDecisionView>
    {
        public async Task<RecommendationDecisionView> Handle(ApplyRecommendationCommand request, CancellationToken ct)
        {
            var recommendation = await recommendations.FindForSiteAsync(CostOptimizationScope.Site(siteState), request.RecommendationId, ct);
            if (recommendation is null)
            {
                return RecommendationDecisionView.Refused(RecommendationDecisionView.NotFound, $"Recommendation '{request.RecommendationId}' was not found.");
            }

            if (recommendation.Status != RecommendationStatus.Open)
            {
                return RecommendationDecisionView.Refused(RecommendationDecisionView.NotOpen, $"Only an open recommendation can be applied (current status: {recommendation.Status}).");
            }

            recommendation.Apply(request.ActionTaken, clock.GetUtcNow().UtcDateTime);
            await recommendations.UpdateAsync(recommendation, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return RecommendationDecisionView.Decided(CostOptimizationViews.View(recommendation));
        }
    }

    /// <summary>
    /// Dismisses an open recommendation of the site with a reason (the predecessor platform's); the advisor suppresses its key for the
    /// cooldown - the request's days, or <see cref="AdvisorOptions.DismissCooldownDays"/>. A non-positive cooldown is
    /// refused by code, before the recommendation is read.
    /// </summary>
    [Requires(CostOptimizationPermissions.Manage)]
    public sealed record DismissRecommendationCommand(Guid RecommendationId, string Reason, int? CooldownDays = null) : ICommand<RecommendationDecisionView>;

    public sealed class DismissRecommendationCommandValidator : AbstractValidator<DismissRecommendationCommand>
    {
        public DismissRecommendationCommandValidator()
        {
            RuleFor(command => command.Reason).NotEmpty().MaximumLength(2000);
        }
    }

    public sealed class DismissRecommendationCommandHandler(
        IOptimizationRecommendationRepository recommendations,
        IUnitOfWork unitOfWork,
        AdvisorOptions options,
        SiteState siteState,
        TimeProvider clock) : ICommandHandler<DismissRecommendationCommand, RecommendationDecisionView>
    {
        public async Task<RecommendationDecisionView> Handle(DismissRecommendationCommand request, CancellationToken ct)
        {
            var cooldownDays = request.CooldownDays ?? options.DismissCooldownDays;
            if (cooldownDays <= 0)
            {
                return RecommendationDecisionView.Refused(RecommendationDecisionView.InvalidCooldown, "The dismissal cooldown must be a positive number of days.");
            }

            var recommendation = await recommendations.FindForSiteAsync(CostOptimizationScope.Site(siteState), request.RecommendationId, ct);
            if (recommendation is null)
            {
                return RecommendationDecisionView.Refused(RecommendationDecisionView.NotFound, $"Recommendation '{request.RecommendationId}' was not found.");
            }

            if (recommendation.Status != RecommendationStatus.Open)
            {
                return RecommendationDecisionView.Refused(RecommendationDecisionView.NotOpen, $"Only an open recommendation can be dismissed (current status: {recommendation.Status}).");
            }

            recommendation.Dismiss(request.Reason, clock.GetUtcNow().UtcDateTime, TimeSpan.FromDays(cooldownDays));
            await recommendations.UpdateAsync(recommendation, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return RecommendationDecisionView.Decided(CostOptimizationViews.View(recommendation));
        }
    }

    /// <summary>The site's recommendations, newest first, optionally by status and/or category (the predecessor platform's).</summary>
    [Requires(CostOptimizationPermissions.View)]
    public sealed record ListRecommendationsQuery(RecommendationStatus? Status = null, RecommendationCategory? Category = null) : IQuery<IReadOnlyList<RecommendationView>>;

    public sealed class ListRecommendationsQueryHandler(IOptimizationRecommendationRepository recommendations, SiteState siteState)
        : IQueryHandler<ListRecommendationsQuery, IReadOnlyList<RecommendationView>>
    {
        public async Task<IReadOnlyList<RecommendationView>> Handle(ListRecommendationsQuery request, CancellationToken ct) =>
            (await recommendations.ListForSiteAsync(CostOptimizationScope.Site(siteState), ct))
                .Where(r => (request.Status is null || r.Status == request.Status) && (request.Category is null || r.Category == request.Category))
                .Select(CostOptimizationViews.View)
                .ToList();
    }

    internal static class CostOptimizationScope
    {
        public static int Site(SiteState siteState) => siteState.Alias?.SiteId ?? throw new InvalidOperationException("Cost optimization belongs to the request's site; none is resolved.");
    }

    internal static class CostOptimizationViews
    {
        public static RecommendationView View(OptimizationRecommendationAggregate recommendation) => new(
            recommendation.Id,
            recommendation.Category,
            recommendation.RecommendationKey,
            recommendation.Summary,
            recommendation.Evidence.ToList(),
            recommendation.EstimatedSavingAmount,
            recommendation.EstimatedSavingCurrency,
            recommendation.Status,
            recommendation.RaisedUtc,
            recommendation.AppliedAction,
            recommendation.AppliedUtc,
            recommendation.DismissReason,
            recommendation.DismissedUntilUtc);
    }
}
