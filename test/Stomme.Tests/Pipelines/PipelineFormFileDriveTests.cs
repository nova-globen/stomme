#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// A form's file field (n8n's File field) DRIVEN over the real receiver on an installed SQLite host: the page sends
    /// multipart with a file input naming the kinds it takes; a submission without the file, with a file of another kind or one
    /// past <c>Pipelines:Forms:MaxFileBytes</c> is the page again with what is wrong and KEEPS NOTHING, a robot's is thanked and
    /// keeps nothing; an accepted one keeps the file in the site's pipeline folder, under <c>forms/{form}/{date}/</c>, its answer
    /// the path, name, size and type - and the bytes there are the bytes sent.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineFormFileDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private static readonly List<PipelineFormField> Fields =
        [
            new() { Label = "Your name", Name = "name", Required = true },
            new() { Label = "Invoice", Name = "invoice", Type = PipelineFormFieldTypes.File, Required = true, Accept = [".pdf"] },
            new() { Label = "Photo", Name = "photo", Type = PipelineFormFieldTypes.File },
        ];

        [Fact]
        public async Task A_forms_file_is_kept_in_the_site_folder_only_when_the_submission_is_accepted()
        {
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Forms"] = new JsonObject { ["Enabled"] = true, ["MaxFileBytes"] = 4096 } } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var definitionId = await PublishAsync(app, client, cookies, hostId);
                using var visitor = GateDrive.NewClient(app, Host); // no sign-in

                // The page sends multipart, the file fields as file inputs naming the kinds they take.
                var html = await (await visitor.GetAsync("/api/Pipelines/forms/invoices")).Content.ReadAsStringAsync();
                Assert.Contains("enctype=\"multipart/form-data\"", html);
                Assert.Contains("<input type=\"file\" id=\"field-1\" name=\"invoice\" required accept=\".pdf\">", html);
                Assert.Contains("<input type=\"file\" id=\"field-2\" name=\"photo\">", html);

                var pdf = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

                // Refused - no file, a file of another kind, a file too large: the page again with what is wrong, nothing kept.
                var missing = await SubmitAsync(visitor, ("name", "Ada"));
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                Assert.Contains("<p class=\"error\" id=\"field-1-error\">Choose a file.</p>", await missing.Content.ReadAsStringAsync());
                var wrongKind = await SubmitAsync(visitor, ("name", "Ada"), ("invoice", "setup.exe", pdf));
                Assert.Contains("<p class=\"error\" id=\"field-1-error\">Only .pdf files.</p>", await wrongKind.Content.ReadAsStringAsync());
                var tooLarge = await SubmitAsync(visitor, ("name", "Ada"), ("invoice", "big.pdf", new byte[5000]));
                var tooLargeHtml = await tooLarge.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
                Assert.Contains("<p class=\"error\" id=\"field-1-error\">At most 4 KB.</p>", tooLargeHtml);
                Assert.Contains("value=\"Ada\"", tooLargeHtml); // the answers kept on the page
                // A refusal for another reason keeps the file field's file nowhere either - and the page says to choose it again.
                var otherwiseRefused = await SubmitAsync(visitor, ("invoice", "invoice.pdf", pdf));
                Assert.Contains("Choose the file again", await otherwiseRefused.Content.ReadAsStringAsync());
                // A robot's: thanked, nothing kept.
                Assert.Equal(HttpStatusCode.SeeOther, (await SubmitAsync(visitor, ("name", "Bot"), ("invoice", "invoice.pdf", pdf), (PipelineFormPage.HoneypotField, "http://spam"))).StatusCode);
                await Task.Delay(1000);
                Assert.Empty(await RunsAsync(client, cookies, definitionId));
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files?folder=forms", output, "forms folder before")).Status);
                // Nor is any file left where files are held while they are read.
                var held = System.IO.Path.Combine(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Modules.Pipelines.Steps.PipelineStepOptions>>(app.Services).Value.Files.Root!, ".incoming");
                string[] Held() => System.IO.Directory.Exists(held) ? System.IO.Directory.GetFiles(held, "form-*") : Array.Empty<string>();
                Assert.Empty(Held());

                // Accepted: kept under forms/invoices/{date}/, the answer its path, name, size and type; the photo not given is null.
                var sent = await SubmitAsync(visitor, ("name", "Ada"), ("invoice", "Invoice 2026-10.pdf", pdf));
                Assert.Equal(HttpStatusCode.SeeOther, sent.StatusCode);
                var run = await UntilFinishedAsync(client, cookies, definitionId);
                var detail = JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/runs/" + run.Id, output, "the run")).Body)!;
                var started = JsonNode.Parse(detail["inputJson"]!.GetValue<string>())!;
                output.WriteLine("started with: " + started.ToJsonString());
                // The submission as the run keeps what it was started with: the answers, as their JSON or as its text.
                var submission = started[Modules.Pipelines.Steps.Form.SubmissionInputKey]!;
                var answersNode = submission is JsonValue answersText ? JsonNode.Parse(answersText.GetValue<string>())! : submission;
                var invoice = answersNode["invoice"]!;
                var path = invoice["path"]!.GetValue<string>();
                Assert.Matches($"^forms/invoices/{DateTime.UtcNow:yyyy-MM-dd}/[0-9a-f]{{8}}-Invoice-2026-10\\.pdf$", path);
                Assert.Equal(("Invoice 2026-10.pdf", 1000, "application/pdf"), (invoice["name"]!.GetValue<string>(), invoice["size"]!.GetValue<int>(), invoice["contentType"]!.GetValue<string>()));
                Assert.Null(answersNode["photo"]);
                Assert.Equal("Ada", answersNode["name"]!.GetValue<string>());

                // The bytes kept are the bytes sent; the folder holds that file alone.
                using (var download = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri("http://" + Host), "/api/Pipelines/files/content?path=" + Uri.EscapeDataString(path))))
                {
                    download.Headers.Add("Cookie", cookies.GetCookieHeader(new Uri("http://" + Host)));
                    var kept = await client.SendAsync(download);
                    Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
                    Assert.Equal(pdf, await kept.Content.ReadAsByteArrayAsync());
                }

                var day = JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/files?folder=forms/invoices/{DateTime.UtcNow:yyyy-MM-dd}", output, "the day's folder")).Body)!;
                Assert.Single(day["entries"]!.AsArray());
                Assert.Empty(Held()); // moved, not copied
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // A run waiting for a form (Wait for a form, at its call address) takes a file the same way: refused when of another kind,
        // kept under forms/answers/{date}/ when accepted, the run going on with the answer.
        [Fact]
        public async Task A_runs_waiting_form_takes_a_file_too()
        {
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Forms"] = new JsonObject { ["Enabled"] = true } } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Ask for a scan" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "address", Type = "Stomme.CallAddress", Name = "Address" },
                        new PipelineGraphStep
                        {
                            Id = "wait", Type = "Stomme.WaitForForm", Name = "Wait for the scan",
                            Inputs = { ["Title"] = "Send the scan", ["Fields"] = PipelineForms.Serialize([new PipelineFormField { Label = "Scan", Name = "scan", Type = PipelineFormFieldTypes.File, Required = true, Accept = [".png"] }]) },
                        },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "address", TargetId = "wait" } },
                };
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
                Assert.True(JsonNode.Parse((await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish")).Body)!["succeeded"]!.GetValue<bool>());
                var started = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), output, "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                // The form's address, as the Address step gave it, once the run waits.
                string? address = null;
                for (var attempt = 0; attempt < 150 && address is null; attempt++)
                {
                    var steps = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "steps");
                    if (steps.Status == HttpStatusCode.OK && JsonSerializer.Deserialize<List<PipelineStepRunView>>(steps.Body, Json)!.FirstOrDefault(step => step.StepId == "address")?.OutputsJson is { } outputs)
                    {
                        address = JsonNode.Parse(outputs)!["FormAddress"]?.GetValue<string>();
                    }

                    await Task.Delay(100);
                }

                var path = new Uri(address!).PathAndQuery;
                using var person = GateDrive.NewClient(app, Host); // no sign-in
                Assert.Contains("enctype=\"multipart/form-data\"", await (await person.GetAsync(path)).Content.ReadAsStringAsync());
                var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

                var refused = await PostAsync(person, path, ("scan", "scan.jpg", png));
                Assert.Contains("Only .png files.", await refused.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.SeeOther, (await PostAsync(person, path, ("scan", "scan.png", png))).StatusCode);

                for (var attempt = 0; attempt < 150; attempt++)
                {
                    if (JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/runs/" + runId, output, "the run")).Body)!["isFinished"]!.GetValue<bool>())
                    {
                        break;
                    }

                    await Task.Delay(100);
                }

                var waited = JsonSerializer.Deserialize<List<PipelineStepRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "steps")).Body, Json)!
                    .Single(step => step.StepId == "wait");
                Assert.Matches($"forms/answers/{DateTime.UtcNow:yyyy-MM-dd}/[0-9a-f]{{8}}-scan\\.png", waited.OutputsJson);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<HttpResponseMessage> PostAsync(HttpClient visitor, string path, (string Name, string FileName, byte[] Bytes) file)
        {
            var content = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(file.Bytes);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(bytes, file.Name, file.FileName);
            return visitor.PostAsync(path, content);
        }

        private async Task<string> PublishAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int hostId)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Invoices in" }, output, "create");
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "form", Type = PipelineForms.StepType, Name = "Send an invoice",
                        Inputs = { ["FormName"] = "invoices", ["Title"] = "Send us an invoice", ["Fields"] = PipelineForms.Serialize(Fields) },
                    },
                    new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "invoice in", ["Level"] = "Information" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "form", TargetId = "log" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
            var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
            Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
            return definitionId;
        }

        // A multipart submission: text parts, and file parts (name, file name, bytes) - a PDF's type for a .pdf, else octet-stream.
        private static Task<HttpResponseMessage> SubmitAsync(HttpClient visitor, params object[] parts)
        {
            var content = new MultipartFormDataContent();
            foreach (var part in parts)
            {
                switch (part)
                {
                    case ValueTuple<string, string> text:
                        content.Add(new StringContent(text.Item2), text.Item1);
                        break;
                    case ValueTuple<string, string, byte[]> file:
                        var bytes = new ByteArrayContent(file.Item3);
                        bytes.Headers.ContentType = new MediaTypeHeaderValue(file.Item2.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream");
                        content.Add(bytes, file.Item1, file.Item2);
                        break;
                }
            }

            return visitor.PostAsync("/api/Pipelines/forms/invoices", content);
        }

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
