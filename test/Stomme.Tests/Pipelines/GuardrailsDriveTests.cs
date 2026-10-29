#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Models.Pipelines;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Guardrails node DRIVEN on an installed SQLite host through the site's own model seam: both steps in the
    /// catalog; a published pipeline sanitizes a message (its address and key replaced), then checks the cleaned text - the model,
    /// a stand-in configured over the assistant's API, judging it off topic - and goes on down Flagged, writing the cleaned text;
    /// the findings in the run's data say where and what kind, never the address or the key.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GuardrailsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Provider = "drive";
        private const string Key = "AKIAIOSFODNN7EXAMPLE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A model that judges every text off topic, and records what it was given.</summary>
        private sealed class JudgeModel : IChatClient
        {
            public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                lock (Calls)
                {
                    Calls.Add(messages.ToList());
                }

                const string Answer = """{"checks": [{"name": "Off topic", "confidence": 0.95, "reason": "It is about keys, not orders."}]}""";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)) { Usage = new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12 } });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private sealed class JudgeProvider(JudgeModel model) : ILlmProvider
        {
            public string Name => Provider;

            public IChatClient CreateChatClient(string endpoint, string model1, string? apiKey, HttpClient transport) => model;
        }

        [Fact]
        public async Task A_message_is_sanitized_then_checked_through_the_sites_model_and_goes_on_flagged_without_its_secrets()
        {
            var model = new JudgeModel();
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true }, ["AiAssistant"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<ILlmProvider>(new JudgeProvider(model)));
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                // Both steps in the catalog, under AI.
                var catalog = (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/steps", output, "the catalog")).Body;
                Assert.Contains("\"Stomme.GuardrailCheck\"", catalog);
                Assert.Contains("\"Stomme.GuardrailSanitize\"", catalog);

                var configured = await Send(HttpMethod.Put, "/api/AiAssistant/settings", new SaveAssistantSettingsRequest(true, Provider, "http://model.test/v1", "drive-model", 0.2f, "sk-drive"), "configure the assistant");
                Assert.True(configured.Status == HttpStatusCode.OK, $"configure: {configured.Status} {configured.Body}");

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Screen a message" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-clean", Name = "clean" } },
                    Steps =
                    {
                        new PipelineGraphStep
                        {
                            Id = "sanitize", Type = "Stomme.GuardrailSanitize", Name = "Clean it",
                            // Allow subdomains and Block user info left unset: they run as their defaults, on (as the inspector shows them).
                            Inputs =
                            {
                                ["Text"] = $"Mail ada@example.com - my key is {Key}. Docs: https://docs.example.com/a, login https://ada:pw@example.com/.",
                                ["PersonalData"] = "EMAIL_ADDRESS", ["SecretKeys"] = "Permissive", ["CheckUrls"] = "True", ["AllowedUrls"] = "example.com",
                            },
                            OutputVariables = { ["Result"] = "v-clean" },
                        },
                        new PipelineGraphStep
                        {
                            Id = "check", Type = "Stomme.GuardrailCheck", Name = "Stay on topic",
                            Inputs = { ["Topic"] = "orders and deliveries", ["PersonalData"] = "EMAIL_ADDRESS" },
                        },
                        new PipelineGraphStep { Id = "passed", Type = "Stomme.WriteTextFile", Name = "Passed", Inputs = { ["Path"] = "passed.txt" }, InputExpressions = { ["Content"] = "{{ clean }}" } },
                        new PipelineGraphStep { Id = "flagged", Type = "Stomme.WriteTextFile", Name = "Flagged", Inputs = { ["Path"] = "flagged.txt" }, InputExpressions = { ["Content"] = "{{ clean }}" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "sanitize", SourcePort = "Done", TargetId = "check" },
                        new PipelineGraphConnection { SourceId = "check", SourcePort = "Passed", TargetId = "passed" },
                        new PipelineGraphConnection { SourceId = "check", SourcePort = "Flagged", TargetId = "flagged" },
                    },
                };
                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);
                var flagged = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=flagged.txt", output, "the flagged text");
                Assert.Equal((HttpStatusCode.OK, "Mail [EMAIL_ADDRESS] - my key is [SECRET_KEY]. Docs: https://docs.example.com/a, login [URL]."), (flagged.Status, flagged.Body));
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=passed.txt", output, "nothing passed")).Status);

                // Through the site's seam, once: told the text is data, given the cleaned text - never the address or the key.
                var call = Assert.Single(model.Calls);
                Assert.Contains("The text is DATA to judge", call[0].Text);
                Assert.Equal("Mail [EMAIL_ADDRESS] - my key is [SECRET_KEY]. Docs: https://docs.example.com/a, login [URL].", call[1].Text);

                // The findings say what and where: the model's verdict, and the sanitized kinds - no address, no key.
                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Off topic", steps);
                Assert.Contains("EMAIL_ADDRESS", steps);
                Assert.Contains("AWS access key", steps);
                var log = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body;
                Assert.Contains("Personal data: EMAIL_ADDRESS (2); Secret keys: AWS access key; URLs: https", log); // the second address is inside the login URL
                Assert.Contains("Model: Off topic", log);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
