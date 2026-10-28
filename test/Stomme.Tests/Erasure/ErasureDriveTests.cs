#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Models.Erasure;
using Stomme.Modules.Erasure.Domain;
using Stomme.Modules.Erasure.Execution;
using Stomme.Erasure.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Export;
using Stomme.DataExport.Persistence;
using Stomme.Mcp.Persistence;
using Stomme.Notifications.Persistence;
using Stomme.Portability.Persistence;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Portability.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Persistence.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Erasure
{
    /// <summary>
    /// BL-116 / D112 driven on a real install, one store at a time: a user with a setting, a site notification and a
    /// visitor row is deleted; the deletion queues an erasure; the job runs it under the site's alias and every
    /// framework participant erases its rows under its declared handling; the identity audit's rows are RETAINED (the
    /// deletion itself wrote one about the subject), so the receipt keeps the subject's ids until that falls due; a
    /// second run changes nothing; the Host reads the receipt over HTTP and it names no person; a registered-only
    /// user cannot read it.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ErasureDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Deleting_a_user_queues_an_erasure_the_job_runs_under_every_participant_and_the_receipt_names_no_one(string storeName)
        {
            // BL-183: the self-delete answered 500 once and neither channel the drive prints on a failure named the cause. The
            // framework's LogManager writes a site's errors to the site's Log table through a background drain and broadcasts
            // them to ILogger only at or above Logging:LogLevel:Broadcast, which ships "None" - so the host channel (the
            // ILogger capture) could not see a caught-and-logged error at all, and the table read raced the drain on a slow
            // tier. Broadcast at Error, here, so the next occurrence names its exception in the drive's own output.
            var broadcastErrors = new System.Text.Json.Nodes.JsonObject
            {
                ["Logging"] = new System.Text.Json.Nodes.JsonObject { ["LogLevel"] = new System.Text.Json.Nodes.JsonObject { ["Broadcast"] = "Error" } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: broadcastErrors);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var subject = await GateDrive.AddUserAsync(app, Host, "erased-user", "Er4sed!User$2026", "erased-user@gate.test");
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);

                // The subject's rows in three framework stores, then the deletion.
                var siteId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting { EntityName = EntityNames.User, EntityId = subject.UserId, SettingName = "theme", SettingValue = "dark", IsPrivate = false });
                    await sp.GetRequiredService<INotificationRepository>().AddNotificationAsync(new Notification { SiteId = alias.SiteId, FromUserId = hostId, FromDisplayName = "host", FromEmail = "host@gate.test", ToUserId = subject.UserId, ToDisplayName = "erased", ToEmail = "erased-user@gate.test", Subject = "welcome", Body = "hello", CreatedOn = DateTime.UtcNow, IsDelivered = false, IsRead = false, IsDeleted = false });
                    await sp.GetRequiredService<IVisitorRepository>().AddVisitorAsync(new Visitor { SiteId = alias.SiteId, UserId = subject.UserId, IPAddress = "10.9.9.9", UserAgent = "drive", Language = "en", Url = "/", Referrer = "", Visits = 1, CreatedOn = DateTime.UtcNow, VisitedOn = DateTime.UtcNow });

                    // ...and in the four Wave-1 ledgers (increment 2): an MCP tool call, a portability audit line about them,
                    // an export job they requested with its audit line, and a notification request naming them and the host.
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var now = DateTime.UtcNow;
                    await sp.GetRequiredService<IMcpAuditRepository>().AddAsync(McpAuditEntryAggregate.Record(Guid.NewGuid(), tenantId, alias.SiteId, subject.UserId, McpAuditVerb.ToolCall, "list_things", McpAuditOutcome.Succeeded, "{}", now));
                    await sp.GetRequiredService<IPortabilityAuditRepository>().AddAsync(PortabilityAuditEntryAggregate.Record(tenantId, alias.SiteId, null, PortabilityAuditAction.BuildRequested, PortabilityPackageKind.SubjectData, 0, hostId, subject.UserId, now));
                    var job = ExportJobAggregate.Request(Guid.NewGuid(), tenantId, alias.SiteId, subject.UserId, "site.users", ExportFormat.Csv, null, "{}", now);
                    await sp.GetRequiredService<IExportJobRepository>().AddAsync(job);
                    await sp.GetRequiredService<IExportAuditRepository>().AddAsync(ExportAuditEntryAggregate.Record(Guid.NewGuid(), tenantId, alias.SiteId, job.Id, subject.UserId, ExportAuditAction.Requested, now));
                    // BL-95: a schedule they own - it runs as its owner, so with the owner gone it goes too.
                    await sp.GetRequiredService<IExportScheduleRepository>().AddAsync(ExportScheduleAggregate.Create(
                        Guid.NewGuid(), tenantId, alias.SiteId, subject.UserId, "Nightly users", "site.users", ExportFormat.Csv, null, "{}", "0 6 * * *", now));
                    await sp.GetRequiredService<INotificationRequestRepository>().AddAsync(NotificationRequestAggregate.Create(Guid.NewGuid(), tenantId, alias.SiteId, "welcome",
                        new[] { new Recipient(RecipientKind.User, subject.UserId.ToString()), new Recipient(RecipientKind.User, hostId.ToString()) }, new[] { ChannelKind.InApp }, createdOnUtc: now));
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

                    await sp.GetRequiredService<IUserManager>().DeleteUser(subject.UserId, alias.SiteId);
                    return alias.SiteId;
                });

                // Queued, with the ids the participants need.
                var queued = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IErasureRequestRepository>().ListOpenAsync(alias.SiteId, DateTime.UtcNow)).Single());
                Assert.Equal((ErasureRequestStatus.Pending, subject.UserId), (queued.Status, queued.UserId));
                Assert.False(string.IsNullOrEmpty(queued.IdentitySubject), "the identity subject was not captured before the identity user went");

                await RunUntilSettledAsync(app, queued.Id);

                var receipt = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IErasureRequestRepository>().GetByIdAsync(queued.Id))!);
                Assert.Equal(ErasureRequestStatus.Retaining, receipt.Status);
                Assert.NotNull(receipt.ErasedOnUtc);
                Assert.Equal(subject.UserId, receipt.UserId); // the audit retention keeps the ids until it falls due
                var outcomes = receipt.ReadOutcomes();
                output.WriteLine(string.Join("\n", outcomes.Select(o => $"{o.Participant} / {o.Store}: {o.Handling} {o.Affected} {o.Reason} {o.RetainedUntilUtc:u}")));
                // At least the seeded row each (the host may have written more of its own to the new user); the stores being empty afterwards is the proof.
                foreach (var participant in new[] { "User settings", "Site notifications", "Visitors" })
                {
                    var affected = outcomes.Single(o => o.Participant == participant).Affected;
                    Assert.True(affected >= 1, $"{storeName}: {participant} affected {affected}, expected at least the seeded row");
                }
                var audit = outcomes.Single(o => o.Participant == "Identity audit");
                Assert.Equal((ErasureHandling.Retain, RetentionReason.Security), (audit.Handling, audit.Reason));
                Assert.True(audit.Affected >= 1, "the deletion itself writes an identity audit entry about the subject");
                Assert.InRange(audit.RetainedUntilUtc!.Value, DateTime.UtcNow.AddDays(364), DateTime.UtcNow.AddDays(366));
                // PostgreSQL keeps microseconds in the column while the outcome in the JSON keeps the full ticks.
                Assert.Equal(receipt.RetainedUntilUtc!.Value, audit.RetainedUntilUtc!.Value, TimeSpan.FromMilliseconds(1));
                foreach (var participant in new[] { "Event log", "Personal pages", "Site access", "Authority tokens", "Sessions", "Notification preferences", "Metering ledger pseudonymisation" })
                {
                    Assert.Contains(outcomes, o => o.Participant == participant);
                }

                // The Wave-1 ledgers (increment 2), each under its declared handling.
                var mcp = outcomes.Single(o => o.Participant == "MCP audit");
                Assert.Equal((ErasureHandling.Retain, RetentionReason.Security, 1), (mcp.Handling, mcp.Reason, mcp.Affected));
                var portabilityAudit = outcomes.Single(o => o.Participant == "Portability" && o.Store == "PortabilityAuditEntryAggregate");
                Assert.Equal((ErasureHandling.Retain, RetentionReason.LegalClaims, 1), (portabilityAudit.Handling, portabilityAudit.Reason, portabilityAudit.Affected));
                Assert.Equal((ErasureHandling.Delete, 1), (outcomes.Single(o => o.Store == "ExportJobAggregate").Handling, outcomes.Single(o => o.Store == "ExportJobAggregate").Affected));
                Assert.Equal((ErasureHandling.Retain, 1), (outcomes.Single(o => o.Store == "ExportAuditEntryAggregate").Handling, outcomes.Single(o => o.Store == "ExportAuditEntryAggregate").Affected));
                Assert.Equal((ErasureHandling.Delete, 1), (outcomes.Single(o => o.Store == "ExportScheduleAggregate").Handling, outcomes.Single(o => o.Store == "ExportScheduleAggregate").Affected));
                Assert.Equal((ErasureHandling.Anonymise, 1), (outcomes.Single(o => o.Participant == "Notification requests").Handling, outcomes.Single(o => o.Participant == "Notification requests").Affected));

                // The rows are gone from the stores.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    Assert.Empty(await sp.GetRequiredService<ISettingRepository>().GetSettingsAsync(EntityNames.User, subject.UserId));
                    Assert.Empty(await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(alias.SiteId, -1, subject.UserId));
                    Assert.DoesNotContain(await sp.GetRequiredService<IVisitorRepository>().GetVisitorsAsync(alias.SiteId, DateTime.UtcNow.AddDays(-1)), v => v.UserId == subject.UserId);
                    // Deleted: the export job. Anonymised: the request keeps the host and no longer names the subject. Retained: the audits.
                    Assert.Empty(await sp.GetRequiredService<IExportJobRepository>().ListByRequesterAsync(alias.SiteId, subject.UserId));
                    Assert.Empty(await sp.GetRequiredService<IExportScheduleRepository>().ListByOwnerAsync(alias.SiteId, subject.UserId));
                    var request = Assert.Single(await sp.GetRequiredService<INotificationRequestRepository>().ListBySiteAsync(alias.SiteId), r => r.TemplateKey == "welcome");
                    Assert.Equal(new[] { hostId.ToString() }, request.Recipients.Select(r => r.Address));
                    Assert.Single(await sp.GetRequiredService<IMcpAuditRepository>().FindAsync(e => e.SiteId == alias.SiteId && e.UserId == subject.UserId));
                    Assert.Single(await sp.GetRequiredService<IExportAuditRepository>().FindAsync(e => e.SiteId == alias.SiteId && e.ActorUserId == subject.UserId));
                    return 0;
                });

                // Idempotent: a second run leaves the receipt as it was.
                output.WriteLine(await RunJobOnceAsync(app));
                var again = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IErasureRequestRepository>().GetByIdAsync(queued.Id))!);
                Assert.Equal((receipt.Status, receipt.ErasedOnUtc, receipt.Attempts, receipt.OutcomesJson), (again.Status, again.ErasedOnUtc, again.Attempts, again.OutcomesJson));

                // The receipt over HTTP, for the Host; it names no person.
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var list = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Erasure/receipts", output, "receipts");
                Assert.Equal(HttpStatusCode.OK, list.Status);
                var receipts = JsonSerializer.Deserialize<List<ErasureReceiptView>>(list.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var shown = Assert.Single(receipts, r => r.Id == queued.Id);
                Assert.Equal(("Retaining", 32), (shown.Status, shown.SubjectErasureId.Length));
                Assert.DoesNotContain("userId", list.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("erased-user", list.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(queued.IdentitySubject!, list.Body, StringComparison.Ordinal);
                var one = await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Erasure/receipts/{queued.Id}", output, "one receipt");
                Assert.Equal(HttpStatusCode.OK, one.Status);

                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var denied = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Erasure/receipts", output, "receipts as a registered user");
                Assert.Equal(HttpStatusCode.Forbidden, denied.Status);

                // Increment 4: the profile's self-delete is the SAME mechanism, over HTTP. A second subject deletes
                // themselves and is signed out; a Host may not delete themselves; the job then writes their receipt.
                var self = await GateDrive.AddUserAsync(app, Host, "self-erased", "S3lf!Erased$2026", "self-erased@gate.test", RoleNames.Admin);
                using var selfClient = GateDrive.NewClient(app, Host);
                var selfCookies = await GateDrive.LoginAsync(app, selfClient, Host, "self-erased", "S3lf!Erased$2026", output);

                // Increment 5: as an administrator they author a page, which is the SITE's content and survives them -
                // stamped with their username until the erasure, with the placeholder afterwards.
                var home = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).First(p => p.Path == ""));
                var authored = JsonSerializer.Deserialize<Page>(JsonSerializer.Serialize(home))!;
                authored.PageId = 0;
                authored.ParentId = null;
                authored.Name = "authored-by-erased";
                authored.Title = "authored-by-erased";
                authored.Path = "authored-by-erased";
                authored.UserId = null;
                // As the Add-page form sends it: no stamps of its own. The RavenDB listener reads an unset CreatedOn as
                // "an insert" (D26 - the document store has no change tracker), so a clone carrying the home page's
                // CreatedOn would be stamped as an UPDATE there, ModifiedBy only, while EF stamps by EntityState.
                authored.CreatedBy = null;
                authored.CreatedOn = default;
                authored.ModifiedBy = null;
                authored.ModifiedOn = default;
                authored.PermissionList = new List<Permission> { new Permission(PermissionNames.View, RoleNames.Everyone, true), new Permission(PermissionNames.Edit, RoleNames.Admin, true) };
                var posted = await GateDrive.SendJsonAsync(app, selfClient, selfCookies, Host, self.UserId, HttpMethod.Post, "/api/Page", authored, output, "the editor's page");
                Assert.Equal(HttpStatusCode.OK, posted.Status);
                // Every provider stamps the signed-in name: the RavenDB listener carried a fixed empty context until
                // this drive found it (BL-175, fixed in the same increment) - a page POSTed by a signed-in
                // administrator came back CreatedBy "" on RavenDB and "self-erased" on SQLite and PostgreSQL.
                var stamped = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("authored-by-erased", alias.SiteId));
                Assert.Equal(("self-erased", "self-erased"), (stamped.CreatedBy, stamped.ModifiedBy));
                var selfDelete = await GateDrive.SendJsonAsync(app, selfClient, selfCookies, Host, self.UserId, HttpMethod.Delete, "/api/User/me", null, output, "self-delete");
                Assert.Equal(HttpStatusCode.OK, selfDelete.Status);
                var hostSelfDelete = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Delete, "/api/User/me", null, output, "a Host's self-delete");
                Assert.Equal(HttpStatusCode.Forbidden, hostSelfDelete.Status);
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    Assert.Null(await sp.GetRequiredService<IUserRepository>().GetUserAsync(self.UserId, false));
                    Assert.NotNull(await sp.GetRequiredService<IUserRepository>().GetUserAsync(hostId, false));
                    return 0;
                });
                var selfRequestId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IErasureRequestRepository>().ListForSiteAsync(alias.SiteId, 10)).Single(r => r.Id != queued.Id).Id);
                await RunUntilSettledAsync(app, selfRequestId);
                var selfReceipt = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IErasureRequestRepository>().GetByIdAsync(selfRequestId))!);
                Assert.Equal(ErasureRequestStatus.Retaining, selfReceipt.Status);
                Assert.Contains(selfReceipt.ReadOutcomes(), o => o.Participant == "Identity audit" && o.Affected >= 1);
                var stampsOutcome = selfReceipt.ReadOutcomes().Single(o => o.Participant == "Authorship stamps");
                Assert.True(stampsOutcome.Affected >= 1, $"{storeName}: the authored page carried the username, expected at least one row anonymised, got {stampsOutcome.Affected}");
                var anonymised = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("authored-by-erased", alias.SiteId));
                Assert.Equal(("[erased]", "[erased]"), (anonymised.CreatedBy, anonymised.ModifiedBy));
                // The dates stay: the audit stamping is suppressed for the write on both providers, or ModifiedBy would
                // come back as the job's empty name with ModifiedOn bumped.
                Assert.Equal((stamped.CreatedOn, stamped.ModifiedOn), (anonymised.CreatedOn, anonymised.ModifiedOn));
                // The FIRST subject's receipt reported 0 stamps at its run: nothing of theirs survived the other participants, and the drive
                // did not stamp anything with their name; the guard is what matters and the second subject exercises it.

                // The receipts page, placed for the Host by the module's page template, exists and renders; the two
                // receipts are listed; a registered user is refused the page.
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/erasure", alias.SiteId)));
                var page = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/admin/erasure", output, "the receipts page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Erasure", page.Body);
                var two = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Erasure/receipts", output, "receipts, two");
                Assert.Equal(2, JsonSerializer.Deserialize<List<ErasureReceiptView>>(two.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Count);
                var refused = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/admin/erasure", output, "the receipts page as a registered user");
                Assert.NotEqual(HttpStatusCode.OK, refused.Status);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        /// <summary>
        /// Runs the job until the request has left Pending. The host's OWN ErasureJob (every minute, and at start) may hold
        /// the per-site lease when the drive's run asks for it - the run then reports 'Is Being Run Elsewhere' and does
        /// nothing, which a full gate hit once (2026-09-27). Either runner completes the erasure; the drive asserts the
        /// outcome, not who produced it. The host cannot run with its jobs off here: the drive's HTTP half needs a started server.
        /// </summary>
        private async Task RunUntilSettledAsync(WebApplication app, Guid requestId)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                output.WriteLine(await RunJobOnceAsync(app));
                var row = await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IErasureRequestRepository>().GetByIdAsync(requestId))!);
                if (row.Status != ErasureRequestStatus.Pending)
                {
                    return;
                }

                await Task.Delay(250);
            }

            Assert.Fail($"erasure {requestId} stayed Pending through 60 runs");
        }

        // The job as the host runs it: per tenant in its own scope, then per site inside (BackupScheduleJobTests' shape).
        private static async Task<string> RunJobOnceAsync(WebApplication app)
        {
            List<Tenant> tenants;
            using (var listing = app.Services.CreateScope())
            {
                tenants = (await listing.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
            }

            var notes = new StringBuilder();
            foreach (var tenant in tenants)
            {
                using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!.Value);
                using var jobScope = app.Services.CreateScope();
                jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes.Append(await new ErasureJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider));
            }

            return notes.ToString();
        }
    }
}
