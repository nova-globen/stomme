#nullable enable
using System;
using System.IO;
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
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the site's pipeline folder over the real HTTP surface of an installed host (SQLite - the folder is the file
    /// system's, whatever the store): the path a WebAssembly site's Files page and any outside caller take, which the page on an
    /// Interactive Server site does not (it asks the server's mediator directly). A multipart upload taken in and listed; the file
    /// given back byte for byte, as an attachment of no particular type; a name already there refused saying why; a request
    /// larger than a pipeline file may be stopped at the request's own bound (413) before any handler runs; a path out of the
    /// site's folder refused; someone who may not change the pipelines refused (403); a file deleted.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineFilesDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private const long MaxFileBytes = 4096;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task The_pipeline_folder_takes_lists_gives_back_and_deletes_files_over_http()
        {
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Files"] = new JsonObject { ["MaxFileBytes"] = MaxFileBytes } } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var made = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/files/folders", new PipelineFileFolderRequest { Path = "reports" }, output, "make a folder");
                Assert.Equal(HttpStatusCode.OK, made.Status);

                var content = Encoding.UTF8.GetBytes("customer,total\nAda,42\n");
                var uploaded = await UploadAsync(app, client, cookies, hostId, "reports", "q3.csv", content, "upload");
                Assert.Equal(HttpStatusCode.OK, uploaded.Status);
                Assert.Equal("reports/q3.csv", JsonSerializer.Deserialize<PipelineFileEntryView>(uploaded.Body, Json)!.Path);

                var listing = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files?folder=reports", output, "list");
                Assert.Equal(HttpStatusCode.OK, listing.Status);
                var folder = JsonSerializer.Deserialize<PipelineFolderView>(listing.Body, Json)!;
                Assert.Equal(MaxFileBytes, folder.MaxFileBytes);
                Assert.Equal(new[] { ("q3.csv", (long)content.Length) }, folder.Entries.Select(entry => (entry.Name, entry.Length)));

                // Given back as it was taken, to save - never opened as the site's own page.
                using (var download = await GetRawAsync(client, cookies, "/api/Pipelines/files/content?path=reports%2Fq3.csv"))
                {
                    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                    Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
                    Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
                    Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
                    Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
                    Assert.Equal("q3.csv", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
                }

                var again = await UploadAsync(app, client, cookies, hostId, "reports", "q3.csv", content, "the same name again");
                Assert.Equal(HttpStatusCode.BadRequest, again.Status);
                Assert.Contains("'q3.csv' is already in this folder", again.Body);

                // Past the file steps' bound and the form's room: stopped by the request's own bound, before any handler runs.
                var huge = await UploadAsync(app, client, cookies, hostId, "", "huge.bin", new byte[MaxFileBytes + 1024 * 1024 + 1], "too large");
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.Status);
                Assert.Contains("larger than the 4 KB a pipeline file may be", huge.Body);
                // Within the form's room but past the bound: the handler refuses it, saying why.
                var large = await UploadAsync(app, client, cookies, hostId, "", "large.bin", new byte[MaxFileBytes + 1], "larger than a file may be");
                Assert.Equal(HttpStatusCode.BadRequest, large.Status);
                Assert.Contains("'large.bin' is larger than the 4 KB a pipeline file may be.", large.Body);

                using (var outside = await GetRawAsync(client, cookies, "/api/Pipelines/files/content?path=..%2Fother%2Fsecret.txt"))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
                    Assert.Contains("resolves outside the site's folder", await outside.Content.ReadAsStringAsync());
                }

                using (var plainClient = GateDrive.NewClient(app, Host))
                {
                    var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                    Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Pipelines/files", output, "a plain user lists")).Status);
                    using var plainDownload = await GetRawAsync(plainClient, plainCookies, "/api/Pipelines/files/content?path=reports%2Fq3.csv");
                    Assert.Equal(HttpStatusCode.Forbidden, plainDownload.StatusCode);
                }

                var deleted = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, "/api/Pipelines/files?path=reports", null, output, "delete the folder");
                Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files?folder=reports", output, "the folder gone")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // A multipart form, the file under "file" - as the Files page's browser service sends it - with the caller's cookie and
        // the antiforgery header every write carries.
        private async Task<(HttpStatusCode Status, string Body)> UploadAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string folder, string name, byte[] bytes, string label)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/Pipelines/files?folder=" + Uri.EscapeDataString(folder));
            using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] POST {uri.PathAndQuery} -> {(int)response.StatusCode} {body}");
            return (response.StatusCode, body);
        }

        private static Task<HttpResponseMessage> GetRawAsync(HttpClient client, CookieContainer cookies, string path)
        {
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            return client.SendAsync(request);
        }
    }
}
