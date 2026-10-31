#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Retention periods a privacy policy can promise (BL-275, the website's privacy review), driven through the daily
    /// <c>PurgeJob</c> on an installed site with metering OFF - the stock default, where the rollup job does not run:
    /// AI usage rows older than <c>MeteringUsageRetention</c> keep their counts and lose their user, and a user's
    /// <c>LastIPAddress</c> older than <c>LastIPAddressRetention</c> is cleared. Rows and users inside the period are
    /// untouched.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PrivacyRetentionTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_purge_job_takes_the_user_out_of_old_ai_usage_and_clears_old_ip_addresses()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var now = DateTime.UtcNow;
                var (siteId, hostId) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    // Two AI calls by the host user: one past the 90-day default, one inside it.
                    var hostUser = await sp.GetRequiredService<IUserRepository>().GetUserAsync(UserNames.Host);
                    var ledger = sp.GetRequiredService<IAiUsageRecordRepository>();
                    var tenant = FrameworkTenantId.ToTenantId(alias.TenantId);
                    await ledger.AddAsync(Call(tenant, alias.SiteId, hostUser.UserId, now.AddDays(-120)));
                    await ledger.AddAsync(Call(tenant, alias.SiteId, hostUser.UserId, now.AddDays(-5)));
                    await sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();

                    // The host signed in from an address 100 days ago - past the 90-day default.
                    hostUser.LastIPAddress = "203.0.113.7";
                    hostUser.LastLoginOn = now.AddDays(-100);
                    await sp.GetRequiredService<IUserRepository>().UpdateUserAsync(hostUser);
                    return (alias.SiteId, hostUser.UserId);
                });

                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) => new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                output.WriteLine("[purge job log] " + log);

                var rows = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IAiUsageRecordRepository>().ListBySiteAsync(siteId));
                Assert.Equal(2, rows.Count); // the count survives
                Assert.Null(rows.Single(row => row.OccurredOnUtc < now.AddDays(-100)).UserId);
                Assert.Equal(hostId, rows.Single(row => row.OccurredOnUtc > now.AddDays(-10)).UserId);

                var user = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IUserRepository>().GetUserAsync(UserNames.Host));
                Assert.Equal(string.Empty, user.LastIPAddress);
                Assert.NotNull(user.LastLoginOn); // when, not where, stays

                // A fresh sign-in's address is inside the period and survives the next run.
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var fresh = await sp.GetRequiredService<IUserRepository>().GetUserAsync(UserNames.Host);
                    fresh.LastIPAddress = "198.51.100.9";
                    fresh.LastLoginOn = now.AddDays(-1);
                    await sp.GetRequiredService<IUserRepository>().UpdateUserAsync(fresh);
                    return 0;
                });
                await GateDrive.InTenantAsync(app, Host, (sp, _) => new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                var kept = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IUserRepository>().GetUserAsync(UserNames.Host));
                Assert.Equal("198.51.100.9", kept.LastIPAddress);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static AiUsageRecordAggregate Call(Stomme.Tenancy.Domain.TenantId tenant, int siteId, int userId, DateTime occurredOnUtc) =>
            AiUsageRecordAggregate.Append(Guid.NewGuid(), tenant, siteId, "test-provider", "test-model", "assistant", 100, 50, 10,
                AiCallStatus.Success, 0.01m, "USD", null, userId, Guid.NewGuid(), occurredOnUtc);
    }
}
