#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Respond to Webhook with headers and a binary file, DRIVEN over the real receiver on an installed SQLite host:
    /// a PDF taken into the pipeline folder through the Files API; a pipeline answering its webhook from the pipeline with that
    /// file, a status, a name to save it under and two headers; a signed call gets exactly that - the status, the PDF's type, an
    /// attachment named as said, the headers, the bytes.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RespondToWebhookDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Secret = "respond-drive-webhook-secret";

        [Fact]
        public async Task A_call_is_answered_with_a_file_of_the_pipeline_folder_its_status_and_headers()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = Secret, ["AnswerWithinSeconds"] = 20 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/files/folders", new PipelineFileFolderRequest { Path = "invoices" }, output, "make a folder")).Status);
                var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice 7, its bytes as uploaded");
                Assert.Equal(HttpStatusCode.OK, (await UploadAsync(app, client, cookies, hostId, "invoices", "7.pdf", pdf)).Status);

                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Invoice by webhook" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "Invoice asked for", Inputs = { ["WebhookName"] = "invoice", ["Answer"] = "FromThePipeline" } },
                        new PipelineGraphStep
                        {
                            Id = "reply", Type = "Stomme.RespondToWebhook", Name = "The invoice",
                            Inputs = { ["StatusCode"] = "201", ["File"] = "invoices/7.pdf", ["DownloadAs"] = "invoice-7.pdf", ["Headers"] = "Access-Control-Allow-Origin: *\nX-Invoice: 7" },
                        },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "hook", TargetId = "reply" } },
                };
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
                var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);

                using var caller = GateDrive.NewClient(app, Host);
                var body = Encoding.UTF8.GetBytes("{\"invoice\": 7}");
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/invoice")) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, PipelineWebhooks.ComputeSignature(Secret, body));
                using var response = await caller.SendAsync(request);
                var answered = await response.Content.ReadAsByteArrayAsync();
                output.WriteLine($"[call] {(int)response.StatusCode} {response.Content.Headers.ContentType} {response.Content.Headers.ContentDisposition}");

                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal(("attachment", "invoice-7.pdf"), (response.Content.Headers.ContentDisposition?.DispositionType, response.Content.Headers.ContentDisposition?.FileName?.Trim('"')));
                Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.Equal("7", Assert.Single(response.Headers.GetValues("X-Invoice")));
                Assert.Equal(pdf, answered);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // A multipart form, the file under "file" - as the Files page's browser service sends it - with the antiforgery header.
        private async Task<(HttpStatusCode Status, string Body)> UploadAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string folder, string name, byte[] bytes)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/Pipelines/files?folder=" + Uri.EscapeDataString(folder));
            using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[upload {name}] POST {uri.PathAndQuery} -> {(int)response.StatusCode} {text}");
            return (response.StatusCode, text);
        }
    }
}
