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
    /// Ask AI's answer shape (n8n's Structured Output Parser, and its Auto-fixing one) DRIVEN on an installed SQLite host
    /// through the site's own model seam: the assistant configured over its API, a stand-in model that first answers the total as
    /// text and corrects itself when told what was wrong; a published pipeline asks with an answer shape - the model told the
    /// shape, asked once more with its answer and the problem, the second answer the result a later step writes.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AskAiShapeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Provider = "drive";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A model that answers the total as text, and as a number once told it does not fit.</summary>
        private sealed class InvoiceModel : IChatClient
        {
            public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                var asked = messages.ToList();
                lock (Calls)
                {
                    Calls.Add(asked);
                }

                var told = asked.Last().Text.Contains("does not fit the shape", StringComparison.Ordinal);
                var answer = told ? "{\"customer\": \"Ada\", \"total\": 120.5}" : "{\"customer\": \"Ada\", \"total\": \"120.50 EUR\"}";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)) { Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 10 } });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private sealed class InvoiceProvider(InvoiceModel model) : ILlmProvider
        {
            public string Name => Provider;

            public IChatClient CreateChatClient(string endpoint, string model1, string? apiKey, HttpClient transport) => model;
        }

        [Fact]
        public async Task An_answer_that_misses_the_shape_is_asked_again_through_the_sites_model_and_the_fixed_one_is_the_result()
        {
            var model = new InvoiceModel();
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true }, ["AiAssistant"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<ILlmProvider>(new InvoiceProvider(model)));
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                var configured = await Send(HttpMethod.Put, "/api/AiAssistant/settings", new SaveAssistantSettingsRequest(true, Provider, "http://model.test/v1", "drive-model", 0.2f, "sk-drive"), "configure the assistant");
                Assert.True(configured.Status == HttpStatusCode.OK, $"configure: {configured.Status} {configured.Body}");

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Read an invoice" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-invoice", Name = "invoice" } },
                    Steps =
                    {
                        new PipelineGraphStep
                        {
                            Id = "read", Type = "Stomme.AskAi", Name = "Read it",
                            Inputs = { ["Prompt"] = "Invoice 1001 from Ada, 120.50 EUR.", ["Shape"] = "{\"customer\": \"\", \"total\": 0}" },
                            OutputVariables = { ["Result"] = "v-invoice" },
                        },
                        new PipelineGraphStep { Id = "write", Type = "Stomme.WriteTextFile", Name = "Write", Inputs = { ["Path"] = "invoice.txt" }, InputExpressions = { ["Content"] = "{{ invoice.customer }} {{ invoice.total }}" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "write" } },
                };
                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish")).Status);
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
                var written = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=invoice.txt", output, "what the run wrote");
                Assert.Equal((HttpStatusCode.OK, "Ada 120.5"), (written.Status, written.Body));

                // Through the site's seam: told the shape, asked once more with its answer and what was wrong.
                Assert.Equal(2, model.Calls.Count);
                Assert.Contains("Answer with JSON only, of exactly this shape", model.Calls[0][0].Text);
                var again = model.Calls[1];
                Assert.Equal((ChatRole.Assistant, "{\"customer\": \"Ada\", \"total\": \"120.50 EUR\"}"), (again[^2].Role, again[^2].Text));
                Assert.Contains("- $.total: a number, not text.", again[^1].Text);
                var log = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body;
                Assert.Contains("Asked again", log);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
