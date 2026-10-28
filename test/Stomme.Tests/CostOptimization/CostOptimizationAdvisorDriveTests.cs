#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Infrastructure;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Models;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 7 - the advisor and Metering's side of the AI-usage port on an installed SQLite host: the predecessor platform's
    /// <c>CostOptimizationSignalSourceTests.AiUsageSignalSource_*</c> and <c>AdvisorJobTests</c> by name. CHANGED
    /// throughout: the predecessor platform's per-tenant pass over a tenant directory is a per-SITE pass of the framework's weekly job, and its
    /// "without a tenant directory" case is a run with no tenant set.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CostOptimizationAdvisorDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string HostB = "costoptb.localhost";

        [Fact]
        public async Task AiUsageSignalSource_ProjectsTheCurrentMonthPerModelWithCacheAndQuota()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var now = DateTime.UtcNow;
                var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var signal = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var ledger = sp.GetRequiredService<IAiUsageRecordRepository>();
                    await ledger.AddAsync(Row(tenantId, alias.SiteId, AiCallStatus.Success, 400, 200, 2.5m, now));
                    await ledger.AddAsync(Row(tenantId, alias.SiteId, AiCallStatus.Cached, 400, 200, 0m, now)); // a cache hit costs nothing
                    await ledger.AddAsync(Row(tenantId, alias.SiteId, AiCallStatus.Success, 100, 50, 1m, monthStart.AddDays(-3)));
                    await ledger.AddAsync(Row(tenantId, alias.SiteId + 100, AiCallStatus.Success, 999, 999, 9m, now)); // another site's
                    var quota = QuotaAggregate.Define(QuotaIds.For(tenantId, alias.SiteId, AiUsageRecorder.CallsMetric, QuotaPeriod.Monthly), tenantId, alias.SiteId,
                        AiUsageRecorder.CallsMetric, QuotaPeriod.Monthly, 100, 80, now);
                    quota.Repair(2, monthStart);
                    await sp.GetRequiredService<IQuotaRepository>().AddAsync(quota);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return await sp.GetRequiredService<IAssistantUsageSignalSource>().GetCurrentMonthUsageAsync();
                });

                Assert.Equal(monthStart.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), signal.Month);
                Assert.Equal("USD", signal.Currency);
                var model = Assert.Single(signal.ByModel); // the previous-month row and another site's stay out
                Assert.Equal(("OpenAI", "gpt-4o"), (model.Provider, model.Model));
                Assert.Equal((2L, 1L, 1200L), (model.RequestCount, model.CachedRequestCount, model.TotalTokens)); // cache hits keep their raw tokens
                Assert.Equal(2.5m, model.EstimatedCost);
                Assert.NotNull(signal.Quota);
                Assert.Equal((100L, 2L), (signal.Quota!.Limit, signal.Quota.Consumed));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void Advisor_UsesTheStableJobName_AndAWeeklyDefaultCron()
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var job = new CostOptimizationAdvisorJob(services.GetRequiredService<IServiceScopeFactory>());

            Assert.Equal(("Cost Optimization Advisor Job", "w", 1, true), (job.Name, job.Frequency, job.Interval, job.IsEnabled));
        }

        [Fact]
        public async Task Advisor_RunsTheAnalysisOncePerSite_AndCountsRaisedRecommendations_ThenRaisesNothingWhileTheKeysStayOpen()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await StopTheSchedulersAdvisorAsync(app);
                var siteId = await SeedSpikeAsync(app, Host);

                // the predecessor platform's Advisor_RunsTheAnalysisOncePerTenant_AndCountsRaisedRecommendations: the anomaly and the licensing review.
                var first = await RunJobAsync(app);
                output.WriteLine($"[first] {first}");
                Assert.Contains("Raised 2, Suppressed 0", first);
                var raised = await GateDrive.InTenantAsync(app, Host, (sp, alias) => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(siteId));
                Assert.Equal(new[] { "right-sizing:cloud-cost:azure:usd:mom-increase", "right-sizing:licensing:usd:seat-review" }, raised.Select(r => r.RecommendationKey).OrderBy(k => k, StringComparer.Ordinal));
                Assert.All(raised, r => Assert.Equal(RecommendationStatus.Open, r.Status));

                // the predecessor platform's Advisor_SecondRun_RaisesNothingWhileTheKeysStayOpen.
                var second = await RunJobAsync(app);
                output.WriteLine($"[second] {second}");
                Assert.Contains("Raised 0, Suppressed 2", second);
                Assert.Equal(2, (await GateDrive.InTenantAsync(app, Host, (sp, alias) => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(siteId))).Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Advisor_WithoutATenantForTheRun_HasNothingToProcess()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var scope = app.Services.CreateScope();
                var log = await new CostOptimizationAdvisorJob(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);

                Assert.Equal("Cost Optimization Advisor: No Tenant Is Set For This Run<br />", log);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Advisor_OneSiteFailure_DoesNotStopTheRemainingSites()
        {
            var failing = new FailingSite();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output, builder =>
            {
                builder.Services.AddSingleton(failing);
                builder.Services.AddScoped<IRecommendationAnalyzer, FailingSiteAnalyzer>();
            });
            try
            {
                await StopTheSchedulersAdvisorAsync(app);
                failing.SiteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var second = await CreateSecondSiteAsync(app);
                var secondSiteId = await SeedSpikeAsync(app, HostB);
                Assert.Equal(second.SiteId, secondSiteId);

                var log = await RunJobAsync(app);
                output.WriteLine($"[one failing site] {log}");

                Assert.Contains("Cost Optimization Advisor Failed For Site:", log);
                Assert.Contains("For Site: Cost Optimization Site B - Raised 2, Suppressed 0", log);
                Assert.Equal(2, (await GateDrive.InTenantAsync(app, HostB, (sp, alias) => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(secondSiteId))).Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static AiUsageRecordAggregate Row(Stomme.Tenancy.Domain.TenantId tenantId, int siteId, AiCallStatus status, long input, long outputTokens, decimal cost, DateTime at) =>
            AiUsageRecordAggregate.Append(Guid.NewGuid(), tenantId, siteId, "OpenAI", "gpt-4o", "assistant", input, outputTokens, 120, status, cost, "USD", null, null, Guid.NewGuid(), at);

        // A month-over-month spike (100 -> 400 USD on Azure) and 150 USD of licensing this month: two recommendations.
        private static Task<int> SeedSpikeAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var monthStart = new DateOnly(today.Year, today.Month, 1);
                var records = sp.GetRequiredService<ICloudCostRecordRepository>();
                await records.AddAsync(CloudCostRecordAggregate.Record(Guid.NewGuid(), tenantId, alias.SiteId, new CloudCostEntry(monthStart, CloudProvider.Azure, CloudServiceCategory.Compute, "Virtual Machines", "vm-1", 400m, "USD")));
                await records.AddAsync(CloudCostRecordAggregate.Record(Guid.NewGuid(), tenantId, alias.SiteId, new CloudCostEntry(monthStart.AddMonths(-1).AddDays(14), CloudProvider.Azure, CloudServiceCategory.Compute, "Virtual Machines", "vm-1", 100m, "USD")));
                await records.AddAsync(CloudCostRecordAggregate.Record(Guid.NewGuid(), tenantId, alias.SiteId, new CloudCostEntry(monthStart, CloudProvider.Microsoft365, CloudServiceCategory.Licensing, "Microsoft 365 E5", "tenant", 150m, "USD")));
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return alias.SiteId;
            });

        // The host's own scheduler runs this job 0-10 s after it starts (a new job's first run is now), beside the run these tests
        // make by hand - measured in a fast gate (2026-10-10): both passes raised site B's two recommendations, 4 where 2 were
        // expected. Stopped before anything is seeded, it has run on nothing or not at all; production has only the scheduler's.
        private static async Task StopTheSchedulersAdvisorAsync(WebApplication app) =>
            await app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<CostOptimizationAdvisorJob>().Single().StopAsync(CancellationToken.None);

        private static async Task<string> RunJobAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var notes = string.Empty;
            foreach (var tenant in await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
            {
                scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes += await new CostOptimizationAdvisorJob(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);
            }

            return notes;
        }

        /// <summary>A second site on the SAME tenant, through the call the Host's "Add Site" page makes (MeteringSiteIsolationTests').</summary>
        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Cost Optimization Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = true
                });

                return site;
            });

        private sealed class FailingSite
        {
            public int SiteId { get; set; }
        }

        // Fails the pass of ONE site - the fault the job must contain.
        private sealed class FailingSiteAnalyzer(FailingSite failing, SiteState siteState) : IRecommendationAnalyzer
        {
            public Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default) =>
                siteState.Alias?.SiteId == failing.SiteId
                    ? throw new InvalidOperationException("A planted analyzer failure for one site.")
                    : Task.FromResult<IReadOnlyList<RecommendationDraft>>(Array.Empty<RecommendationDraft>());
        }
    }
}
