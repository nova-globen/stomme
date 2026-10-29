using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Notifications.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A quota crossing reaches the site's ADMINISTRATORS through the producer port, exactly ONCE
    /// per crossing per window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Once" is a property of the persisted counter, not of anything in memory.</b>
    /// <c>QuotaAggregate.Apply</c> returns a crossing only the first time a fold passes the threshold in a window
    /// and records that in <c>WarningRaised</c>/<c>ExceededRaised</c>, so a restart or a second host cannot
    /// re-raise it. This suite records well past both thresholds and asserts the dispatch count stays at two.
    /// </para>
    /// <para>
    /// <b>Recipients are the site's operators by USER ID, never an address.</b> The dispatcher refuses an external
    /// address unless the site opts in and refuses an endpoint recipient outright; a quota warning has no business
    /// being the first thing to test that. The operators are the site's administrators AND the Host Users: a fresh
    /// install's site has no administrator at all, so a warning to administrators alone reached nobody - the
    /// second test is that install, untouched.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringQuotaNotificationTests
    {
        private const string Host = "localhost";
        private const string Metric = "tests.notified";

        private readonly ITestOutputHelper _output;

        public MeteringQuotaNotificationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_warning_and_an_exceeded_crossing_each_dispatch_once_to_the_sites_operators()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                await GateDrive.EnsureRoleAsync(app, Host, RoleNames.Admin);
                var admin = await GateDrive.AddUserAsync(app, Host, "quotaadmin", "Qu0ta!Admin$2026", "quotaadmin@gate.test", RoleNames.Admin);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                _output.WriteLine($"[admin] user {admin.UserId} in {RoleNames.Admin}; [host] user {hostId}");

                var dispatches = await CrossBothThresholdsAsync(app, siteId);
                Assert.Equal(2, dispatches.Count);
                Assert.Single(dispatches, request => request.TemplateKey == MeteringTemplateCatalogue.QuotaWarningKey);
                Assert.Single(dispatches, request => request.TemplateKey == MeteringTemplateCatalogue.QuotaExceededKey);

                // The site's operators: the administrator and the host, each once, by user id.
                foreach (var request in dispatches)
                {
                    Assert.All(request.Recipients, recipient => Assert.Equal(RecipientKind.User, recipient.Kind));
                    Assert.Equal(new[] { admin.UserId, hostId }.OrderBy(id => id).Select(id => id.ToString()), request.Recipients.Select(recipient => recipient.Address));
                }

                // The two templates the module contributes exist on the site - without them the dispatch would be
                // queued against a key nothing can render.
                var keys = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<INotificationTemplateRepository>().ListBySiteAsync(siteId)).Select(t => t.Key).ToList());
                _output.WriteLine($"[templates] {string.Join(", ", keys)}");
                Assert.Contains(MeteringTemplateCatalogue.QuotaWarningKey, keys);
                Assert.Contains(MeteringTemplateCatalogue.QuotaExceededKey, keys);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task On_a_fresh_install_with_no_administrator_the_host_is_told()
        {
            // The install's one user is the host, holding Host Users only; the site's Administrators role has no
            // member. Before the fix both crossings were logged as "no administrator to tell" and queued nothing.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var administrators = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IUserRoleRepository>().GetUserRolesAsync(RoleNames.Admin, siteId)).Count());
                _output.WriteLine($"[fresh install] {administrators} administrator(s); host user {hostId}");
                Assert.Equal(0, administrators);

                var dispatches = await CrossBothThresholdsAsync(app, siteId);

                Assert.Equal(new[] { MeteringTemplateCatalogue.QuotaExceededKey, MeteringTemplateCatalogue.QuotaWarningKey }, dispatches.Select(request => request.TemplateKey).OrderBy(key => key, StringComparer.Ordinal));
                Assert.All(dispatches, request => Assert.Equal(new RecipientShape(RecipientKind.User, hostId.ToString()), Assert.Single(request.Recipients)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // Limit 4, warn at 50% -> the warning threshold is 2. Recording 7: 2 -> warning; 3, 4 -> nothing; 5 -> exceeded;
        // 6, 7 -> nothing (once per crossing). Answers the metering dispatches the site then holds.
        private async Task<List<NotificationRequestShape>> CrossBothThresholdsAsync(WebApplication app, int siteId)
        {
            await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                await sp.GetRequiredService<IQuotaRepository>().AddAsync(QuotaAggregate.Define(
                    QuotaIds.For(tenantId, siteId, Metric, QuotaPeriod.Monthly), tenantId, siteId, Metric, QuotaPeriod.Monthly, 4, 50, DateTime.UtcNow));
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return true;
            });

            foreach (var _ in Enumerable.Range(0, 7))
            {
                await GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                    sp.GetRequiredService<IUsageRecorder>().RecordAsync(new UsageEvent(Metric, 1, Guid.NewGuid(), "tests")));
            }

            var dispatches = await MeteringDispatchesAsync(app, siteId);
            foreach (var request in dispatches)
            {
                _output.WriteLine($"[dispatch] {request.TemplateKey} -> {string.Join(", ", request.Recipients.Select(r => $"{r.Kind}:{r.Address}"))}");
            }

            return dispatches;
        }

        private static Task<List<NotificationRequestShape>> MeteringDispatchesAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<INotificationRequestRepository>().ListBySiteAsync(siteId))
                    .Where(request => request.TemplateKey.StartsWith("metering.", StringComparison.Ordinal))
                    .Select(request => new NotificationRequestShape(
                        request.TemplateKey,
                        request.Recipients.Select(recipient => new RecipientShape(recipient.Kind, recipient.Address)).ToList()))
                    .ToList());

        private sealed record NotificationRequestShape(string TemplateKey, IReadOnlyList<RecipientShape> Recipients);

        private sealed record RecipientShape(RecipientKind Kind, string Address);
    }
}
