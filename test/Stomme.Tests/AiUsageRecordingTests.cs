#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The AI meter, driven on an installed site.
    /// </summary>
    /// <remarks>
    /// Until recently the AI recorder had a port-shape test and nothing else: no case recorded a call. Once the recorder
    /// had cases, they showed that one rule had been lost: a CACHE HIT was priced, and charged to the token quota, like
    /// a call the provider was paid for. The
    /// price list comes from the override FILE here, as a deployed install's does.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class AiUsageRecordingTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public AiUsageRecordingTests(ITestOutputHelper output) => _output = output;

        private static JsonObject Settings() => new()
        {
            ["Metering"] = new JsonObject
            {
                ["Enabled"] = true,
                ["AiPricing"] = new JsonObject
                {
                    ["testprovider"] = new JsonObject
                    {
                        ["priced-model"] = new JsonObject { ["InputPerMillion"] = 2, ["OutputPerMillion"] = 8, ["Currency"] = "eur" },
                    },
                },
            },
        };

        [Fact]
        public async Task A_call_is_priced_by_the_SERVER_recorded_once_and_forwarded_to_both_shared_metrics()
        {
            await WithInstallAsync(async (app, siteId) =>
            {
                var operation = Guid.NewGuid();
                var call = new AiUsageEvent("TestProvider", "Priced-Model", "summariser", 500_000, 250_000, 120, AiCallStatus.Success, operation, 0.9);

                var first = await RecordAsync(app, call);
                var repeat = await RecordAsync(app, call);
                _output.WriteLine($"[first] recorded={first.Recorded} cost={first.EstimatedCost} {first.CurrencyCode} idempotent={first.Idempotent}; [repeat] idempotent={repeat.Idempotent} cost={repeat.EstimatedCost}");

                // 0.5 M in at 2 + 0.25 M out at 8 = 1 + 2. The caller supplied no price; there is nowhere to put one.
                Assert.True(first.Recorded);
                Assert.False(first.Idempotent);
                Assert.Equal(3m, first.EstimatedCost);
                Assert.Equal("EUR", first.CurrencyCode);
                Assert.True(repeat.Idempotent);
                Assert.Equal(3m, repeat.EstimatedCost);

                var rows = await AiLedgerAsync(app, siteId);
                var row = Assert.Single(rows);
                Assert.Equal("TestProvider", row.Provider);
                Assert.Equal("summariser", row.Agent);
                Assert.Equal(750_000, row.BillableTokens());
                Assert.Equal(3m, row.EstimatedCost);
                // Outside a request there is no user, and the recorder does not invent one.
                Assert.Null(row.UserId);

                // Both shared metrics, ONCE each despite the repeat - same operation, different metric, different row.
                var ledger = await LedgerAsync(app, siteId);
                Assert.Equal(1, Assert.Single(ledger, r => r.Metric == AiUsageRecorder.CallsMetric).Quantity);
                Assert.Equal(750_000, Assert.Single(ledger, r => r.Metric == AiUsageRecorder.TokensMetric).Quantity);
                Assert.All(ledger, r => Assert.Equal("testprovider", r.Source));
            });
        }

        [Fact]
        public async Task A_CACHE_HIT_is_a_call_that_costs_nothing_and_burns_no_tokens()
        {
            await WithInstallAsync(async (app, siteId) =>
            {
                var outcome = await RecordAsync(app,
                    new AiUsageEvent("testprovider", "priced-model", "summariser", 500_000, 250_000, 3, AiCallStatus.Cached, Guid.NewGuid()));
                _output.WriteLine($"[cached] cost={outcome.EstimatedCost} {outcome.CurrencyCode}");

                Assert.True(outcome.Recorded);
                Assert.Equal(0m, outcome.EstimatedCost);

                var row = Assert.Single(await AiLedgerAsync(app, siteId));
                Assert.Equal(AiCallStatus.Cached, row.Status);
                Assert.Equal(0m, row.EstimatedCost);
                Assert.Equal(0, row.BillableTokens());
                // The raw counts are KEPT: they say how much the cache saved.
                Assert.Equal(500_000, row.InputTokens);
                Assert.Equal(250_000, row.OutputTokens);

                // It is still a call - and no token usage was forwarded, so a token quota is not charged for it.
                var ledger = await LedgerAsync(app, siteId);
                Assert.Single(ledger, r => r.Metric == AiUsageRecorder.CallsMetric);
                Assert.DoesNotContain(ledger, r => r.Metric == AiUsageRecorder.TokensMetric);

                // ...and the ROLLUP agrees with the row: one call, no billable tokens, no cost.
                await RecordAsync(app, new AiUsageEvent("testprovider", "priced-model", "summariser", 1_000_000, 0, 50, AiCallStatus.Success, Guid.NewGuid()));
                var notes = await RunRollupAsync(app);
                _output.WriteLine($"[rollup] {notes}");

                var dashboard = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new GetAiUsageDashboardQueryHandler(
                        sp.GetRequiredService<IAiUsageRollupRepository>(), sp.GetRequiredService<Stomme.Shared.SiteState>(), sp.GetRequiredService<IMeteringClock>())
                        .Handle(new GetAiUsageDashboardQuery(30), default));
                _output.WriteLine($"[dashboard] calls={dashboard.Calls} billable={dashboard.BillableTokens} cost={dashboard.EstimatedCost} {dashboard.CurrencyCode}");

                Assert.Equal(2, dashboard.Calls);
                Assert.Equal(1_000_000, dashboard.BillableTokens);
                Assert.Equal(2m, dashboard.EstimatedCost);
            });
        }

        [Fact]
        public async Task An_UNPRICED_model_is_recorded_at_zero_in_the_default_currency_and_an_ERROR_is_counted_as_one()
        {
            await WithInstallAsync(async (app, siteId) =>
            {
                var unpriced = await RecordAsync(app,
                    new AiUsageEvent("testprovider", "some-new-model", "a", 1_000, 1_000, 10, AiCallStatus.Error, Guid.NewGuid()));

                Assert.True(unpriced.Recorded);
                Assert.Equal(0m, unpriced.EstimatedCost);
                Assert.Equal(AiCostCalculator.DefaultCurrency, unpriced.CurrencyCode);

                var row = Assert.Single(await AiLedgerAsync(app, siteId));
                Assert.Equal(AiCallStatus.Error, row.Status);
                // An error still burned the tokens it burned.
                Assert.Equal(2_000, row.BillableTokens());

                // Nonsense is refused rather than recorded.
                await Assert.ThrowsAnyAsync<ArgumentException>(() => RecordAsync(app, new AiUsageEvent(" ", "m", "a", 1, 1, 1, AiCallStatus.Success, Guid.NewGuid())));
                await Assert.ThrowsAnyAsync<ArgumentException>(() => RecordAsync(app, new AiUsageEvent("p", "", "a", 1, 1, 1, AiCallStatus.Success, Guid.NewGuid())));
            });
        }

        private async Task WithInstallAsync(Func<WebApplication, int, Task> drive)
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, Settings());
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                await drive(app, (await GateDrive.AliasAsync(app, Host)).SiteId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<AiUsageOutcome> RecordAsync(WebApplication app, AiUsageEvent call) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IAiUsageRecorder>().RecordAsync(call));

        private static Task<System.Collections.Generic.IReadOnlyList<Stomme.Modules.Metering.Domain.AiUsageRecordAggregate>> AiLedgerAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IAiUsageRecordRepository>().ListBySiteAsync(siteId));

        private static Task<System.Collections.Generic.IReadOnlyList<Stomme.Modules.Metering.Domain.UsageRecordAggregate>> LedgerAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId));

        private static async Task<string> RunRollupAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var notes = string.Empty;
            foreach (var tenant in await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
            {
                scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes += await new UsageRollupJob(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);
            }

            return notes;
        }
    }
}
