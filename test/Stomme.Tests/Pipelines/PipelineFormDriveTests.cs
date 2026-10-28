#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// BL-229 - n8n's Form trigger DRIVEN over the real receiver on an installed SQLite host: a published pipeline's form is a
    /// page anyone can open (its own policy, every text encoded); a submission judged by the fields is the page again with what is
    /// wrong (400), and an accepted one starts the pipeline with the answers and redirects to the thank-you (303); a robot's is
    /// thanked and dropped; a draft's form, an unknown name and a non-form body are not served. With Pipelines:Forms:Enabled off
    /// nothing is.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineFormDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private static readonly List<PipelineFormField> Fields =
        [
            new() { Label = "Your name", Name = "name", Required = true },
            new() { Label = "E-mail", Name = "email", Type = PipelineFormFieldTypes.Email, Required = true },
            new() { Label = "Topic", Type = PipelineFormFieldTypes.Dropdown, Options = ["Question", "Other"] },
            new() { Label = "How many", Name = "count", Type = PipelineFormFieldTypes.Number },
            new() { Label = "I agree", Name = "agree", Type = PipelineFormFieldTypes.Checkbox, Required = true },
        ];

        [Fact]
        public async Task A_published_form_is_served_judged_and_starts_its_pipeline_with_the_answers()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Forms"] = new JsonObject { ["Enabled"] = true } } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var contact = await SaveFormPipelineAsync(app, client, cookies, hostId, "contact", publish: true);
                await SaveFormPipelineAsync(app, client, cookies, hostId, "draft-only", publish: false);
                using var visitor = GateDrive.NewClient(app, Host); // no sign-in

                // The page: the form's own policy, every text the author wrote encoded, the name read in any case.
                var page = await visitor.GetAsync("/api/Pipelines/forms/CONTACT");
                var html = await page.Content.ReadAsStringAsync();
                output.WriteLine(html);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
                Assert.Equal(PipelineFormPage.ContentSecurityPolicy, page.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
                Assert.Contains("<h1>Contact &lt;us&gt;</h1>", html);
                Assert.DoesNotContain("<script", html);
                Assert.Contains("name=\"email\" required", html);
                Assert.Contains("<button type=\"submit\">Send it</button>", html);

                // n8n's prefilled fields: the address gives a field its value - encoded, as every value on the page is.
                var prefilled = await (await visitor.GetAsync("/api/Pipelines/forms/contact?name=Ada%20%3CL%3E&count=3&nobody=x")).Content.ReadAsStringAsync();
                Assert.Contains("value=\"Ada &lt;L&gt;\"", prefilled);
                Assert.Contains("value=\"3\"", prefilled);
                Assert.DoesNotContain("nobody", prefilled);

                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/forms/draft-only")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/forms/nobody")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/forms/not%20a%20name")).StatusCode);
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await visitor.PostAsync("/api/Pipelines/forms/contact", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);

                // A submission the fields refuse: the page again, with what was given and what is wrong; no run.
                var refused = await SubmitAsync(visitor, "contact", new() { ["name"] = "Ada", ["count"] = "three" });
                var refusedHtml = await refused.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains("value=\"Ada\"", refusedHtml);
                Assert.Contains("<p class=\"error\" id=\"field-1-error\">This is required.</p>", refusedHtml);
                Assert.Contains("<p class=\"error\" id=\"field-3-error\">Not a number.</p>", refusedHtml);
                Assert.Contains("<p class=\"error\" id=\"field-4-error\">Tick this to go on.</p>", refusedHtml);

                // A robot fills the field people never see: thanked, and nothing starts.
                var robot = await SubmitAsync(visitor, "contact", new() { ["name"] = "Bot", ["email"] = "bot@example.com", ["agree"] = "on", [PipelineFormPage.HoneypotField] = "http://spam" });
                Assert.Equal(HttpStatusCode.SeeOther, robot.StatusCode);
                await Task.Delay(1500); // had it been dispatched, its run would be there by now
                Assert.Empty(await RunsAsync(client, cookies, contact));

                // An accepted submission: the pipeline runs with the answers; the person is sent to the thank-you.
                var sent = await SubmitAsync(visitor, "contact", new() { ["name"] = "Ada", ["email"] = "ada@example.com", ["topic"] = "Question", ["count"] = "3", ["agree"] = "on" });
                Assert.Equal(HttpStatusCode.SeeOther, sent.StatusCode);
                Assert.Equal("?sent=1", sent.Headers.Location?.OriginalString);
                var run = await UntilFinishedAsync(client, cookies, contact);
                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run.Id}/log", output, "the run's log")).Body, Json)!;
                foreach (var line in log)
                {
                    output.WriteLine($"[log] {line.StepType} {line.EventName} {line.Message}");
                }

                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.Message == "Form: Ada <ada@example.com> Question 3 true");
                var thanks = await visitor.GetAsync("/api/Pipelines/forms/contact?sent=1");
                Assert.Contains("We write back within a day &amp; a half.", await thanks.Content.ReadAsStringAsync());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_forms_switched_off_no_form_is_served_or_taken()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var contact = await SaveFormPipelineAsync(app, client, cookies, hostId, "contact", publish: true);
                using var visitor = GateDrive.NewClient(app, Host);

                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/forms/contact")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(visitor, "contact", new() { ["name"] = "Ada", ["email"] = "ada@example.com", ["agree"] = "on" })).StatusCode);
                await Task.Delay(1500);
                Assert.Empty(await RunsAsync(client, cookies, contact));

                // The editor's trigger panel says so.
                var info = JsonSerializer.Deserialize<TriggerInfoView>((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/triggers/info?type=Stomme.Form&value=Contact", output, "trigger info")).Body, Json)!;
                Assert.Equal(("Form", false, "api/Pipelines/forms/contact"), (info.Kind, info.Enabled, info.Path));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> SaveFormPipelineAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int hostId, string name, bool publish)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Form " + name }, output, "create " + name);
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-answers", Name = "answers" } },
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "form", Type = PipelineForms.StepType, Name = "Contact form",
                        Inputs =
                        {
                            ["FormName"] = name,
                            ["Title"] = "Contact <us>",
                            ["Description"] = "Write to us. <script>alert(1)</script>",
                            ["Fields"] = PipelineForms.Serialize(Fields),
                            ["SubmitLabel"] = "Send it",
                            ["Answer"] = "We write back within a day & a half.",
                        },
                        OutputVariables = { ["Result"] = "v-answers" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "log", Type = "Stomme.LogMessage", Name = "Log",
                        Inputs = { ["Level"] = "Information" },
                        InputExpressions = { ["Message"] = "Form: {{ answers.name }} <{{ answers.email }}> {{ answers.topic }} {{ answers.count }} {{ answers.agree }}" },
                    },
                },
                Connections = { new PipelineGraphConnection { SourceId = "form", TargetId = "log" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph " + name)).Status);
            if (publish)
            {
                var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish " + name);
                Assert.Equal(HttpStatusCode.OK, published.Status);
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
            }

            return definitionId;
        }

        private static Task<HttpResponseMessage> SubmitAsync(HttpClient visitor, string name, Dictionary<string, string> answers) =>
            visitor.PostAsync("/api/Pipelines/forms/" + name, new FormUrlEncodedContent(answers));

        private async Task<List<PipelineRunView>> RunsAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId) =>
            JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;

        private async Task<PipelineRunView> UntilFinishedAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if ((await RunsAsync(client, cookies, definitionId)).FirstOrDefault() is { IsFinished: true } run)
                {
                    return run;
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException($"{definitionId} has no finished run.");
        }
    }
}
