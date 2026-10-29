#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Security.Abstractions;
using Stomme.CloudUsage;
using Stomme.Connectivity;
using Stomme.Datasets;
using Stomme.Models.Notifications;
using Stomme.Modules.CloudUsage.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// The <c>SyncCloudCostsCommandHandlerTests</c> cases, by name, through the mediator and the module's
    /// own startup: a stubbed payload feeds the shared sync path, rows land in the SITE's ledger, a refusal is a code on the
    /// view, a named dataset materialises with the connection as its origin. The port's own: a connection of another
    /// connector type, a connection without its token, a fetch failure reported as a category and never the exception's
    /// words, and no sync without a resolved site.
    /// </summary>
    public sealed class SyncCloudCostsCommandHandlerTests
    {
        [Fact]
        public async Task Handle_SyncsStubbedPayload_IntoLedger_AndReportsRowCount()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.True(result.Success, result.Reason);
            Assert.Equal(CloudProvider.Azure, result.Provider);
            Assert.Equal(2, result.SyncedRows);
            Assert.Null(result.Dataset);
            var ledger = await harness.LedgerAsync();
            Assert.Equal(2, ledger.Count);
            Assert.Contains(ledger, r => r.Service == "Virtual Machines" && r.Cost == 12.34m);
            Assert.All(ledger, r => Assert.Equal(CloudUsageApplicationHarness.SiteId, r.SiteId));
            Assert.Empty(harness.Registrar.Registrations); // no target dataset named, nothing materialises
            Assert.NotNull(harness.Payload.LastQuery?.Egress); // the fetch was handed the host's egress
            Assert.Equal("sp-token", harness.Payload.LastQuery!.ConnectionSettings[Modules.CloudUsage.Providers.ConnectorCostPayloadSource.CredentialSettingKey]);
        }

        [Fact]
        public async Task Handle_DailyWindowByDefault_MonthlyOnRequest()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();

            var daily = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));
            Assert.True(daily.Success);
            Assert.Equal((new DateOnly(2026, 7, 14), new DateOnly(2026, 7, 14)), (daily.From, daily.To));

            var monthly = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure", Window: "monthly"));
            Assert.True(monthly.Success);
            Assert.Equal((new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)), (monthly.From, monthly.To));
            Assert.Equal(new DateOnly(2026, 6, 1), harness.Payload.LastQuery!.From);
        }

        [Fact]
        public async Task Handle_WithTargetDataset_MaterializesItWithTheConnectionAsOrigin()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            var connectionId = Guid.NewGuid();

            var result = await harness.SendAsync(new SyncCloudCostsCommand(connectionId, "azure", TargetDataset: "Azure manual sync"));

            Assert.True(result.Success, result.Reason);
            Assert.Equal("Azure manual sync", result.Dataset);
            var registration = Assert.Single(harness.Registrar.Registrations);
            Assert.Equal(("Azure manual sync", OriginKind.ExternalQuery, connectionId.ToString()), (registration.Name, registration.OriginKind, registration.OriginReference));
            Assert.Equal(DataClassification.Internal, registration.Classification); // the classifier may escalate it, never lower it
        }

        [Theory]
        [InlineData("orangecloud", "PROVIDER_UNKNOWN")]
        [InlineData("gcp", "PROVIDER_UNSUPPORTED")] // no GCP adapter registered in this harness
        public async Task Handle_ProviderProblems_FailWithTheSyncServicesStructuredCodes(string provider, string expectedCode)
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync(onlyAzure: true);

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), provider));

            Assert.False(result.Success);
            Assert.Equal(expectedCode, result.Code);
            Assert.Empty(await harness.LedgerAsync());
        }

        [Fact]
        public async Task Handle_UnresolvableConnection_FailsConnectionNotFound()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            harness.Connections.Connection = null;

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "CONNECTION_NOT_FOUND"), (result.Success, result.Code));
        }

        [Fact]
        public async Task Handle_MalformedPayload_FailsPayloadInvalid_WithoutPersisting()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            harness.Payload.Payload = """{ "unexpected": true }""";

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "PAYLOAD_INVALID"), (result.Success, result.Code));
            Assert.Empty(await harness.LedgerAsync());
        }

        [Fact]
        public void SyncCloudCostsCommand_RequiresManagePermission()
        {
            var requires = typeof(SyncCloudCostsCommand).GetCustomAttributes(typeof(RequiresAttribute), inherit: false).Cast<RequiresAttribute>().Single();

            Assert.Equal(CloudUsagePermissions.Manage, requires.Permission);
        }

        [Fact]
        public async Task A_connection_of_another_connector_type_is_refused_by_name_and_nothing_is_fetched()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            harness.Connections.Connection = CloudUsageApplicationHarness.AzureConnection(type: "rest");

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "CONNECTION_TYPE_MISMATCH"), (result.Success, result.Code));
            Assert.Contains("azurecost", result.Reason);
            Assert.Null(harness.Payload.LastQuery);
        }

        [Fact]
        public async Task A_connection_without_its_token_is_refused_and_says_what_is_not_supported()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            harness.Connections.Connection = CloudUsageApplicationHarness.AzureConnection(secret: null);

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "CREDENTIAL_MISSING"), (result.Success, result.Code));
            Assert.Contains("SigV4", result.Reason);
            Assert.Null(harness.Payload.LastQuery);
        }

        [Theory]
        [InlineData("status")]
        [InlineData("denied")]
        [InlineData("timeout")]
        public async Task A_fetch_failure_is_a_category_and_never_the_exceptions_words(string kind)
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            const string secretBearingMessage = "GET https://management.azure.com/subscriptions/sub-123?token=sp-token failed";
            harness.Payload.Throw = kind switch
            {
                "status" => new HttpRequestException(secretBearingMessage, null, HttpStatusCode.Forbidden),
                "denied" => new ConnectorEgressDeniedException(secretBearingMessage),
                _ => new TaskCanceledException(secretBearingMessage),
            };

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "FETCH_FAILED"), (result.Success, result.Code));
            Assert.DoesNotContain("sub-123", result.Reason);
            Assert.DoesNotContain("sp-token", result.Reason);
            Assert.Contains(kind switch { "status" => "403", "denied" => "egress refused", _ => "did not answer" }, result.Reason);
            Assert.Empty(await harness.LedgerAsync());
        }

        [Fact]
        public async Task Without_a_resolved_site_nothing_is_synced()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync(withSite: false);

            var result = await harness.SendAsync(new SyncCloudCostsCommand(Guid.NewGuid(), "azure"));

            Assert.Equal((false, "SITE_REQUIRED"), (result.Success, result.Code));
            Assert.Null(harness.Payload.LastQuery);
        }
    }

    /// <summary>The command-handler half of the <c>CloudBudgetTests</c> cases, by name: one budget per scope, the second set amends it.</summary>
    public sealed class CloudBudgetCommandTests
    {
        [Fact]
        public async Task Handle_CreatesTheBudget_ThenAmendsTheSameScopeInsteadOfDuplicating()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();

            var created = await harness.SendAsync(new SetCloudBudgetCommand(100m, "USD"));
            Assert.Equal(100m, created.MonthlyAmount);
            Assert.Null(created.Provider);

            var amended = await harness.SendAsync(new SetCloudBudgetCommand(150m, "USD"));
            Assert.Equal(created.BudgetId, amended.BudgetId);
            Assert.Equal(150m, amended.MonthlyAmount);
            Assert.Equal(150m, Assert.Single(await harness.SendAsync(new ListCloudBudgetsQuery())).MonthlyAmount);
        }

        [Fact]
        public async Task Handle_ProviderScopedBudget_IsItsOwnScope()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();

            var overall = await harness.SendAsync(new SetCloudBudgetCommand(200m, "USD"));
            var azure = await harness.SendAsync(new SetCloudBudgetCommand(80m, "USD", CloudProvider.Azure));

            Assert.NotEqual(overall.BudgetId, azure.BudgetId);
            Assert.Equal(CloudProvider.Azure, azure.Provider);
            Assert.Equal(2, (await harness.SendAsync(new ListCloudBudgetsQuery())).Count);
        }

        [Fact]
        public void SetCloudBudgetCommand_RequiresManagePermission()
        {
            var requires = typeof(SetCloudBudgetCommand).GetCustomAttributes(typeof(RequiresAttribute), inherit: false).Cast<RequiresAttribute>().Single();

            Assert.Equal(CloudUsagePermissions.Manage, requires.Permission);
        }
    }

    /// <summary>The <c>CloudUsageQueryTests</c> cases, by name, through the mediator, plus a site's rollups never counting another site's rows.</summary>
    public sealed class CloudUsageQueryTests
    {
        [Fact]
        public async Task Summary_GroupsTotalsPerCurrency_AndCountsRecords()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 10m, provider: CloudProvider.Azure);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 5m, provider: CloudProvider.Aws);
            await harness.SpendAsync(new DateOnly(2026, 7, 12), 7m, currency: "EUR", provider: CloudProvider.Gcp);

            var view = await harness.SendAsync(new GetCloudCostSummaryQuery());

            Assert.Equal(3, view.RecordCount);
            Assert.Equal(2, view.TotalsByCurrency.Count);
            Assert.Equal(7m, Assert.Single(view.TotalsByCurrency, t => t.Currency == "EUR").TotalCost);
            Assert.Equal(15m, Assert.Single(view.TotalsByCurrency, t => t.Currency == "USD").TotalCost);
        }

        [Fact]
        public async Task Summary_HonorsTheDateWindow()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 6, 30), 100m);
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 10m);
            await harness.SpendAsync(new DateOnly(2026, 8, 1), 100m);

            var view = await harness.SendAsync(new GetCloudCostSummaryQuery(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));

            Assert.Equal(1, view.RecordCount);
            Assert.Equal(10m, Assert.Single(view.TotalsByCurrency).TotalCost);
        }

        [Fact]
        public async Task ByProvider_GroupsPerProviderAndCurrency()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 10m);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 2m);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 4m, currency: "EUR");
            await harness.SpendAsync(new DateOnly(2026, 7, 12), 5m, provider: CloudProvider.Aws);

            var view = await harness.SendAsync(new GetCloudCostByProviderQuery());

            Assert.Equal(3, view.ByProvider.Count);
            var azureUsd = Assert.Single(view.ByProvider, b => b.Provider == CloudProvider.Azure && b.Currency == "USD");
            Assert.Equal((12m, 2L), (azureUsd.TotalCost, azureUsd.RecordCount));
            Assert.Equal(4m, Assert.Single(view.ByProvider, b => b.Provider == CloudProvider.Azure && b.Currency == "EUR").TotalCost);
            Assert.Equal(5m, Assert.Single(view.ByProvider, b => b.Provider == CloudProvider.Aws).TotalCost);
        }

        [Fact]
        public async Task ByService_ReturnsTopNByCost_WithCategoryAndRawService()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 50m, service: "Virtual Machines", category: CloudServiceCategory.Compute);
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 30m, service: "Storage", category: CloudServiceCategory.Storage);
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 20m, provider: CloudProvider.Aws, service: "AmazonRDS", category: CloudServiceCategory.Database);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 25m, service: "Virtual Machines", category: CloudServiceCategory.Compute);

            var view = await harness.SendAsync(new GetCloudCostByServiceQuery(Top: 2));

            Assert.Equal(2, view.ByService.Count);
            Assert.Equal(("Virtual Machines", CloudServiceCategory.Compute, 75m), (view.ByService[0].Service, view.ByService[0].ServiceCategory, view.ByService[0].TotalCost));
            Assert.Equal(("Storage", 30m), (view.ByService[1].Service, view.ByService[1].TotalCost));
        }

        [Fact]
        public async Task MonthOverMonth_ComputesDeltaAndPercent_PerCurrency()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 6, 10), 100m);
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 150m);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 9m, currency: "EUR", provider: CloudProvider.Gcp);

            var view = await harness.SendAsync(new GetCloudCostMonthOverMonthQuery());

            Assert.Equal(("2026-07", "2026-06"), (view.Month, view.PreviousMonth));
            var usd = Assert.Single(view.ByCurrency, d => d.Currency == "USD");
            Assert.Equal((150m, 100m, 50m, (decimal?)50m), (usd.CurrentTotal, usd.PreviousTotal, usd.Delta, usd.DeltaPercent));
            var eur = Assert.Single(view.ByCurrency, d => d.Currency == "EUR");
            Assert.Equal(9m, eur.Delta);
            Assert.Null(eur.DeltaPercent); // started this month: honestly null, not infinity
        }

        [Fact]
        public async Task MonthOverMonth_HonorsAnExplicitMonth()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 5, 10), 40m);
            await harness.SpendAsync(new DateOnly(2026, 6, 10), 30m);

            var view = await harness.SendAsync(new GetCloudCostMonthOverMonthQuery(new DateOnly(2026, 6, 15)));

            Assert.Equal(("2026-06", "2026-05"), (view.Month, view.PreviousMonth));
            var usd = Assert.Single(view.ByCurrency);
            Assert.Equal((-10m, (decimal?)-25m), (usd.Delta, usd.DeltaPercent));
        }

        [Fact]
        public async Task BudgetStatus_WithNoBudgetSet_ReturnsAnEmptyView()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 10m);

            var view = await harness.SendAsync(new GetCloudCostBudgetStatusQuery());

            Assert.Equal("2026-07", view.Month);
            Assert.Empty(view.Budgets);
        }

        [Fact]
        public async Task BudgetStatus_ComputesActualRemainingAndPercent_InTheBudgetsCurrencyOnly()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 30m);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 10m, provider: CloudProvider.Aws);
            await harness.SpendAsync(new DateOnly(2026, 7, 12), 999m, currency: "EUR", provider: CloudProvider.Gcp); // other currency, never converted in
            await harness.SpendAsync(new DateOnly(2026, 6, 30), 999m); // previous month, out of window
            await harness.BudgetAsync(100m);

            var line = Assert.Single((await harness.SendAsync(new GetCloudCostBudgetStatusQuery())).Budgets);

            Assert.Null(line.Provider);
            Assert.Equal(("USD", 100m, 40m, 60m, 40m), (line.Currency, line.BudgetAmount, line.ActualSpend, line.RemainingAmount, line.PercentUsed));
        }

        [Fact]
        public async Task BudgetStatus_ProviderScopedBudget_CountsOnlyThatProvidersSpend()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 80m);
            await harness.SpendAsync(new DateOnly(2026, 7, 11), 10m, provider: CloudProvider.Aws);
            await harness.BudgetAsync(200m);
            await harness.BudgetAsync(100m, provider: CloudProvider.Azure);

            var view = await harness.SendAsync(new GetCloudCostBudgetStatusQuery());

            Assert.Equal(2, view.Budgets.Count);
            Assert.Null(view.Budgets[0].Provider); // the overall line first, provider-scoped lines after
            Assert.Equal(90m, view.Budgets[0].ActualSpend);
            Assert.Equal((CloudProvider?)CloudProvider.Azure, view.Budgets[1].Provider);
            Assert.Equal((80m, 80m, 20m), (view.Budgets[1].ActualSpend, view.Budgets[1].PercentUsed, view.Budgets[1].RemainingAmount));
        }

        [Fact]
        public async Task A_sites_rollups_never_count_another_sites_rows_or_budgets()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 10m);
            await harness.SpendAsync(new DateOnly(2026, 7, 10), 500m, siteId: CloudUsageApplicationHarness.SiteId + 1);
            await harness.BudgetAsync(50m, siteId: CloudUsageApplicationHarness.SiteId + 1);

            var summary = await harness.SendAsync(new GetCloudCostSummaryQuery());
            var status = await harness.SendAsync(new GetCloudCostBudgetStatusQuery());

            Assert.Equal(10m, Assert.Single(summary.TotalsByCurrency).TotalCost);
            Assert.Empty(status.Budgets);
        }
    }

    /// <summary>
    /// The <c>CloudCostAlertServiceTests</c> cases, by name, on the site-scoped service. The
    /// recipients are the site's administrators by user id (never a configured address list), the alerts default OFF, and
    /// the job is a framework job gated by <c>CloudUsage:Alerts:Enabled</c>.
    /// </summary>
    public sealed class CloudCostAlertServiceTests
    {
        private static readonly DateOnly LatestFullDay = new(2026, 7, 14);

        // Spend early in the month, outside the anomaly baseline window (2026-07-07..13), so budget fixtures never trip the anomaly rule.
        private static Task MonthSpendAsync(CloudUsageApplicationHarness harness, decimal cost, CloudProvider provider = CloudProvider.Azure) =>
            harness.SpendAsync(new DateOnly(2026, 7, 2), cost, provider: provider);

        [Fact]
        public async Task Budget_SpendCrossing80Percent_DispatchesWarning_OncePerMonth()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 850m);

            Assert.Equal(1, await harness.EvaluateAlertsAsync());

            var request = Assert.Single(harness.Dispatcher.Requests);
            Assert.Equal((CloudCostAlertService.BudgetTemplateKey, NotificationSeverity.Warning, CloudCostAlertService.Topic), (request.TemplateKey, request.Severity, request.Topic));
            Assert.Contains(request.Recipients, r => r is { Kind: NotificationRecipientKind.User, Value: "5" });
            Assert.Contains(NotificationChannelKind.InApp, request.Channels);
            Assert.Contains(request.Parameters!, p => p is { Name: "threshold", Value: "80" });
            Assert.Contains(request.Parameters!, p => p is { Name: "month", Value: "2026-07" });
            Assert.Contains(request.Parameters!, p => p is { Name: "currency", Value: "USD" });
            Assert.Equal(0, await harness.EvaluateAlertsAsync()); // the same month dispatches nothing more
            Assert.Single(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Budget_SpendCrossing100Percent_DispatchesOneCritical_AndMarksBothThresholds()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 1050m);

            Assert.Equal(1, await harness.EvaluateAlertsAsync());

            var request = Assert.Single(harness.Dispatcher.Requests);
            Assert.Equal(NotificationSeverity.Critical, request.Severity);
            Assert.Contains(request.Parameters!, p => p is { Name: "threshold", Value: "100" });
            Assert.Equal(0, await harness.EvaluateAlertsAsync()); // both markers written: no stale 80 % warning later
            Assert.Single(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Budget_WarningThenCritical_DispatchesEachThresholdOnceAcrossTheMonth()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 850m);

            Assert.Equal(1, await harness.EvaluateAlertsAsync());
            Assert.Equal(NotificationSeverity.Warning, Assert.Single(harness.Dispatcher.Requests).Severity);

            await MonthSpendAsync(harness, 200m); // 105 % now
            Assert.Equal(1, await harness.EvaluateAlertsAsync());
            Assert.Equal(2, harness.Dispatcher.Requests.Count);
            Assert.Equal(NotificationSeverity.Critical, harness.Dispatcher.Requests[1].Severity);
            Assert.Contains(harness.Dispatcher.Requests[1].Parameters!, p => p is { Name: "threshold", Value: "100" });
        }

        [Fact]
        public async Task Budget_SpendBelow80Percent_DispatchesNothing()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 500m);

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Budget_HonorsCurrencyAndProviderScope()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m, provider: CloudProvider.Azure);
            await MonthSpendAsync(harness, 700m, CloudProvider.Azure);
            await MonthSpendAsync(harness, 900m, CloudProvider.Aws);
            await harness.SpendAsync(new DateOnly(2026, 7, 2), 900m, currency: "EUR");

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Budget_NoneSet_DispatchesNoBudgetAlert()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await MonthSpendAsync(harness, 10_000m);

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Anomaly_SpikeAboveRollingAverage_DispatchesWarning_OncePerDay()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            for (var d = 7; d <= 13; d++)
            {
                await harness.SpendAsync(new DateOnly(2026, 7, d), 10m); // a rolling average of 10
            }

            await harness.SpendAsync(LatestFullDay, 13m); // +30 %

            Assert.Equal(1, await harness.EvaluateAlertsAsync());

            var request = Assert.Single(harness.Dispatcher.Requests);
            Assert.Equal((CloudCostAlertService.AnomalyTemplateKey, NotificationSeverity.Warning, CloudCostAlertService.Topic), (request.TemplateKey, request.Severity, request.Topic));
            Assert.Contains(request.Parameters!, p => p is { Name: "date", Value: "2026-07-14" });
            Assert.Contains(request.Parameters!, p => p is { Name: "currency", Value: "USD" });
            Assert.Contains(request.Parameters!, p => p is { Name: "lookbackDays", Value: "7" });
            Assert.Equal(0, await harness.EvaluateAlertsAsync()); // the same day dispatches nothing more
            Assert.Single(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Anomaly_SpikeWithinThreshold_DispatchesNothing()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            for (var d = 7; d <= 13; d++)
            {
                await harness.SpendAsync(new DateOnly(2026, 7, d), 10m);
            }

            await harness.SpendAsync(LatestFullDay, 11m); // +10 %, below the default 20 %

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Anomaly_NoBaselineSpend_DispatchesNothing()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(LatestFullDay, 500m); // a first-ever cost day has no average to compare

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Anomaly_HonorsConfiguredThreshold()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            for (var d = 7; d <= 13; d++)
            {
                await harness.SpendAsync(new DateOnly(2026, 7, d), 10m);
            }

            await harness.SpendAsync(LatestFullDay, 11.5m); // +15 %

            Assert.Equal(1, await harness.EvaluateAlertsAsync(new CloudUsageAlertOptions { Enabled = true, AnomalyThresholdPercent = 10m }));
            Assert.Equal(CloudCostAlertService.AnomalyTemplateKey, Assert.Single(harness.Dispatcher.Requests).TemplateKey);
        }

        [Fact]
        public async Task Evaluate_WithoutRecipients_DispatchesNothing()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            harness.Administrators.UserIds = Array.Empty<int>();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 2000m);

            Assert.Equal(0, await harness.EvaluateAlertsAsync());
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Evaluate_WhenAlertingDisabled_DispatchesNothing()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 2000m);

            Assert.Equal(0, await harness.EvaluateAlertsAsync(new CloudUsageAlertOptions { Enabled = false }));
            Assert.Empty(harness.Dispatcher.Requests);
        }

        [Fact]
        public void AddCloudUsage_RegistersAlertJobAndDefaults()
        {
            var defaults = CloudUsageAlertOptions.From(configuration: null);

            Assert.False(defaults.Enabled); // Off by default - a stock install gains no alert work
            Assert.Equal((7, 20m), (defaults.AnomalyLookbackDays, defaults.AnomalyThresholdPercent));
            Assert.True(typeof(Stomme.Infrastructure.HostedServiceBase).IsAssignableFrom(typeof(CloudCostAlertJob))); // found by the framework's job scan
            var job = new CloudCostAlertJob(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
            Assert.Equal(("Cloud Cost Alert Job", "d", 1), (job.Name, job.Frequency, job.Interval));
        }

        [Fact]
        public async Task A_failed_dispatch_is_retried_by_the_next_pass_because_it_left_no_marker()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 850m);
            harness.Dispatcher.Fail = true;

            Assert.Equal(0, await harness.EvaluateAlertsAsync());

            harness.Dispatcher.Fail = false;
            Assert.Equal(1, await harness.EvaluateAlertsAsync());
            Assert.Single(harness.Dispatcher.Requests);
        }

        [Fact]
        public async Task Another_sites_spend_and_markers_do_not_touch_this_sites_alerts()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.BudgetAsync(1000m, siteId: CloudUsageApplicationHarness.SiteId + 1);
            await harness.SpendAsync(new DateOnly(2026, 7, 2), 5000m, siteId: CloudUsageApplicationHarness.SiteId + 1);
            await harness.BudgetAsync(1000m);
            await MonthSpendAsync(harness, 100m);

            Assert.Equal(0, await harness.EvaluateAlertsAsync()); // this site is at 10 %; the other site's 500 % is not ours
            Assert.Equal(1, await harness.EvaluateAlertsAsync(siteId: CloudUsageApplicationHarness.SiteId + 1));
        }
    }
}
