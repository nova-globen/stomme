#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.CostOptimization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// The cases of <c>CostOptimizationSignalSourceTests.CloudCostSignalSource_*</c>, by name: CloudUsage's
    /// side of CostOptimization's read port, through the module's own startup. The port's own: another site's spend is not
    /// this site's signal.
    /// </summary>
    public sealed class CloudCostSignalSourceTests
    {
        // The harness clock is 2026-07-15: this month is 2026-07, the previous 2026-06.
        [Fact]
        public async Task CloudCostSignalSource_ProjectsMonthOverMonthPerProviderAndTheServiceBreakdown()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 1), 400m, provider: CloudProvider.Azure, service: "Virtual Machines", category: CloudServiceCategory.Compute);
            await harness.SpendAsync(new DateOnly(2026, 6, 15), 250m, provider: CloudProvider.Azure, service: "Virtual Machines", category: CloudServiceCategory.Compute);
            await harness.SpendAsync(new DateOnly(2026, 7, 1), 150m, provider: CloudProvider.Microsoft365, service: "Microsoft 365 E5", category: CloudServiceCategory.Licensing);
            await harness.SpendAsync(new DateOnly(2026, 5, 15), 999m, provider: CloudProvider.Azure, service: "Virtual Machines", category: CloudServiceCategory.Compute);

            var signal = await harness.InScopeAsync(sp => sp.GetRequiredService<ICloudCostSignalSource>().GetCurrentMonthSpendAsync());

            Assert.Equal(("2026-07", "2026-06"), (signal.Month, signal.PreviousMonth));
            Assert.Equal(2, signal.ByProvider.Count);
            var azure = Assert.Single(signal.ByProvider, p => p.Provider == "Azure");
            Assert.Equal(("USD", 400m, 250m), (azure.Currency, azure.CurrentMonthTotal, azure.PreviousMonthTotal)); // two months ago stays out of the baseline
            var licensing = Assert.Single(signal.ByService, s => s.ServiceCategory == "Licensing");
            Assert.Equal(("Microsoft 365 E5", 150m), (licensing.Service, licensing.CurrentMonthTotal));
            Assert.DoesNotContain(signal.ByService, s => s.CurrentMonthTotal == 250m); // per-service is this month only
        }

        [Fact]
        public async Task Another_sites_spend_is_not_this_sites_signal()
        {
            await using var harness = await CloudUsageApplicationHarness.StartAsync();
            await harness.SpendAsync(new DateOnly(2026, 7, 1), 400m, siteId: CloudUsageApplicationHarness.SiteId + 1);

            var signal = await harness.InScopeAsync(sp => sp.GetRequiredService<ICloudCostSignalSource>().GetCurrentMonthSpendAsync());

            Assert.Empty(signal.ByProvider);
            Assert.Empty(signal.ByService);
        }
    }
}
