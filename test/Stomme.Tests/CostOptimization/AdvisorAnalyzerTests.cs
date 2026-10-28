#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 7 - the predecessor platform's <c>AdvisorAnalyzerTests</c> by name (17 cases): the fixtures and assertions are
    /// unchanged but for the options instance; the binding and registration cases CHANGED as marked.
    /// </summary>
    public sealed class AdvisorAnalyzerTests
    {
        private static readonly AdvisorOptions Defaults = new();

        // --- Expensive model for simple tasks (ModelTiering) ---

        [Fact]
        public async Task ExpensiveModel_FlagsAHighCostShareModelWithSimpleTaskTraffic()
        {
            var usage = AssistantUsage(
                Model("OpenAI", "gpt-large", requests: 200, cached: 0, totalTokens: 200 * 300, cost: 600m),
                Model("OpenAI", "gpt-mini", requests: 100, cached: 0, totalTokens: 100 * 300, cost: 200m));
            var analyzer = new ExpensiveModelForSimpleTaskAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            var draft = Assert.Single(await analyzer.AnalyzeAsync());

            Assert.Equal(RecommendationCategory.ModelTiering, draft.Category);
            Assert.Equal("model-tiering:gpt-large:simple-tasks", draft.RecommendationKey);
            Assert.Equal(300m, draft.EstimatedSaving.Amount); // 600 × the 0.5 assumed saving rate
            Assert.Equal("USD", draft.EstimatedSaving.Currency);
            Assert.Contains(draft.Evidence, line => line.Contains("gpt-large") && line.Contains("200 requests") && line.Contains("300 tokens"));
            Assert.Contains(draft.Evidence, line => line.Contains("2026-06") && line.Contains("600") && line.Contains("75.0%"));
        }

        [Fact]
        public async Task ExpensiveModel_IgnoresAModelWhoseAverageTokensExceedTheSimpleBudget()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-large", requests: 200, cached: 0, totalTokens: 200 * 501, cost: 600m));
            var analyzer = new ExpensiveModelForSimpleTaskAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        [Fact]
        public async Task ExpensiveModel_IgnoresAModelBelowTheRequestFloor()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-large", requests: 49, cached: 0, totalTokens: 49 * 300, cost: 600m));
            var analyzer = new ExpensiveModelForSimpleTaskAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        [Fact]
        public async Task ExpensiveModel_DraftsNothingWithoutASignalSource()
        {
            Assert.Empty(await new ExpensiveModelForSimpleTaskAnalyzer(Defaults).AnalyzeAsync());
        }

        // --- High cache miss (Caching) ---

        [Fact]
        public async Task CacheMiss_FlagsALowCachedShareOverMeaningfulVolume()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-4o", requests: 200, cached: 5, totalTokens: 200 * 800, cost: 100m));
            var analyzer = new HighCacheMissAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            var draft = Assert.Single(await analyzer.AnalyzeAsync());

            Assert.Equal(RecommendationCategory.Caching, draft.Category);
            Assert.Equal("caching:assistant:low-hit-rate", draft.RecommendationKey);
            Assert.Equal(7.5m, draft.EstimatedSaving.Amount); // 100 × (10% floor − 2.5% actual)
            Assert.Contains(draft.Evidence, line => line.Contains("5 of 200") && line.Contains("2.5%"));
        }

        [Fact]
        public async Task CacheMiss_IgnoresAMonthAtOrAboveTheHitShareFloor()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-4o", requests: 200, cached: 30, totalTokens: 200 * 800, cost: 100m));
            var analyzer = new HighCacheMissAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        [Fact]
        public async Task CacheMiss_IgnoresAMonthBelowTheVolumeFloor()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-4o", requests: 99, cached: 0, totalTokens: 99 * 800, cost: 100m));
            var analyzer = new HighCacheMissAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        // --- Quota approaching (RightSizing) ---

        [Fact]
        public async Task Quota_FlagsAQuotaAtTheWarningLevel()
        {
            var usage = AssistantUsage(quota: new AssistantQuotaSignal(Limit: 100, Consumed: 85),
                Model("OpenAI", "gpt-4o", requests: 85, cached: 0, totalTokens: 85 * 100, cost: 10m));
            var analyzer = new QuotaApproachingAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            var draft = Assert.Single(await analyzer.AnalyzeAsync());

            Assert.Equal(RecommendationCategory.RightSizing, draft.Category);
            Assert.Equal("right-sizing:ai-quota:approaching", draft.RecommendationKey);
            Assert.Equal(0m, draft.EstimatedSaving.Amount); // an advisory, not a measured spend cut
            Assert.Contains(draft.Evidence, line => line.Contains("85 of 100") && line.Contains("85.0%"));
        }

        [Fact]
        public async Task Quota_IgnoresConsumptionBelowTheWarningLevel()
        {
            var usage = AssistantUsage(quota: new AssistantQuotaSignal(Limit: 100, Consumed: 79),
                Model("OpenAI", "gpt-4o", requests: 79, cached: 0, totalTokens: 79 * 100, cost: 10m));
            var analyzer = new QuotaApproachingAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        [Fact]
        public async Task Quota_IgnoresAnUnlimitedTenant()
        {
            var usage = AssistantUsage(Model("OpenAI", "gpt-4o", requests: 5000, cached: 0, totalTokens: 5000 * 100, cost: 10m));
            var analyzer = new QuotaApproachingAnalyzer(Defaults, new StubAssistantUsageSignalSource(usage));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        // --- Cloud cost anomaly (RightSizing) ---

        [Fact]
        public async Task CloudAnomaly_FlagsAMonthOverMonthSpikePerProviderAndCurrency()
        {
            var spend = CloudCost(
                byProvider: [new CloudSpendSignal("Azure", "USD", CurrentMonthTotal: 400m, PreviousMonthTotal: 250m)]);
            var analyzer = new CloudCostAnomalyAnalyzer(Defaults, new StubCloudCostSignalSource(spend));

            var draft = Assert.Single(await analyzer.AnalyzeAsync());

            Assert.Equal(RecommendationCategory.RightSizing, draft.Category);
            Assert.Equal("right-sizing:cloud-cost:azure:usd:mom-increase", draft.RecommendationKey);
            Assert.Equal(150m, draft.EstimatedSaving.Amount); // the increase itself
            Assert.Equal("USD", draft.EstimatedSaving.Currency);
            Assert.Contains(draft.Evidence, line =>
                line.Contains("Azure") && line.Contains("2026-06") && line.Contains("2026-05") && line.Contains("60.0%"));
        }

        [Fact]
        public async Task CloudAnomaly_IgnoresAnIncreaseBelowTheThreshold()
        {
            var spend = CloudCost(byProvider: [new CloudSpendSignal("Azure", "USD", 300m, 250m)]);
            var analyzer = new CloudCostAnomalyAnalyzer(Defaults, new StubCloudCostSignalSource(spend));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        [Fact]
        public async Task CloudAnomaly_IgnoresASpikeUnderTheSpendFloorOrWithoutABaseline()
        {
            var spend = CloudCost(byProvider:
            [
                new CloudSpendSignal("Aws", "USD", CurrentMonthTotal: 60m, PreviousMonthTotal: 30m), // +100% but tiny base
                new CloudSpendSignal("Gcp", "USD", CurrentMonthTotal: 500m, PreviousMonthTotal: 0m), // no baseline month
            ]);
            var analyzer = new CloudCostAnomalyAnalyzer(Defaults, new StubCloudCostSignalSource(spend));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        // --- Licensing spend review (RightSizing) ---

        [Fact]
        public async Task Licensing_FlagsLicensingSpendAtTheReviewFloorAndCitesEachService()
        {
            var spend = CloudCost(byService:
            [
                new CloudServiceSpendSignal("Licensing", "Microsoft 365 E5", "USD", 150m),
                new CloudServiceSpendSignal("Licensing", "Salesforce Sales Cloud", "USD", 90m),
                new CloudServiceSpendSignal("Compute", "Virtual Machines", "USD", 900m),
            ]);
            var analyzer = new LicensingSpendReviewAnalyzer(Defaults, new StubCloudCostSignalSource(spend));

            var draft = Assert.Single(await analyzer.AnalyzeAsync());

            Assert.Equal(RecommendationCategory.RightSizing, draft.Category);
            Assert.Equal("right-sizing:licensing:usd:seat-review", draft.RecommendationKey);
            Assert.Equal(0m, draft.EstimatedSaving.Amount); // no seat-utilization signal → no fabricated estimate
            Assert.Contains(draft.Evidence, line => line.Contains("240 USD"));
            Assert.Contains(draft.Evidence, line => line.Contains("Microsoft 365 E5"));
            Assert.Contains(draft.Evidence, line => line.Contains("Salesforce Sales Cloud"));
            Assert.DoesNotContain(draft.Evidence, line => line.Contains("Virtual Machines"));
        }

        [Fact]
        public async Task Licensing_IgnoresLicensingSpendBelowTheReviewFloor()
        {
            var spend = CloudCost(byService: [new CloudServiceSpendSignal("Licensing", "Microsoft 365 E5", "USD", 99m)]);
            var analyzer = new LicensingSpendReviewAnalyzer(Defaults, new StubCloudCostSignalSource(spend));

            Assert.Empty(await analyzer.AnalyzeAsync());
        }

        // --- Options binding + registration ---

        // CHANGED: the section is CostOptimization:Advisor (the predecessor platform's the predecessor platform: prefix dropped), read from the HOST's configuration.
        [Fact]
        public void AdvisorOptions_BindFromTheAdvisorSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CostOptimization:Advisor:SimpleTaskMaxAvgTokens"] = "250",
                    ["CostOptimization:Advisor:MinCachedHitShare"] = "0.25",
                    ["CostOptimization:Advisor:CloudCostAnomalyMinIncreasePercent"] = "50",
                    ["CostOptimization:Advisor:DismissCooldownDays"] = "14",
                })
                .Build();

            var bound = AdvisorOptions.From(configuration);

            Assert.Equal(250, bound.SimpleTaskMaxAvgTokens);
            Assert.Equal(0.25m, bound.MinCachedHitShare);
            Assert.Equal(50m, bound.CloudCostAnomalyMinIncreasePercent);
            Assert.Equal(14, bound.DismissCooldownDays);
            Assert.Equal(0.30m, bound.ExpensiveModelMinCostShare); // untouched defaults stay
        }

        // CHANGED: through the module's own startup, with neither read port composed (no CloudUsage, no Metering) - the
        // harness's scripted analyzer set aside.
        [Fact]
        public async Task AddCostOptimization_RegistersEveryAnalyzer_AndTheyStaySilentWithoutSignalSources()
        {
            await using var harness = await CostOptimizationApplicationHarness.StartAsync();

            var analyzers = await harness.InScopeAsync(sp => Task.FromResult(sp.GetServices<IRecommendationAnalyzer>().Where(analyzer => analyzer is not ScriptedAnalyzer).ToList()));

            Assert.Equal(5, analyzers.Count);
            foreach (var analyzer in analyzers)
            {
                Assert.Empty(await analyzer.AnalyzeAsync());
            }
        }

        // --- Fixtures ---

        private static AssistantModelUsageSignal Model(
            string provider, string model, long requests, long cached, long totalTokens, decimal cost) =>
            new(provider, model, requests, cached, totalTokens, cost);

        private static AssistantUsageSignal AssistantUsage(params AssistantModelUsageSignal[] byModel) =>
            AssistantUsage(quota: null, byModel);

        private static AssistantUsageSignal AssistantUsage(AssistantQuotaSignal? quota, params AssistantModelUsageSignal[] byModel) =>
            new("2026-06", "USD", byModel, quota);

        private static CloudCostSignal CloudCost(
            IReadOnlyList<CloudSpendSignal>? byProvider = null,
            IReadOnlyList<CloudServiceSpendSignal>? byService = null) =>
            new("2026-06", "2026-05", byProvider ?? [], byService ?? []);

        private sealed class StubAssistantUsageSignalSource(AssistantUsageSignal signal) : IAssistantUsageSignalSource
        {
            public Task<AssistantUsageSignal> GetCurrentMonthUsageAsync(CancellationToken ct = default) =>
                Task.FromResult(signal);
        }

        private sealed class StubCloudCostSignalSource(CloudCostSignal signal) : ICloudCostSignalSource
        {
            public Task<CloudCostSignal> GetCurrentMonthSpendAsync(CancellationToken ct = default) =>
                Task.FromResult(signal);
        }
    }
}
