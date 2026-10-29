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
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// The assistant DRIVEN on installed hosts on SQLite and PostgreSQL, over HTTP, with a scripted model
    /// composed into the host as a provider (no live model is called). The page is placed from the module's template; a
    /// user without <c>assistant:use</c> is refused; a site with the assistant off answers its reason; then the host asks for a
    /// board - the orchestrator routes to the Dashboard Designer, which reads the site's datasets through the Datasets module
    /// AS THE USER and proposes a board bound to one of them - nothing is created until the apply, which creates the board
    /// through the Dashboards module's own commands; every model call is in the site's AI usage, attributed to its agent.
    /// the predecessor platform's API cases by name are the start, send and apply-and-discard legs.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AiAssistantDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/AiAssistant";
        private const string Provider = "drive";
        private const string Model = "drive-model";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        /// <summary>
        /// The scripted model: it answers from what the agent offers it and what the tools already returned - route to the
        /// designer; list the datasets; propose a board bound to "Sales"; say so - so each agent's loop runs for real.
        /// </summary>
        private sealed class DriveModel : IChatClient
        {
            public int Calls;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                var tools = (options?.Tools ?? new List<AITool>()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                var results = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToDictionary(result => result.CallId, result => result.Result);
                ChatMessage reply;
                if (tools.Contains("Route"))
                {
                    reply = results.Count == 0
                        ? TurnScriptChatClient.Call("route", "Route", new Dictionary<string, object?> { ["specialists"] = JsonSerializer.SerializeToElement(new[] { "dashboard-designer" }), ["rationale"] = "a board was asked for" })
                        : TurnScriptChatClient.Answer("Routed.");
                }
                else if (tools.Contains("ProposeDashboard"))
                {
                    if (!results.TryGetValue("list", out var listed))
                    {
                        reply = TurnScriptChatClient.Call("list", "ListDatasets", new Dictionary<string, object?>());
                    }
                    else if (!results.ContainsKey("propose"))
                    {
                        var sales = JsonSerializer.SerializeToElement(listed).EnumerateArray()
                            .Single(dataset => Property(dataset, "name").GetString() == "Sales");
                        var draft = new AssistantDashboardDraft("Sales board", 12, 40,
                            [new AssistantWidgetDraft("Sales rows", "Grid", Property(sales, "id").GetGuid(), ["sku", "amount"], 0, 0, 6, 4)]);
                        reply = TurnScriptChatClient.Call("propose", "ProposeDashboard", "draft", draft);
                    }
                    else
                    {
                        reply = TurnScriptChatClient.Answer("I proposed a sales board.");
                    }
                }
                else
                {
                    reply = TurnScriptChatClient.Answer("Hello.");
                }

                return Task.FromResult(new ChatResponse(reply) { Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 12 } });
            }

            private static JsonElement Property(JsonElement element, string name) =>
                element.EnumerateObject().First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private sealed class DriveProvider(DriveModel model) : ILlmProvider
        {
            public string Name => Provider;

            public IChatClient CreateChatClient(string endpoint, string model1, string? apiKey, HttpClient transport) => model;
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_board_asked_for_is_proposed_through_the_users_reads_and_created_only_on_apply(string storeName)
        {
            var model = new DriveModel();
            using var scratch = await GateInstallTemplates.CloneAsync(
                LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["AiAssistant"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<ILlmProvider>(new DriveProvider(model)));
            try
            {
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/assistant", alias.SiteId)));
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // The site has not switched the assistant on: a start answers why, and nothing is stored.
                var off = await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations", new { }, "start while the site is off");
                Assert.Equal(HttpStatusCode.Conflict, off.Status);
                Assert.Equal("Disabled", JsonSerializer.Deserialize<AssistantRefusalView>(off.Body, Json)!.Code);

                // A user holding no assistant key is refused at the door - to talk to it and to configure it.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plainClient, plainCookies, plainId, HttpMethod.Post, $"{Api}/conversations", new { }, "start as a plain user")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, $"{Api}/settings", output, "settings as a plain user")).Status);

                // The site's administrator configures it: the defaults, a refusal to switch on with no model, then the scripted
                // provider with a key - which is stored and NEVER read back.
                var defaults = await Ok<AssistantSettingsView>(app, client, cookies, hostId, HttpMethod.Get, $"{Api}/settings", null, "read the settings");
                Assert.Equal((true, false, "openai", "None"), (defaults.InstallationEnabled, defaults.Enabled, defaults.Provider, defaults.KeyStatus));
                Assert.Contains(Provider, defaults.Providers);
                var noModel = await Send(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/settings", new SaveAssistantSettingsRequest(true, Provider, "http://model.test/v1", null, null), "switch on with no model");
                Assert.Equal(HttpStatusCode.BadRequest, noModel.Status);
                var saved = await Send(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/settings", new SaveAssistantSettingsRequest(true, Provider, "http://model.test/v1", Model, 0.2f, "sk-drive-secret"), "configure the site");
                Assert.True(saved.Status == HttpStatusCode.OK, $"configure: {saved.Status} {saved.Body}");
                Assert.DoesNotContain("sk-drive-secret", saved.Body);
                var configured = JsonSerializer.Deserialize<AssistantSettingsView>(saved.Body, Json)!;
                Assert.Equal((true, Provider, Model, 0.2f, "Stored"), (configured.Enabled, configured.Provider, configured.Model, configured.Temperature, configured.KeyStatus));

                // The dataset the board will bind, through the Datasets API.
                var sales = await Ok<DatasetDescriptor>(app, client, cookies, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-sales", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]", Classification = DataClassification.Internal },
                    "register Sales");

                // Start, then ask (the predecessor platform's AssistantEndpoints / SendMessage_StoresAssistantReplyAndProposedArtifact).
                var started = await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations", new { }, "start");
                Assert.Equal(HttpStatusCode.Created, started.Status);
                var conversationId = JsonSerializer.Deserialize<AssistantConversationView>(started.Body, Json)!.Id;
                var sent = await Ok<AssistantConversationView>(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations/{conversationId}/messages", new SendAssistantMessageRequest("Build me a board of the sales rows"), "send");

                Assert.Equal(new[] { ("User", "Build me a board of the sales rows"), ("Assistant", "I proposed a sales board.") }, sent.Messages.Select(message => (message.Role, message.Content)));
                var artifact = Assert.Single(sent.Artifacts);
                Assert.Equal(("Dashboard", "Sales board", "Proposed"), (artifact.Kind, artifact.Title, artifact.State));
                Assert.Contains(sales.Id.ToString(), artifact.SpecJson);

                // Proposed is not created: the Dashboards module has no such board yet.
                var before = JsonSerializer.Deserialize<List<DashboardView>>((await GateDrive.GetAsync(client, cookies, Host, "/api/Dashboards", output, "boards before the apply")).Body, Json)!;
                Assert.DoesNotContain(before, board => board.Name == "Sales board");

                // Apply (the predecessor platform's ApplyAndDiscard_RoundTripArtifactStateThroughApi): the board through the Dashboards module.
                var applied = await Ok<AssistantArtifactApplyResult>(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations/{conversationId}/artifacts/{artifact.Id}/apply", new { }, "apply");
                var board = JsonSerializer.Deserialize<DashboardView>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Dashboards/{applied.CreatedResourceId}", output, "the created board")).Body, Json)!;
                Assert.Equal("Sales board", board.Name);
                var widget = Assert.Single(board.Widgets);
                Assert.Equal(("Sales rows", WidgetKind.Grid), (widget.Title, widget.Kind));
                var again = await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations/{conversationId}/artifacts/{artifact.Id}/apply", new { }, "apply twice");
                Assert.Equal(HttpStatusCode.Conflict, again.Status);

                // A second proposal, discarded - it stays proposed-then-discarded and creates nothing.
                var second = await Ok<AssistantConversationView>(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations/{conversationId}/messages", new SendAssistantMessageRequest("And another one"), "send again");
                var proposed = second.Artifacts.Single(candidate => candidate.State == "Proposed");
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/conversations/{conversationId}/artifacts/{proposed.Id}/discard", new { }, "discard")).Status);
                var final = await Ok<AssistantConversationView>(app, client, cookies, hostId, HttpMethod.Get, $"{Api}/conversations/{conversationId}", null, "read back");
                Assert.Equal(new[] { "Applied", "Discarded" }, final.Artifacts.Select(candidate => candidate.State));
                Assert.Single(JsonSerializer.Deserialize<List<DashboardView>>((await GateDrive.GetAsync(client, cookies, Host, "/api/Dashboards", output, "boards after")).Body, Json)!, candidate => candidate.Name == "Sales board");

                // A user without the key cannot read it either (another key-holder's read answers as absent - the service suite).
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, $"{Api}/conversations/{conversationId}", output, "another user's")).Status);

                // Every model call is in the site's AI usage, attributed to its agent and the site's provider and model.
                var usage = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IAiUsageRecordRepository>().ListBySiteAsync(alias.SiteId));
                Assert.Equal(model.Calls, usage.Count);
                Assert.All(usage, row => Assert.Equal((Provider, Model, AiCallStatus.Success, (int?)hostId), (row.Provider, row.Model, row.Status, row.UserId)));
                Assert.Equal(new[] { "dashboard-designer", "orchestrator" }, usage.Select(row => row.Agent).Distinct().OrderBy(agent => agent, StringComparer.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private async Task<T> Ok<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label)
        {
            var reply = method == HttpMethod.Get
                ? await GateDrive.GetAsync(client, cookies, Host, path, output, label) is var got ? (got.Status, got.Body) : default
                : await Send(app, client, cookies, userId, method, path, body, label);
            Assert.True(reply.Status == HttpStatusCode.OK, $"{label}: {reply.Status} {reply.Body}");
            return JsonSerializer.Deserialize<T>(reply.Body, Json)!;
        }
    }
}
