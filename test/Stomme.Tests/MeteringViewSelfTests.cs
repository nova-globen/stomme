using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D102 item 8, driven over REAL HTTP: the self-service key reads only the caller's own consumption, and it
    /// cannot be turned into a read of the site or of another user by anything on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape the predecessor platform got wrong twice over: its usage pages hand-checked <c>HasClaim</c> rather than
    /// carrying a key, and its override command carried no key at all while taking the tenant from the request
    /// body. The answer here is structural rather than careful - <c>GetMyUsageQuery</c> has NO user-id parameter
    /// and no metering request names a site or a tenant, so there is nothing for a caller to forge. A test that
    /// only asserted "the right rows come back" would pass against a forgeable API too; these assert that the
    /// OTHER surfaces are refused for the same caller, in the same session.
    /// </para>
    /// <para>
    /// A registered user holds <c>metering:view-own</c> by default and neither of the other two keys, so the same
    /// cookie that reads "my usage" must be refused at the log, the quota list and every write.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringViewSelfTests
    {
        private const string Host = "localhost";
        private const string Metric = "tests.viewself";

        private readonly ITestOutputHelper _output;

        public MeteringViewSelfTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_registered_user_reads_only_their_own_usage_and_is_refused_every_other_surface()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                // No extra role: IUserManager.AddUser already puts a new user in Registered Users, and passing it
                // again violates the (RoleId, UserId) unique constraint - measured.
                var mine = await GateDrive.AddUserAsync(app, Host, "selfuser", "S3lf!User$2026", "selfuser@gate.test");
                var other = await GateDrive.AddUserAsync(app, Host, "otheruser", "0ther!User$2026", "otheruser@gate.test");
                _output.WriteLine($"[users] mine={mine.UserId} other={other.UserId}");

                // Two users' consumption of the same metric, and a quota so the line has a window to report.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    await sp.GetRequiredService<IQuotaRepository>().AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(tenantId, siteId, Metric, QuotaPeriod.Monthly), tenantId, siteId, Metric, QuotaPeriod.Monthly, 1000, 0, DateTime.UtcNow));

                    var ledger = sp.GetRequiredService<IUsageRecordRepository>();
                    foreach (var (userId, quantity) in new[] { (mine.UserId, 3L), (other.UserId, 11L) })
                    {
                        var row = UsageRecordAggregate.Append(
                            QuotaIds.ForUsage(tenantId, siteId, Metric, Guid.NewGuid()), tenantId, siteId, Metric, quantity, userId, "tests", Guid.NewGuid(), DateTime.UtcNow);
                        row.MarkApplied();
                        await ledger.AddAsync(row);
                    }

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, "selfuser", "S3lf!User$2026", _output);

                var own = await GateDrive.GetAsync(client, cookies, Host, "/api/Metering/mine", _output, "mine");
                _output.WriteLine($"[mine] {(int)own.Status} {GateDrive.Truncate(own.Body)}");
                Assert.Equal(HttpStatusCode.OK, own.Status);

                // The caller's OWN quantity, never the other user's - and the body must not leak the other's total.
                Assert.Contains("\"myConsumption\":3", own.Body.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"myConsumption\":11", own.Body.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

                // The same session at every surface this key does NOT open.
                foreach (var path in new[] { "/api/Metering/usage", "/api/Metering/quotas", "/api/Metering/ai", "/api/Metering/ai/calls" })
                {
                    var refused = await GateDrive.GetAsync(client, cookies, Host, path, _output, path);
                    _output.WriteLine($"[refused] {path} -> {(int)refused.Status}");
                    Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
                }

                var define = await GateDrive.SendJsonAsync(app, client, cookies, Host, mine.UserId, HttpMethod.Post, "/api/Metering/quotas",
                    new { Metric = "tests.forged", Period = "Monthly", Limit = 5, WarnAtPercent = 0 }, _output, "define");
                _output.WriteLine($"[define] {(int)define.Status}");
                Assert.Equal(HttpStatusCode.Forbidden, define.Status);

                // Nothing was written by the refused write.
                var quotas = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(siteId)).Select(quota => quota.Metric).ToList());
                _output.WriteLine($"[quotas after] {string.Join(", ", quotas)}");
                Assert.DoesNotContain("tests.forged", quotas);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
