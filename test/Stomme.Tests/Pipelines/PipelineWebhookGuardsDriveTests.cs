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
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// the predecessor platform's per-endpoint webhook guards DRIVEN over the real receiver on an installed SQLite host (a BL-95 residue,
    /// 2026-09-28). The drive's clients carry addresses in 10.77.0.0/16. The installation admits only 192.0.2.0/24, so a name
    /// with no endpoint of its own refuses them (403, whatever the signature); <c>orders</c> admits 10.77.0.0/16 with a
    /// 60-second window keyed on the body, <c>by-id</c> the same keyed on <c>X-Request-Id</c>. A repeat of an authentic delivery
    /// is 202 and starts no run; a different one starts one; an UNSIGNED repeat is 401 and does not pre-empt the delivery.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineWebhookGuardsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Secret = "guards-webhook-secret";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task The_allowlist_refuses_before_the_body_and_a_repeat_inside_the_window_starts_no_run()
        {
            var pipelines = new JsonObject
            {
                ["Enabled"] = true,
                ["Webhooks"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Secret"] = Secret,
                    ["AllowedCallers"] = new JsonArray("192.0.2.0/24"),
                    ["Endpoints"] = new JsonObject
                    {
                        ["orders"] = new JsonObject { ["AllowedCallers"] = new JsonArray("10.77.0.0/16"), ["DeduplicationSeconds"] = 60 },
                        ["by-id"] = new JsonObject { ["AllowedCallers"] = new JsonArray("10.77.0.0/16"), ["DeduplicationSeconds"] = 60, ["DeduplicationHeader"] = "X-Request-Id" },
                    },
                },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: new JsonObject { ["Pipelines"] = pipelines });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var orders = await PublishWebhookPipelineAsync(app, client, cookies, hostId, "orders");
                var byId = await PublishWebhookPipelineAsync(app, client, cookies, hostId, "by-id");
                using var caller = GateDrive.NewClient(app, Host);

                // A name without an endpoint of its own takes the installation's allowlist, which this caller is not on.
                var first = Encoding.UTF8.GetBytes("{\"order\":1}");
                Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(caller, "other", first, Sign(first))).StatusCode);

                // orders: the first delivery starts a run; its repeat is 202 and starts none; an unsigned repeat is 401 and
                // does not pre-empt anything; a different delivery starts the second run.
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", first, Sign(first))).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 1);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", first, Sign(first))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "orders", Encoding.UTF8.GetBytes("{\"order\":2}"), null)).StatusCode);
                var second = Encoding.UTF8.GetBytes("{\"order\":2}");
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", second, Sign(second))).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 2);
                await Task.Delay(1500); // had the repeat been dispatched, its run would be there by now
                Assert.Equal(2, (await RunsAsync(client, cookies, orders)).Count);

                // A URL-encoded call is verified over its body like any other, and its name is the route's, not a field's
                // (found 2026-10-07: it was refused 401 - MVC's form binding had read the body before the signature was checked).
                var form = Encoding.UTF8.GetBytes("order=3&name=other");
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "orders", form, Sign(form), contentType: "application/x-www-form-urlencoded")).StatusCode);
                await UntilRunsAsync(client, cookies, orders, 3);

                // by-id: the same id with a different body is a repeat; a new id is not.
                var a = Encoding.UTF8.GetBytes("{\"payment\":\"a\"}");
                var b = Encoding.UTF8.GetBytes("{\"payment\":\"b\"}");
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "by-id", a, Sign(a), "delivery-1")).StatusCode);
                await UntilRunsAsync(client, cookies, byId, 1);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "by-id", b, Sign(b), "delivery-1")).StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "by-id", b, Sign(b), "delivery-2")).StatusCode);
                await UntilRunsAsync(client, cookies, byId, 2);
                await Task.Delay(1500);
                Assert.Equal(2, (await RunsAsync(client, cookies, byId)).Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string Sign(byte[] body) => PipelineWebhooks.ComputeSignature(Secret, body);

        private async Task<string> PublishWebhookPipelineAsync(WebApplication app, HttpClient client, CookieContainer cookies, int hostId, string name)
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

        private async Task<List<PipelineRunView>> RunsAsync(HttpClient client, CookieContainer cookies, string definitionId) =>
            JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;

        private async Task UntilRunsAsync(HttpClient client, CookieContainer cookies, string definitionId, int count)
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

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string name, byte[] body, string? signature, string? requestId = null, string contentType = "application/json")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/" + name));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            if (signature is not null)
            {
                request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, signature);
            }

            if (requestId is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            }

            return await client.SendAsync(request);
        }
    }
}
