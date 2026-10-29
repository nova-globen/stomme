#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Landing;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Tests.AiAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Landing
{
    /// <summary>
    /// The AI mode and the edit mode, driven over HTTP on an installed host, through the REAL Landing
    /// Page Designer and the site's metered model (a scripted one composed into the host as a provider): the site's assistant
    /// off is the refusal that says so; a brief is designed - the model's first submission carrying a script and a handler,
    /// which the sanitizer takes out and sends back, its second accepted - and PROPOSED, nothing stored; the page accepted with
    /// a script appended is stored without it; a section revised is the page proposed with it replaced, nothing stored until it
    /// is used; published, a visitor's home page carries it in a frame that runs no script, with no interactive island; a user
    /// without the keys is refused.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LandingDesignDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Landing";
        private const string Provider = "drive";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Regex InteractiveMarker = new("<!--Blazor:", RegexOptions.CultureInvariant);

        private const string Dirty = "<section data-section=\"hero\" onclick=\"steal()\"><script>alert(1)</script><h1>Fresh bread</h1></section>";
        private const string Clean = "<section data-section=\"hero\" style=\"padding: 2rem\"><h1>Fresh bread</h1><a href=\"/order\">Order</a></section><section data-section=\"contact\"><p>Call us on 555.</p></section>";
        private const string Revised = "<section data-section=\"hero\" style=\"padding: 2rem\"><h1>Baked daily at dawn</h1></section>";

        /// <summary>A model that designs by the tool it is offered: a dirty page first, the clean one once told what was wrong.</summary>
        private sealed class DesignModel : IChatClient
        {
            public int Calls;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                var tools = (options?.Tools ?? new List<AITool>()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                var results = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Select(result => result.Result?.ToString() ?? string.Empty).ToList();
                ChatMessage reply = tools.Contains("SubmitLandingPage")
                    ? results.Count == 0 ? TurnScriptChatClient.Call("a", "SubmitLandingPage", "html", Dirty)
                    : results.Last() == "accepted" ? TurnScriptChatClient.Answer("Designed.")
                    : TurnScriptChatClient.Call("b", "SubmitLandingPage", "html", Clean)
                    : tools.Contains("SubmitSection")
                    ? results.Count == 0 ? TurnScriptChatClient.Call("c", "SubmitSection", "html", Revised) : TurnScriptChatClient.Answer("Revised.")
                    : TurnScriptChatClient.Answer("Hello.");
                return Task.FromResult(new ChatResponse(reply) { Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 20 } });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private sealed class DesignProvider(DesignModel model) : ILlmProvider
        {
            public string Name => Provider;

            public IChatClient CreateChatClient(string endpoint, string model1, string? apiKey, HttpClient transport) => model;
        }

        [Fact]
        public async Task A_page_is_designed_from_a_brief_revised_by_section_and_shown_to_a_visitor_in_a_frame_that_runs_nothing()
        {
            var model = new DesignModel();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["AiAssistant"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<ILlmProvider>(new DesignProvider(model)));
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // The site has not switched its assistant on: the refusal says so, and no model is called.
                var off = await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/generate", new GenerateLandingRequest { Brief = "A bakery" }, "generate while off");
                Assert.Equal(HttpStatusCode.Conflict, off.Status);
                Assert.Equal("Disabled", JsonSerializer.Deserialize<AssistantRefusalView>(off.Body, Json)!.Code);
                Assert.Equal(0, model.Calls);

                var configured = await Send(app, client, cookies, hostId, HttpMethod.Put, "/api/AiAssistant/settings", new SaveAssistantSettingsRequest(true, Provider, "http://model.test/v1", "drive-model", 0.2f, "sk-drive"), "configure the assistant");
                Assert.True(configured.Status == HttpStatusCode.OK, $"configure: {configured.Status} {configured.Body}");

                // Designed from a brief: the dirty first submission was sent back, the clean one accepted - and nothing is stored.
                var draft = Ok<LandingDraftView>(await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/generate", new GenerateLandingRequest { Brief = "A bakery in the old town" }, "generate"), "generate");
                Assert.True(draft.Succeeded, string.Join("; ", draft.Problems));
                Assert.Equal(new[] { "hero", "contact" }, draft.Sections);
                Assert.DoesNotContain("script", draft.Html, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(2, model.Calls); // dirty, then clean - accepted ends the turn
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.GetAsync(client, cookies, Host, Api, output, "nothing stored yet")).Status);

                // Accepted - with a script appended on the way: the server sanitises again whatever the page sent.
                var stored = Ok<LandingPageView>(await Send(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/generated", new ConfigureGeneratedLandingRequest { Html = draft.Html + "<script>alert(document.cookie)</script>" }, "use the page"), "use the page");
                Assert.Equal((LandingSource.GeneratedHtml, LandingStatus.Draft), (stored.Source, stored.Status));
                Assert.DoesNotContain("script", stored.GeneratedHtml!, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(HttpStatusCode.BadRequest, (await Send(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/generated", new ConfigureGeneratedLandingRequest { Html = "<script>alert(1)</script>" }, "a page of nothing but a script")).Status);

                // A section revised: the whole page proposed with it replaced; stored only when used.
                var proposal = Ok<LandingDraftView>(await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/sections/propose", new ProposeLandingSectionRequest { Section = "hero", Instruction = "Say we bake at dawn" }, "revise the hero"), "revise the hero");
                Assert.True(proposal.Succeeded, string.Join("; ", proposal.Problems));
                Assert.Contains("Baked daily at dawn", proposal.Html);
                Assert.Contains("Call us on 555.", proposal.Html);
                var before = await GateDrive.GetAsync(client, cookies, Host, Api, output, "the page before the revision is used");
                Assert.Contains(">Fresh bread<", Ok<LandingPageView>((before.Status, before.Body), "the page before the revision is used").GeneratedHtml);
                Assert.Equal(HttpStatusCode.NotFound, (await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/sections/propose", new ProposeLandingSectionRequest { Section = "pricing", Instruction = "Add prices" }, "a section that does not exist")).Status);
                Ok<LandingPageView>(await Send(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/generated", new ConfigureGeneratedLandingRequest { Html = proposal.Html }, "use the revision"), "use the revision");
                Ok<LandingPageView>(await Send(app, client, cookies, hostId, HttpMethod.Post, $"{Api}/publish", new { }, "publish"), "publish");

                // A visitor's home page: the page in a sandboxed frame - no script allowed, no same origin - and no interactive island.
                using var visitor = GateDrive.NewClient(app, Host);
                var home = await GateDrive.GetAsync(visitor, null, Host, "/", output, "a visitor's home page");
                Assert.Equal(HttpStatusCode.OK, home.Status);
                var frame = Regex.Match(home.Body, "<iframe id=\"landing-generated\"[^>]*>", RegexOptions.CultureInvariant);
                Assert.True(frame.Success, "the home page carries no landing frame");
                Assert.Contains("sandbox=\"allow-popups allow-popups-to-escape-sandbox allow-top-navigation-by-user-activation\"", frame.Value);
                Assert.DoesNotContain("allow-scripts", frame.Value);
                Assert.DoesNotContain("allow-same-origin", frame.Value);
                Assert.Contains("Baked daily at dawn", frame.Value);
                Assert.Contains("default-src &#x27;none&#x27;", frame.Value.Replace("&#39;", "&#x27;"));
                Assert.Empty(InteractiveMarker.Matches(home.Body));

                // A user without the keys designs nothing.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plain, plainCookies, plainId, HttpMethod.Post, $"{Api}/generate", new GenerateLandingRequest { Brief = "Mine now" }, "a plain user's brief")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plain, plainCookies, plainId, HttpMethod.Put, $"{Api}/generated", new ConfigureGeneratedLandingRequest { Html = Clean }, "a plain user's page")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private static T Ok<T>((HttpStatusCode Status, string Body) answer, string label)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }
    }
}
