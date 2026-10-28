#nullable enable
using System;
using System.Linq;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 6 - the predecessor platform's <c>OptimizationRecommendationTests</c> by name (24 cases). CHANGED throughout: the
    /// recommendation belongs to a site, the saving is read from its two scalars, and the instants are UTC <c>DateTime</c>
    /// values. The port's own: a recommendation without a site is refused.
    /// </summary>
    public sealed class OptimizationRecommendationTests
    {
        private const int Site = 3;

        private static readonly DateTime RaisedAt = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

        private static OptimizationRecommendationAggregate RaiseSample(Guid? id = null) =>
            OptimizationRecommendationAggregate.Raise(
                id ?? Guid.NewGuid(),
                TenantId.Default,
                Site,
                RecommendationCategory.ModelTiering,
                "model-tiering:expensive-model:simple-tasks",
                "Route simple prompts to the cheapest tier model.",
                new[] { "82% of June requests were under 500 tokens", "avg cost/request 12x the simple-tier price" },
                EstimatedSaving.Of(120.50m, "usd"),
                RaisedAt);

        [Fact]
        public void Raise_CreatesOpenRecommendation_AndRaisesRaisedEvent()
        {
            var id = Guid.NewGuid();

            var recommendation = RaiseSample(id);

            Assert.Equal(id, recommendation.Id);
            Assert.Equal(TenantId.Default, recommendation.TenantId);
            Assert.Equal(Site, recommendation.SiteId);
            Assert.Equal(RecommendationCategory.ModelTiering, recommendation.Category);
            Assert.Equal("model-tiering:expensive-model:simple-tasks", recommendation.RecommendationKey);
            Assert.Equal("Route simple prompts to the cheapest tier model.", recommendation.Summary);
            Assert.Equal(2, recommendation.Evidence.Count);
            Assert.Equal(120.50m, recommendation.EstimatedSavingAmount);
            Assert.Equal("USD", recommendation.EstimatedSavingCurrency);
            Assert.Equal(RecommendationStatus.Open, recommendation.Status);
            Assert.Equal(RaisedAt, recommendation.RaisedUtc);

            var raised = Assert.Single(recommendation.DomainEvents.OfType<RecommendationRaisedEvent>());
            Assert.Equal(id, raised.RecommendationId);
            Assert.Equal(TenantId.Default, raised.TenantId);
            Assert.Equal(RecommendationCategory.ModelTiering, raised.Category);
            Assert.Equal("model-tiering:expensive-model:simple-tasks", raised.RecommendationKey);
            Assert.Equal(120.50m, raised.EstimatedSavingAmount);
            Assert.Equal("USD", raised.EstimatedSavingCurrency);
        }

        [Fact]
        public void Raise_RejectsEmptyEvidence()
        {
            var exception = Assert.Throws<ArgumentException>(() => OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(), TenantId.Default, Site, RecommendationCategory.Caching, "caching:high-miss-rate", "Enable response caching.",
                Array.Empty<string>(), EstimatedSaving.Of(10m, "EUR"), RaisedAt));

            Assert.Equal("evidence", exception.ParamName);
        }

        [Fact]
        public void Raise_RejectsBlankEvidenceLine()
        {
            Assert.Throws<ArgumentException>(() => OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(), TenantId.Default, Site, RecommendationCategory.Caching, "caching:high-miss-rate", "Enable response caching.",
                new[] { "cache hit rate was 4% in June", "   " }, EstimatedSaving.Of(10m, "EUR"), RaisedAt));
        }

        [Fact]
        public void Raise_RejectsUndefinedCategory()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(), TenantId.Default, Site, (RecommendationCategory)0, "unknown:finding", "Something.",
                new[] { "a cited line" }, EstimatedSaving.Of(1m, "USD"), RaisedAt));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Raise_RejectsBlankRecommendationKey(string key)
        {
            Assert.Throws<ArgumentException>(() => OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(), TenantId.Default, Site, RecommendationCategory.Cleanup, key, "Purge stale artifacts.",
                new[] { "37 GB of expired export artifacts" }, EstimatedSaving.Of(5m, "USD"), RaisedAt));
        }

        [Fact]
        public void EstimatedSaving_RejectsNegativeAmount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EstimatedSaving.Of(-0.01m, "USD"));
        }

        [Fact]
        public void EstimatedSaving_AllowsZeroAmount()
        {
            Assert.Equal(0m, EstimatedSaving.Of(0m, "USD").Amount);
        }

        [Theory]
        [InlineData("")]
        [InlineData("US")]
        [InlineData("USDX")]
        [InlineData("U$D")]
        public void EstimatedSaving_RejectsInvalidCurrency(string currency)
        {
            Assert.Throws<ArgumentException>(() => EstimatedSaving.Of(1m, currency));
        }

        [Fact]
        public void EstimatedSaving_NormalizesCurrencyToUppercase()
        {
            Assert.Equal("EUR", EstimatedSaving.Of(1m, " eur ").Currency);
        }

        [Fact]
        public void Apply_RecordsActionAndTimestamp_AndRaisesAppliedEvent()
        {
            var recommendation = RaiseSample();
            var appliedAt = RaisedAt.AddDays(1);

            recommendation.Apply("Switched default routing to the simple tier.", appliedAt);

            Assert.Equal(RecommendationStatus.Applied, recommendation.Status);
            Assert.Equal("Switched default routing to the simple tier.", recommendation.AppliedAction);
            Assert.Equal(appliedAt, recommendation.AppliedUtc);

            var applied = Assert.Single(recommendation.DomainEvents.OfType<RecommendationAppliedEvent>());
            Assert.Equal(recommendation.Id, applied.RecommendationId);
            Assert.Equal(recommendation.RecommendationKey, applied.RecommendationKey);
            Assert.Equal("Switched default routing to the simple tier.", applied.ActionTaken);
        }

        [Fact]
        public void Apply_RejectsBlankAction()
        {
            var recommendation = RaiseSample();

            Assert.Throws<ArgumentException>(() => recommendation.Apply("  ", RaisedAt.AddDays(1)));
            Assert.Equal(RecommendationStatus.Open, recommendation.Status);
        }

        [Fact]
        public void Apply_Twice_IsRejected()
        {
            var recommendation = RaiseSample();
            recommendation.Apply("Done once.", RaisedAt.AddDays(1));

            Assert.Throws<InvalidOperationException>(() => recommendation.Apply("Done twice.", RaisedAt.AddDays(2)));
        }

        [Fact]
        public void Dismiss_RecordsReasonAndCooldown_AndRaisesDismissedEvent()
        {
            var recommendation = RaiseSample();
            var dismissedAt = RaisedAt.AddDays(2);
            var cooldown = TimeSpan.FromDays(30);

            recommendation.Dismiss("Model choice is contractual this quarter.", dismissedAt, cooldown);

            Assert.Equal(RecommendationStatus.Dismissed, recommendation.Status);
            Assert.Equal("Model choice is contractual this quarter.", recommendation.DismissReason);
            Assert.Equal(dismissedAt, recommendation.DismissedUtc);
            Assert.Equal(dismissedAt + cooldown, recommendation.DismissedUntilUtc);

            var dismissed = Assert.Single(recommendation.DomainEvents.OfType<RecommendationDismissedEvent>());
            Assert.Equal(recommendation.Id, dismissed.RecommendationId);
            Assert.Equal(recommendation.RecommendationKey, dismissed.RecommendationKey);
            Assert.Equal("Model choice is contractual this quarter.", dismissed.Reason);
            Assert.Equal(dismissedAt + cooldown, dismissed.DismissedUntilUtc);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Dismiss_RejectsNonPositiveCooldown(int hours)
        {
            var recommendation = RaiseSample();

            Assert.Throws<ArgumentOutOfRangeException>(() => recommendation.Dismiss("No thanks.", RaisedAt.AddDays(1), TimeSpan.FromHours(hours)));
            Assert.Equal(RecommendationStatus.Open, recommendation.Status);
        }

        [Fact]
        public void Dismiss_RejectsBlankReason()
        {
            var recommendation = RaiseSample();

            Assert.Throws<ArgumentException>(() => recommendation.Dismiss(" ", RaisedAt.AddDays(1), TimeSpan.FromDays(7)));
        }

        [Fact]
        public void Dismiss_AfterApply_IsRejected()
        {
            var recommendation = RaiseSample();
            recommendation.Apply("Applied.", RaisedAt.AddDays(1));

            Assert.Throws<InvalidOperationException>(() => recommendation.Dismiss("Too late.", RaisedAt.AddDays(2), TimeSpan.FromDays(7)));
        }

        [Fact]
        public void Apply_AfterDismiss_IsRejected()
        {
            var recommendation = RaiseSample();
            recommendation.Dismiss("Not now.", RaisedAt.AddDays(1), TimeSpan.FromDays(7));

            Assert.Throws<InvalidOperationException>(() => recommendation.Apply("Changed my mind.", RaisedAt.AddDays(2)));
        }

        [Fact]
        public void IsInDismissCooldown_TracksTheCooldownWindow()
        {
            var recommendation = RaiseSample();
            var dismissedAt = RaisedAt.AddDays(1);
            recommendation.Dismiss("Later.", dismissedAt, TimeSpan.FromDays(30));

            Assert.True(recommendation.IsInDismissCooldown(dismissedAt.AddDays(29)));
            Assert.False(recommendation.IsInDismissCooldown(dismissedAt.AddDays(30)));
        }

        [Fact]
        public void IsInDismissCooldown_IsFalseForOpenAndApplied()
        {
            var open = RaiseSample();
            Assert.False(open.IsInDismissCooldown(RaisedAt));

            var applied = RaiseSample();
            applied.Apply("Applied.", RaisedAt.AddDays(1));
            Assert.False(applied.IsInDismissCooldown(RaisedAt.AddDays(1)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        public void A_recommendation_without_a_site_is_refused(int siteId)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(), TenantId.Default, siteId, RecommendationCategory.Cleanup, "cleanup:temp-files", "Purge temp files.",
                new[] { "12 GB of temp files" }, EstimatedSaving.Of(0m, "USD"), RaisedAt));
        }
    }
}
