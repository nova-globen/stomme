#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 6 - the CostOptimization module driven over HTTP on an installed host, on SQLite and PostgreSQL: the
    /// page served from the module's own template; two recommendations of the site listed with their evidence and filtered
    /// by status; an advisor pass answering over HTTP; one applied (200) and refused a second decision (409 `not-open`); a
    /// zero cooldown refused (400 `invalid-cooldown`) before the other is dismissed for fourteen days; an unknown id 404; a
    /// blank action a validation problem; a plain user refused at the door (403) on the list and the pass.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class CostOptimizationModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/CostOptimization";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Recommendations_are_listed_decided_and_refused_over_http(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // ---- the page, served from the module's own template ------------------------------------------------------
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<Stomme.Repository.IPageRepository>().GetPageAsync("admin/cost-optimization", alias.SiteId)));
                var page = await GateDrive.GetAsync(client, cookies, Host, "/admin/cost-optimization", output, "the page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Cost Optimization", page.Body, StringComparison.OrdinalIgnoreCase);

                // ---- two open recommendations of the site, as an advisor pass would have raised them --------------------------
                var (toApply, toDismiss) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var repository = sp.GetRequiredService<IOptimizationRecommendationRepository>();
                    var anomaly = OptimizationRecommendationAggregate.Raise(Guid.NewGuid(), tenantId, alias.SiteId, RecommendationCategory.RightSizing,
                        "right-sizing:cloud-cost:azure:usd:mom-increase", "Review Azure spend: it rose 45% month-over-month.",
                        new[] { "2026-09: Azure spend was 580 USD, up from 400 USD in 2026-08 (+45.0%; alert threshold: 30%)." }, EstimatedSaving.Of(180m, "usd"), DateTime.UtcNow.AddMinutes(-2));
                    var caching = OptimizationRecommendationAggregate.Raise(Guid.NewGuid(), tenantId, alias.SiteId, RecommendationCategory.Caching,
                        "caching:assistant:low-hit-rate", "Enable or tune the deterministic LLM response cache.",
                        new[] { "2026-09: 3 of 400 AI requests were served from the response cache." }, EstimatedSaving.Of(12.5m, "USD"), DateTime.UtcNow.AddMinutes(-1));
                    await repository.AddAsync(anomaly);
                    await repository.AddAsync(caching);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return (anomaly.Id, caching.Id);
                });

                var listed = await Get<List<RecommendationView>>(client, cookies, Api, "the list");
                Assert.Equal(new[] { toDismiss, toApply }, listed.Select(r => r.Id)); // newest first
                Assert.Equal((180m, "USD", RecommendationStatus.Open), (listed[1].EstimatedSavingAmount, listed[1].EstimatedSavingCurrency, listed[1].Status));
                Assert.Contains("+45.0%", Assert.Single(listed[1].Evidence));
                Assert.Single(await Get<List<RecommendationView>>(client, cookies, $"{Api}?category=Caching", "by category"));

                // ---- an advisor pass over HTTP: both keys are open, so nothing new --------------------------------------------
                var run = await Send<AdvisorRunView>(app, client, cookies, hostId, $"{Api}/run", new { }, "an advisor pass");
                Assert.Equal(0, run.RaisedCount);

                // ---- apply, then a second decision is refused ---------------------------------------------------------------
                var applied = await Send<RecommendationDecisionView>(app, client, cookies, hostId, $"{Api}/{toApply}/apply", new ApplyRecommendationRequest { ActionTaken = "Right-sized the VM scale set." }, "apply");
                Assert.True(applied.Success);
                Assert.Equal((RecommendationStatus.Applied, "Right-sized the VM scale set."), (applied.Recommendation!.Status, applied.Recommendation.AppliedAction));
                var again = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{toApply}/dismiss", new DismissRecommendationRequest { Reason = "Too late." }, output, "a second decision");
                Assert.Equal(HttpStatusCode.Conflict, again.Status);
                Assert.Equal(RecommendationDecisionView.NotOpen, JsonSerializer.Deserialize<RecommendationDecisionView>(again.Body, Json)!.Code);

                // ---- a zero cooldown is refused; fourteen days is not --------------------------------------------------------
                var zero = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{toDismiss}/dismiss", new DismissRecommendationRequest { Reason = "Cache is off on purpose.", CooldownDays = 0 }, output, "a zero cooldown");
                Assert.Equal(HttpStatusCode.BadRequest, zero.Status);
                Assert.Equal(RecommendationDecisionView.InvalidCooldown, JsonSerializer.Deserialize<RecommendationDecisionView>(zero.Body, Json)!.Code);
                var dismissed = await Send<RecommendationDecisionView>(app, client, cookies, hostId, $"{Api}/{toDismiss}/dismiss", new DismissRecommendationRequest { Reason = "Cache is off on purpose.", CooldownDays = 14 }, "dismiss");
                Assert.True(dismissed.Success);
                Assert.InRange(dismissed.Recommendation!.DismissedUntilUtc!.Value, DateTime.UtcNow.AddDays(14).AddHours(-1), DateTime.UtcNow.AddDays(14).AddHours(1));

                // ---- an unknown id, and a blank action -----------------------------------------------------------------------
                var unknown = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{Guid.NewGuid()}/apply", new ApplyRecommendationRequest { ActionTaken = "Anything." }, output, "an unknown id");
                Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
                var blank = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{toApply}/apply", new ApplyRecommendationRequest { ActionTaken = " " }, output, "a blank action");
                Assert.Equal(HttpStatusCode.BadRequest, blank.Status);
                Assert.Contains("ActionTaken", blank.Body);

                var decided = await Get<List<RecommendationView>>(client, cookies, Api, "the list after the decisions");
                Assert.Equal(new[] { RecommendationStatus.Dismissed, RecommendationStatus.Applied }, decided.Select(r => r.Status));
                Assert.Empty(await Get<List<RecommendationView>>(client, cookies, $"{Api}?status=Open", "open after the decisions"));

                // ---- a plain user is refused at the door ----------------------------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, Api, output, "plain list")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, plainClient, plainCookies, Host, plainId, HttpMethod.Post, $"{Api}/run", new { }, output, "plain pass")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<T> Send<T>(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string path, object body, string label)
        {
            var response = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, path, body, output, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<T>(response.Body, Json)!;
        }

        private async Task<T> Get<T>(HttpClient client, System.Net.CookieContainer cookies, string path, string label)
        {
            var response = await GateDrive.GetAsync(client, cookies, Host, path, output, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<T>(response.Body, Json)!;
        }
    }
}
