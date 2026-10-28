#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - JWT and TOTP (n8n's JWT and TOTP nodes) DRIVEN on an installed SQLite host: a pipeline authored over the API as the
    /// editor saves it - a webhook, JWT signing claims, JWT verifying the token it made, TOTP making a code, a file written of what
    /// they gave - published and called; the file says the verified subject and issuer and a six-digit code; and neither the
    /// signing key nor the TOTP secret is in what the run's page reads of its steps.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class JwtAndTotpDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string WebhookSecret = "jwt-drive-webhook-secret";
        private const string SigningKey = "jwt-drive-signing-key-of-32-bytes!";
        private const string TotpSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_published_pipeline_signs_verifies_and_makes_a_code_and_its_run_shows_no_secret()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = WebhookSecret } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var definitionId = await PublishAsync(app, client, cookies, hostId);

                using var caller = GateDrive.NewClient(app, Host);
                var body = Encoding.UTF8.GetBytes("{\"order\": 7}");
                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/tokens")))
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, PipelineWebhooks.ComputeSignature(WebhookSecret, body));
                    Assert.Equal(HttpStatusCode.Accepted, (await caller.SendAsync(request)).StatusCode);
                }

                PipelineRunView? run = null;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (run is not { IsFinished: true } && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    run = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!.SingleOrDefault();
                }

                Assert.Equal("Finished", run?.SubStatus);
                var written = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=tokens.txt", output, "what the run wrote");
                Assert.Matches("^42 https://site\\.example [0-9]{6}$", written.Body);

                // What the run's page reads of its steps: every input it took, the key and the secret hidden.
                var steps = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run!.Id}/steps", output, "the run's steps");
                Assert.Equal(HttpStatusCode.OK, steps.Status);
                Assert.Contains("Stomme.Jwt", steps.Body);
                Assert.Equal(3, Regex.Matches(steps.Body, Regex.Escape("(hidden - this input can hold a secret)")).Count); // two keys, one secret
                Assert.DoesNotContain(SigningKey, steps.Body);
                Assert.DoesNotContain(TotpSecret, steps.Body);
                Assert.DoesNotContain(Regex.Replace(TotpSecret, "(.{4})", "$1 ").Trim(), steps.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> PublishAsync(WebApplication app, HttpClient client, CookieContainer cookies, int hostId)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Tokens" }, output, "create");
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Variables =
                {
                    new PipelineGraphVariable { Id = "v-token", Name = "token" },
                    new PipelineGraphVariable { Id = "v-claims", Name = "claims" },
                    new PipelineGraphVariable { Id = "v-code", Name = "code" },
                },
                Steps =
                {
                    new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "Tokens asked for", Inputs = { ["WebhookName"] = "tokens" } },
                    new PipelineGraphStep
                    {
                        Id = "sign", Type = "Stomme.Jwt", Name = "Sign",
                        Inputs = { ["Claims"] = "{\"sub\": \"42\"}", ["Algorithm"] = "HS256", ["Key"] = SigningKey, ["Issuer"] = "https://site.example", ["ExpiresInSeconds"] = "300" },
                        OutputVariables = { ["Result"] = "v-token" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "verify", Type = "Stomme.Jwt", Name = "Verify",
                        Inputs = { ["Operation"] = "Verify", ["Algorithm"] = "HS256", ["Key"] = SigningKey, ["Issuer"] = "https://site.example" },
                        InputVariables = { ["Token"] = "v-token" },
                        OutputVariables = { ["Result"] = "v-claims" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "code", Type = "Stomme.Totp", Name = "Code", Inputs = { ["Secret"] = TotpSecret }, OutputVariables = { ["Result"] = "v-code" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "write", Type = "Stomme.WriteTextFile", Name = "Write",
                        Inputs = { ["Path"] = "tokens.txt" },
                        InputExpressions = { ["Content"] = "{{ claims.sub }} {{ claims.iss }} {{ code.token }}" },
                    },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "hook", TargetId = "sign" },
                    new PipelineGraphConnection { SourceId = "sign", SourcePort = "Done", TargetId = "verify" },
                    new PipelineGraphConnection { SourceId = "verify", SourcePort = "Done", TargetId = "code" },
                    new PipelineGraphConnection { SourceId = "code", SourcePort = "Done", TargetId = "write" },
                },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
            var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
            Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
            return definitionId;
        }
    }
}
