#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's multi-page forms DRIVEN on an installed SQLite host through the real receivers and the host's background
    /// dispatcher: a published pipeline's form, then a Wait for a form with Next page and no Call address. A person's submission is
    /// redirected straight to the run's form address, which already shows page two; page two's submission is thanked at once (the
    /// run ends, nothing more to show), the run having both pages' answers; the address then says the form is closed.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class FormNextPageDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_submission_is_taken_straight_on_to_the_next_page_and_the_last_page_thanks_at_once()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Forms"] = new JsonObject { ["Enabled"] = true, ["NextPageWithinSeconds"] = 30 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Sign up" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-first", Name = "first" }, new PipelineGraphVariable { Id = "v-second", Name = "second" } },
                    Steps =
                    {
                        new PipelineGraphStep
                        {
                            Id = "form", Type = PipelineForms.StepType, Name = "Sign up",
                            Inputs =
                            {
                                ["FormName"] = "signup", ["Title"] = "Sign up", ["Answer"] = "Welcome aboard - see you soon.",
                                ["Fields"] = PipelineForms.Serialize([new PipelineFormField { Label = "Your name", Name = "name", Required = true }]),
                            },
                            OutputVariables = { ["Result"] = "v-first" },
                        },
                        new PipelineGraphStep
                        {
                            Id = "page-two", Type = PipelineForms.WaitStepType, Name = "Page two",
                            Inputs =
                            {
                                ["Title"] = "Page two", [PipelineForms.NextPageInput] = "True",
                                ["Fields"] = PipelineForms.Serialize([new PipelineFormField { Label = "E-mail", Name = "email", Type = PipelineFormFieldTypes.Email, Required = true }]),
                            },
                            OutputVariables = { ["Result"] = "v-second" },
                        },
                        new PipelineGraphStep
                        {
                            Id = "log", Type = "Stomme.LogMessage", Name = "Log",
                            Inputs = { ["Level"] = "Information" },
                            InputExpressions = { ["Message"] = "Signed up: {{ first.name }} <{{ second.email }}>" },
                        },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "form", TargetId = "page-two" },
                        new PipelineGraphConnection { SourceId = "page-two", SourcePort = "Submitted", TargetId = "log" },
                    },
                };
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
                var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                using var visitor = GateDrive.NewClient(app, Host); // no sign-in

                // Page one: the person is sent straight on to the run's form address - a sibling of the form's own - which shows page two.
                var first = await visitor.PostAsync("/api/Pipelines/forms/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Ada" }));
                Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
                var location = first.Headers.Location!.OriginalString;
                output.WriteLine("page one -> " + location);
                Assert.Matches("^\\.\\./calls/[A-Za-z0-9_-]{43}/form$", location);
                var pageTwo = new Uri(new Uri("http://" + Host + "/api/Pipelines/forms/signup"), location).PathAndQuery;
                Assert.StartsWith("/" + PipelineCalls.Path, pageTwo);
                var shown = await visitor.GetAsync(pageTwo);
                var html = await shown.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, shown.StatusCode); // there at once: the redirect came once the run was stored
                Assert.Contains("<h1>Page two</h1>", html);
                Assert.Contains("name=\"email\"", html);

                // Page two, the last: thanked as soon as the run ends - not after the 30 seconds a next page would be waited for - on the
                // Form trigger's own thank-you, its Answer read from the published pipeline (n8n's form ending).
                var clock = Stopwatch.StartNew();
                var second = await visitor.PostAsync(pageTwo, new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "ada@example.com" }));
                Assert.Equal(HttpStatusCode.SeeOther, second.StatusCode);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"thanked after {clock.Elapsed.TotalSeconds:0.0} s");
                Assert.Equal("../../forms/signup?sent=1", second.Headers.Location?.OriginalString);
                var ending = new Uri(new Uri("http://" + Host + pageTwo), second.Headers.Location!.OriginalString).PathAndQuery;
                Assert.Equal("/api/Pipelines/forms/signup?sent=1", ending);
                Assert.Contains("Welcome aboard - see you soon.", await (await visitor.GetAsync(ending)).Content.ReadAsStringAsync());

                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    run = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!.SingleOrDefault();
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);
                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run!.Id}/log", output, "the run's log")).Body, Json)!;
                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.Message == "Signed up: Ada <ada@example.com>");
                Assert.Contains(log, line => line.EventName == "Next page");
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(pageTwo)).StatusCode); // answered: closed
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
