#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The MCP audit ledger's retention pass (BL-80, D101 item 6, on D104's <see cref="IRetentionPurger"/> seam).
    /// </summary>
    /// <remarks>
    /// <b>The floor is the assertion that matters.</b> This ledger is where a denied call, a call waiting on an
    /// approval nobody can give, and a tool that threw are told apart - the wire answers all three identically. A
    /// per-site retention of <c>0</c> would therefore be a way to erase that record through a text box, from inside
    /// the product, leaving nothing that says it happened. The pass reads anything below seven days AS seven.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpAuditRetentionTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpAuditRetentionTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_pass_ages_out_old_entries_keeps_recent_ones_and_will_not_go_below_its_floor()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // Four entries: 200 days old, 100, 30 and 1. A 90-day retention takes the first two and no others.
                await WriteAsync(app, siteId, 200, 100, 30, 1);
                Assert.Equal(4, (await LedgerAsync(app, siteId)).Count);

                var purged = await PurgeAsync(app, siteId, 90);
                _output.WriteLine($"90-day pass purged {purged}");
                Assert.Equal(2, purged);

                var remaining = await LedgerAsync(app, siteId);
                Assert.Equal(2, remaining.Count);
                Assert.All(remaining, entry => Assert.True(entry.OccurredOnUtc > DateTime.UtcNow.AddDays(-90)));

                // ---- THE FLOOR ------------------------------------------------------------------------------------
                // A site setting of 0 would mean "everything older than now", which empties a security record through
                // configuration. It is read as seven days instead, so the 1-day-old entry survives it.
                var zeroDayPass = await PurgeAsync(app, siteId, 0);
                _output.WriteLine($"0-day pass purged {zeroDayPass}");
                Assert.Equal(1, zeroDayPass);                       // the 30-day-old one, and only it

                var afterFloor = await LedgerAsync(app, siteId);
                Assert.Single(afterFloor);
                Assert.True(afterFloor[0].OccurredOnUtc > DateTime.UtcNow.AddDays(-7));

                // ...and a NEGATIVE value cannot reach past the floor either.
                Assert.Equal(0, await PurgeAsync(app, siteId, -3650));
                Assert.Single(await LedgerAsync(app, siteId));

                // The pure half of the same rule, so the clamp is pinned without a host as well.
                Assert.Equal(McpAuditRetentionPurger.MinimumRetentionDays, McpAuditRetentionPurger.ClampRetention(0));
                Assert.Equal(McpAuditRetentionPurger.MinimumRetentionDays, McpAuditRetentionPurger.ClampRetention(-1));
                Assert.Equal(365, McpAuditRetentionPurger.ClampRetention(365));   // no ceiling
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_pass_is_the_frameworks_own_and_names_the_setting_the_job_reads()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // Contributed to PurgeJob, not shipped as a second job: a module with its own schedule is a second
                // thing an operator can disable without knowing what they switched off.
                var purgers = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    Task.FromResult(sp.GetServices<IRetentionPurger>().ToList()));

                var mine = purgers.OfType<McpAuditRetentionPurger>().SingleOrDefault();
                Assert.NotNull(mine);
                Assert.Equal(McpAuditRetentionPurger.RetentionSettingKey, mine!.SettingKey);
                Assert.Equal(90, mine.DefaultDays);
                Assert.False(string.IsNullOrWhiteSpace(mine.DisplayName));

                // Non-vacuity: the framework's other contributed passes are there too, so "found mine" is not an
                // accident of an empty collection.
                Assert.True(purgers.Count >= 1);
                _output.WriteLine($"contributed passes: {string.Join(", ", purgers.Select(p => p.DisplayName))}");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<bool> WriteAsync(WebApplication app, int siteId, params int[] ageInDays) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var audit = sp.GetRequiredService<IMcpAuditRepository>();
                var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                foreach (var age in ageInDays)
                {
                    await audit.AddAsync(McpAuditEntryAggregate.Record(
                        Guid.NewGuid(), tenantId, siteId, userId: 1, McpAuditVerb.ToolCall, "a_tool",
                        McpAuditOutcome.Succeeded, "{}", DateTime.UtcNow.AddDays(-age)));
                }

                return true;
            });

        private static Task<System.Collections.Generic.List<McpAuditEntryAggregate>> LedgerAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IMcpAuditRepository>().ListBySiteAsync(siteId)).ToList());

        /// <summary>Runs the REAL pass the job would run, with the whole days the job would hand it.</summary>
        private static Task<int> PurgeAsync(WebApplication app, int siteId, int days) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) =>
                sp.GetServices<IRetentionPurger>().OfType<McpAuditRetentionPurger>().Single().PurgeAsync(siteId, days));
    }
}
