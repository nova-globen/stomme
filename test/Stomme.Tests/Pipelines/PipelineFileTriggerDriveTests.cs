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
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Local File Trigger as "File added", DRIVEN on an installed SQLite host: a pipeline watching 'inbox' for
    /// '*.csv' is published while a file is already there; a CSV put there through the Files page's own upload a moment later
    /// starts ONE run - by the real task, on its own interval - whose next step writes what it was handed into another folder,
    /// read back through the Files API; the file already there, and a file the pattern does not take, start nothing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineFileTriggerDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_file_uploaded_into_a_watched_folder_starts_one_run_that_reads_it()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["FileTriggers"] = new JsonObject { ["IntervalSeconds"] = 2, ["SettleSeconds"] = 0 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var made = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/files/folders", new PipelineFileFolderRequest { Path = "inbox" }, output, "make inbox");
                Assert.Equal(HttpStatusCode.OK, made.Status);
                Assert.Equal(HttpStatusCode.OK, (await UploadAsync(app, client, cookies, hostId, "inbox", "before.csv", "there first")).Status);

                var definitionId = await PublishAsync(app, client, cookies, hostId);
                // At once - no look has come round yet: the publish recorded what was there.
                Assert.Equal(HttpStatusCode.OK, (await UploadAsync(app, client, cookies, hostId, "inbox", "orders.csv", "a,b,c")).Status);
                Assert.Equal(HttpStatusCode.OK, (await UploadAsync(app, client, cookies, hostId, "inbox", "notes.txt", "not a csv")).Status);

                var runs = await UntilAsync(client, cookies, definitionId, found => found.Count == 1 && found[0].IsFinished, "one run, finished");
                Assert.Equal("Finished", runs[0].SubStatus);
                var written = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=" + Uri.EscapeDataString("seen/orders.csv.txt"), output, "what the run wrote");
                Assert.Equal((HttpStatusCode.OK, "add inbox/orders.csv 5"), (written.Status, written.Body));

                // Three more looks: the file already there, the one not matching and the one taken start nothing.
                await Task.Delay(TimeSpan.FromSeconds(6));
                Assert.Single(await RunsAsync(client, cookies, definitionId));

                // Whether the first look came before the upload is chance, so the above can hold without the record made at the
                // publish: the host has the handler that makes it.
                var handlers = await GateDrive.InTenantAsync(app, Host, (services, _) =>
                    Task.FromResult(services.GetServices<Stomme.Engine.Mediator.Contracts.INotificationHandler>().Select(handler => handler.GetType()).ToList()));
                Assert.Contains(typeof(Stomme.Modules.Pipelines.Triggers.StartWatchingOnPublish), handlers);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // The starter as the editor applies it - its graph saved and published: a CSV uploaded into inbox becomes a spreadsheet.
        [Fact]
        public async Task The_csv_put_in_a_folder_starter_makes_a_spreadsheet_of_a_csv_uploaded()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["FileTriggers"] = new JsonObject { ["IntervalSeconds"] = 2, ["SettleSeconds"] = 0 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "CSVs in" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var starter = PipelineStarters.Find("csv-put-in-a-folder")!.Graph();
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", starter, output, "graph")).Status);
                var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);

                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/files/folders", new PipelineFileFolderRequest { Path = "inbox" }, output, "make inbox")).Status);
                Assert.Equal(HttpStatusCode.OK, (await UploadAsync(app, client, cookies, hostId, "inbox", "orders.csv", "id,total\n1,10\n2,20\n")).Status);
                var runs = await UntilAsync(client, cookies, definitionId, found => found.Count == 1 && found[0].IsFinished, "one run, finished");
                Assert.Equal("Finished", runs[0].SubStatus);
                var folder = JsonSerializer.Deserialize<PipelineFolderView>((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files?folder=spreadsheets", output, "spreadsheets")).Body, Json)!;
                var made = Assert.Single(folder.Entries);
                Assert.Equal("orders.csv.xlsx", made.Name);
                Assert.True(made.Length > 0);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> PublishAsync(WebApplication app, HttpClient client, CookieContainer cookies, int hostId)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders in" }, output, "create");
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-file", Name = "file" } },
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "file", Type = PipelineFileEvent.StepType, Name = "An order file", Inputs = { ["Folder"] = "inbox", ["Pattern"] = "*.csv" },
                        OutputVariables = { ["Result"] = "v-file" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "write", Type = "Stomme.WriteTextFile", Name = "Note it",
                        InputExpressions = { ["Path"] = "seen/{{ file.name }}.txt", ["Content"] = "{{ file.event }} {{ file.path }} {{ file.size }}" },
                    },
                },
                Connections = { new PipelineGraphConnection { SourceId = "file", TargetId = "write" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
            var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
            Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
            return definitionId;
        }

        // A multipart form, the file under "file" - as the Files page's browser service sends it - with the caller's cookie and
        // the antiforgery header every write carries.
        private async Task<(HttpStatusCode Status, string Body)> UploadAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string folder, string name, string text)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/Pipelines/files?folder=" + Uri.EscapeDataString(folder));
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } };
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[upload {name}] POST {uri.PathAndQuery} -> {(int)response.StatusCode} {body}");
            return (response.StatusCode, body);
        }

        private async Task<List<PipelineRunView>> RunsAsync(HttpClient client, CookieContainer cookies, string definitionId) =>
            JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;

        private async Task<List<PipelineRunView>> UntilAsync(HttpClient client, CookieContainer cookies, string definitionId, Func<List<PipelineRunView>, bool> done, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var runs = await RunsAsync(client, cookies, definitionId);
                if (done(runs))
                {
                    return runs;
                }

                await Task.Delay(250);
            }

            throw new Xunit.Sdk.XunitException("Not reached: " + what);
        }
    }
}
