#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Mediator;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Stomme.Tests.CostOptimization.CostOptimizationApplicationHarness;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// The predecessor platform's <c>RecommendationCqrsTests</c> by name, through the mediator over the module's own startup
    /// on the File store. CHANGED throughout: the predecessor platform's <c>Result&lt;RecommendationView&gt;</c> is the decision view's
    /// <c>Success</c>/<c>Code</c>/<c>Recommendation</c>, with the predecessor platform's codes; the instants are UTC <c>DateTime</c> values.
    /// The port's own: another site's recommendation is not found, and another site's open key suppresses nothing here.
    /// </summary>
    public sealed class RecommendationCqrsTests
    {
        // --- RaiseRecommendationsCommand ---

        [Fact]
        public async Task Raise_PersistsEveryAnalyzerDraftAsAnOpenRecommendation()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(
                new[] { Draft("model-tiering:gpt-large:simple-tasks", RecommendationCategory.ModelTiering, 300m) },
                new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 7.5m) });

            var view = await harness.RaiseAsync();

            Assert.Equal(2, view.RaisedCount);
            Assert.Equal(new[] { "model-tiering:gpt-large:simple-tasks", "caching:assistant:low-hit-rate" }, view.RaisedKeys);
            Assert.Equal(0, view.SuppressedCount);
            var persisted = await harness.RecommendationsAsync();
            Assert.Equal(2, persisted.Count);
            Assert.All(persisted, r => Assert.Equal(RecommendationStatus.Open, r.Status));
            Assert.All(persisted, r => Assert.Equal(Start, r.RaisedUtc));
            Assert.All(persisted, r => Assert.NotEmpty(r.Evidence));
            Assert.All(persisted, r => Assert.Equal(SiteId, r.SiteId));
        }

        [Fact]
        public async Task Raise_DeduplicatesTheSameKeyDraftedTwiceInOneRun()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(
                new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 5m) },
                new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 9m) });

            var view = await harness.RaiseAsync();

            Assert.Equal(1, view.RaisedCount);
            Assert.Equal(1, view.SuppressedCount);
            Assert.Single(await harness.RecommendationsAsync());
        }

        [Fact]
        public async Task Raise_SuppressesAKeyThatIsAlreadyOpen()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 5m) });

            await harness.RaiseAsync();
            var second = await harness.RaiseAsync();

            Assert.Equal(0, second.RaisedCount);
            Assert.Equal(1, second.SuppressedCount);
            Assert.Single(await harness.RecommendationsAsync());
        }

        [Fact]
        public async Task Raise_HonorsTheDismissCooldown_ThenReRaisesAfterItExpires()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[] { Draft("right-sizing:cloud-cost:azure:usd:mom-increase", RecommendationCategory.RightSizing, 150m) });
            var first = await harness.RaiseAsync();
            var raisedId = (await harness.RecommendationsAsync()).Single().Id;
            var dismissed = await harness.SendAsync(new DismissRecommendationCommand(raisedId, "Planned migration traffic.", CooldownDays: 10));
            Assert.True(dismissed.Success);

            harness.Clock.Set(Start.AddDays(5)); // still inside the 10-day cooldown
            var duringCooldown = await harness.RaiseAsync();

            Assert.Equal(1, first.RaisedCount);
            Assert.Equal(0, duringCooldown.RaisedCount);
            Assert.Equal(1, duringCooldown.SuppressedCount);

            harness.Clock.Set(Start.AddDays(11)); // cooldown expired
            var afterCooldown = await harness.RaiseAsync();

            Assert.Equal(1, afterCooldown.RaisedCount);
            var all = await harness.RecommendationsAsync();
            Assert.Equal(2, all.Count); // a re-raise is a new aggregate; the dismissed one keeps its history
            Assert.Single(all, r => r.Status == RecommendationStatus.Dismissed);
            Assert.Single(all, r => r.Status == RecommendationStatus.Open);
        }

        // --- Apply / Dismiss lifecycle ---

        [Fact]
        public async Task Apply_RecordsTheActionTaken_AndRefusesASecondDecision()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[] { Draft("model-tiering:gpt-large:simple-tasks", RecommendationCategory.ModelTiering, 300m) });
            await harness.RaiseAsync();
            var id = (await harness.RecommendationsAsync()).Single().Id;

            var applied = await harness.SendAsync(new ApplyRecommendationCommand(id, "Mapped Simple tier to gpt-mini in CostOptimization:ModelTiers."));

            Assert.True(applied.Success);
            Assert.Equal(RecommendationStatus.Applied, applied.Recommendation!.Status);
            Assert.Equal("Mapped Simple tier to gpt-mini in CostOptimization:ModelTiers.", applied.Recommendation.AppliedAction);
            Assert.Equal(Start, applied.Recommendation.AppliedUtc);

            var again = await harness.SendAsync(new ApplyRecommendationCommand(id, "Again."));
            Assert.False(again.Success);
            Assert.Equal(RecommendationDecisionView.NotOpen, again.Code);
        }

        [Fact]
        public async Task Apply_FailsForAnUnknownRecommendation()
        {
            await using var harness = await StartAsync();

            var result = await harness.SendAsync(new ApplyRecommendationCommand(Guid.NewGuid(), "Anything."));

            Assert.False(result.Success);
            Assert.Equal(RecommendationDecisionView.NotFound, result.Code);
        }

        [Fact]
        public async Task Dismiss_UsesTheConfiguredDefaultCooldown_AndHonorsAnOverride()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[]
            {
                Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 5m),
                Draft("right-sizing:ai-quota:approaching", RecommendationCategory.RightSizing, 0m),
            });
            await harness.RaiseAsync();
            var byKey = (await harness.RecommendationsAsync()).ToDictionary(r => r.RecommendationKey);

            var defaulted = await harness.SendAsync(new DismissRecommendationCommand(byKey["caching:assistant:low-hit-rate"].Id, "Cache is intentionally off."));
            var overridden = await harness.SendAsync(new DismissRecommendationCommand(byKey["right-sizing:ai-quota:approaching"].Id, "Quota bump already ordered.", CooldownDays: 7));

            Assert.True(defaulted.Success);
            Assert.Equal(Start.AddDays(30), defaulted.Recommendation!.DismissedUntilUtc); // AdvisorOptions.DismissCooldownDays default
            Assert.True(overridden.Success);
            Assert.Equal(Start.AddDays(7), overridden.Recommendation!.DismissedUntilUtc);
            Assert.Equal("Quota bump already ordered.", overridden.Recommendation.DismissReason);
        }

        [Fact]
        public async Task Dismiss_RejectsANonPositiveCooldown()
        {
            await using var harness = await StartAsync();

            var result = await harness.SendAsync(new DismissRecommendationCommand(Guid.NewGuid(), "Reason.", CooldownDays: 0));

            Assert.False(result.Success);
            Assert.Equal(RecommendationDecisionView.InvalidCooldown, result.Code);
        }

        // --- ListRecommendationsQuery ---

        [Fact]
        public async Task List_FiltersByStatusAndCategory_NewestFirst()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[]
            {
                Draft("model-tiering:gpt-large:simple-tasks", RecommendationCategory.ModelTiering, 300m),
                Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 7.5m),
            });
            await harness.RaiseAsync();
            harness.Clock.Set(Start.AddDays(1));
            harness.Analyzer.Script(new[] { Draft("right-sizing:licensing:usd:seat-review", RecommendationCategory.RightSizing, 0m) });
            await harness.RaiseAsync();
            var cachingId = (await harness.RecommendationsAsync()).Single(r => r.RecommendationKey == "caching:assistant:low-hit-rate").Id;
            Assert.True((await harness.SendAsync(new DismissRecommendationCommand(cachingId, "Cache is intentionally off."))).Success);

            var everything = await harness.SendAsync(new ListRecommendationsQuery());
            var open = await harness.SendAsync(new ListRecommendationsQuery(Status: RecommendationStatus.Open));
            var dismissed = await harness.SendAsync(new ListRecommendationsQuery(Status: RecommendationStatus.Dismissed));
            var tiering = await harness.SendAsync(new ListRecommendationsQuery(Category: RecommendationCategory.ModelTiering));

            Assert.Equal(3, everything.Count);
            Assert.Equal("right-sizing:licensing:usd:seat-review", everything[0].RecommendationKey); // newest first
            Assert.Equal(2, open.Count);
            Assert.Equal("caching:assistant:low-hit-rate", Assert.Single(dismissed).RecommendationKey);
            Assert.Equal("model-tiering:gpt-large:simple-tasks", Assert.Single(tiering).RecommendationKey);
        }

        // --- The port's own ---

        [Fact]
        public async Task Another_sites_recommendation_is_not_found_and_its_open_key_suppresses_nothing_here()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 5m) });
            harness.Site = SiteId + 1;
            await harness.RaiseAsync();
            var theirs = (await harness.RecommendationsAsync(SiteId + 1)).Single().Id;

            harness.Site = SiteId;
            var applied = await harness.SendAsync(new ApplyRecommendationCommand(theirs, "Not mine to apply."));
            var dismissed = await harness.SendAsync(new DismissRecommendationCommand(theirs, "Not mine to dismiss."));
            var mine = await harness.RaiseAsync();

            Assert.Equal((false, RecommendationDecisionView.NotFound), (applied.Success, applied.Code));
            Assert.Equal((false, RecommendationDecisionView.NotFound), (dismissed.Success, dismissed.Code));
            Assert.Equal((1, 0), (mine.RaisedCount, mine.SuppressedCount));
            Assert.Empty(await harness.SendAsync(new ListRecommendationsQuery(Status: RecommendationStatus.Applied)));
            Assert.Equal(RecommendationStatus.Open, (await harness.RecommendationsAsync(SiteId + 1)).Single().Status);
        }

        [Fact]
        public async Task A_blank_action_or_reason_is_refused_by_the_validators()
        {
            await using var harness = await StartAsync();
            harness.Analyzer.Script(new[] { Draft("caching:assistant:low-hit-rate", RecommendationCategory.Caching, 5m) });
            await harness.RaiseAsync();
            var id = (await harness.RecommendationsAsync()).Single().Id;

            await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => harness.SendAsync(new ApplyRecommendationCommand(id, " ")));
            await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => harness.SendAsync(new DismissRecommendationCommand(id, "")));
            Assert.Equal(RecommendationStatus.Open, (await harness.RecommendationsAsync()).Single().Status);
        }
    }

    /// <summary>
    /// The predecessor platform's <c>CostOptimizationRegistrationTests.AddCostOptimization_BuildsAndResolvesTheRecommendationRepository</c>
    /// by name over the module's own startup, and the port's own: the four requests and the advisor's options from the HOST's
    /// configuration. the predecessor platform's job, cleanup-option and navigation cases are booked for increments 7 and 8, where their
    /// subjects land.
    /// </summary>
    public sealed class CostOptimizationRegistrationTests
    {
        [Fact]
        public async Task AddCostOptimization_BuildsAndResolvesTheRecommendationRepository()
        {
            await using var harness = await StartAsync();

            Assert.NotNull(await harness.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<IOptimizationRecommendationRepository>())));
        }

        [Fact]
        public async Task The_startup_registers_the_four_requests_and_reads_the_advisor_options_from_the_host_configuration()
        {
            await using var harness = await StartAsync(new Dictionary<string, string?>
            {
                ["CostOptimization:Advisor:DismissCooldownDays"] = "12",
                ["CostOptimization:Advisor:CloudCostAnomalyMinIncreasePercent"] = "45",
            });

            var resolved = await harness.InScopeAsync(sp => Task.FromResult(new object?[]
            {
                sp.GetService<IRequestHandler<RaiseRecommendationsCommand, AdvisorRunView>>(),
                sp.GetService<IRequestHandler<ApplyRecommendationCommand, RecommendationDecisionView>>(),
                sp.GetService<IRequestHandler<DismissRecommendationCommand, RecommendationDecisionView>>(),
                sp.GetService<IRequestHandler<ListRecommendationsQuery, IReadOnlyList<RecommendationView>>>(),
            }));
            var options = await harness.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<AdvisorOptions>()));

            Assert.All(resolved, Assert.NotNull);
            Assert.Equal((12, 45m), (options.DismissCooldownDays, options.CloudCostAnomalyMinIncreasePercent));
            Assert.Equal(new AdvisorOptions().LicensingReviewMinMonthlySpend, options.LicensingReviewMinMonthlySpend);
        }
    }
}
