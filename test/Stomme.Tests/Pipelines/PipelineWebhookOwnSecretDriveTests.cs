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
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// DRIVEN over the real receiver on an installed SQLite host: a webhook given a secret of its own is verified under it
    /// alone - the installation's secret no longer opens it, another webhook still takes the installation's - a new one replaces
    /// it, one that cannot be read refuses every call rather than fall back, and removing it gives the installation's back. Made
    /// and removed through the editor's routes by the host; the ledger names the webhook and never the secret; an anonymous
    /// caller cannot make one.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineWebhookOwnSecretDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Installation = "installation-webhook-secret";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_webhook_with_its_own_secret_takes_it_alone_and_removing_it_gives_the_installations_back()
        {
            var pipelines = new JsonObject { ["Enabled"] = true, ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = Installation } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: new JsonObject { ["Pipelines"] = pipelines });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var orders = await PublishWebhookPipelineAsync(app, client, cookies, hostId, "orders");
                var other = await PublishWebhookPipelineAsync(app, client, cookies, hostId, "other");
                using var caller = GateDrive.NewClient(app, Host);
                var body = Encoding.UTF8.GetBytes("{\"order\":1}");

                // Before: the installation's secret opens it, and the panel says it has none of its own.
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", body, Installation)).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 1);
                var before = await InfoAsync(client, cookies, "orders");
                Assert.Equal((true, false, false), (before.Signed, before.OwnSecret, before.OwnSecretUnreadable));

                // An anonymous caller cannot make one.
                using var anonymous = GateDrive.NewClient(app, Host);
                Assert.True((await anonymous.PostAsync("http://" + Host + "/api/Pipelines/triggers/webhooks/orders/secret", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode
                    is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

                // Its own, made by the host (the name as typed, case-blind): the installation's is refused, its own admitted;
                // the other webhook still takes the installation's and not orders' secret.
                var own = await RenewAsync(app, client, cookies, hostId, "Orders");
                Assert.Equal("orders", own.Name);
                Assert.Matches("^[0-9a-f]{64}$", own.Secret);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "orders", body, Installation)).StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", body, own.Secret)).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 2);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "other", body, Installation)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "other", body, own.Secret)).StatusCode);
                await UntilRunsAsync(client, cookies, other, 1);
                var with = await InfoAsync(client, cookies, "orders");
                Assert.Equal((true, true, false), (with.Signed, with.OwnSecret, with.OwnSecretUnreadable));

                // A new one replaces it: the one before is refused.
                var renewed = await RenewAsync(app, client, cookies, hostId, "orders");
                Assert.NotEqual(own.Secret, renewed.Secret);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "orders", body, own.Secret)).StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", body, renewed.Secret)).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 3);

                // One that cannot be read (the key ring's shape: not a protected payload): every call refused, the installation's
                // included - never a fall back - and the panel says why.
                var key = PipelineWebhookSecrets.ReferenceKey("orders");
                await RewriteRawAsync(app, siteId, key, "not-a-protected-payload");
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "orders", body, Installation)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "orders", body, renewed.Secret)).StatusCode);
                var lost = await InfoAsync(client, cookies, "orders");
                Assert.Equal((false, false, true), (lost.Signed, lost.OwnSecret, lost.OwnSecretUnreadable));

                // Removed: the installation's opens it again; a second removal finds none.
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, "/api/Pipelines/triggers/webhooks/orders/secret", new { }, output, "remove")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, "/api/Pipelines/triggers/webhooks/orders/secret", new { }, output, "remove again")).Status);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", body, Installation)).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 4);
                Assert.False((await InfoAsync(client, cookies, "orders")).OwnSecret);
                Assert.Equal(4, (await RunsAsync(client, cookies, orders)).Count); // the refused calls started nothing

                // The ledger: who made and removed it, by the webhook's name - never a secret.
                var ledger = (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/audit", output, "audit")).Body;
                var entries = JsonSerializer.Deserialize<List<PipelineAuditEntryView>>(ledger, Json)!;
                Assert.Equal(2, entries.Count(entry => entry.Action == "made a new secret for a webhook" && entry.Detail == "'orders'"));
                Assert.Single(entries, entry => entry.Action == "removed a webhook's own secret" && entry.Detail == "'orders'");
                Assert.DoesNotContain(own.Secret, ledger);
                Assert.DoesNotContain(renewed.Secret, ledger);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<PipelineWebhookSecretView> RenewAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int hostId, string name)
        {
            var made = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/triggers/webhooks/{name}/secret", new { }, output, "secret " + name);
            Assert.Equal(HttpStatusCode.OK, made.Status);
            return JsonSerializer.Deserialize<PipelineWebhookSecretView>(made.Body, Json)!;
        }

        private async Task<TriggerInfoView> InfoAsync(HttpClient client, System.Net.CookieContainer cookies, string name) =>
            JsonSerializer.Deserialize<TriggerInfoView>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/triggers/info?type=Stomme.Webhook&value={name}", output, "info " + name)).Body, Json)!;

        private static Task RewriteRawAsync(WebApplication app, int siteId, string key, string value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var row = await settings.GetSettingAsync(EntityNames.Site, siteId, key);
                row.SettingValue = value;
                await settings.UpdateSettingAsync(row);
                return true;
            });

        private async Task<string> PublishWebhookPipelineAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int hostId, string name)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Webhook " + name }, output, "create " + name);
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "w", Type = "Stomme.Webhook", Name = name, Inputs = { ["WebhookName"] = name } },
                    new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = name + " received", ["Level"] = "Information" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "w", TargetId = "log" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph " + name)).Status);
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish " + name)).Status);
            return definitionId;
        }

        private async Task<List<PipelineRunView>> RunsAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId) =>
            JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;

        private async Task UntilRunsAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId, int count)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if ((await RunsAsync(client, cookies, definitionId)).Count >= count)
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException($"{definitionId} did not reach {count} run(s).");
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string name, byte[] body, string secret)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/" + name));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, PipelineWebhooks.ComputeSignature(secret, body));
            return await client.SendAsync(request);
        }
    }
}
