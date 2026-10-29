#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tests.Connectivity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Datasets
{
    /// <summary>The upload drives share one shape: a folder the Host may view, a file uploaded through the framework's own file API, then <c>POST api/Datasets/upload</c>.</summary>
    internal static class UploadDrive
    {
        public const string Host = "localhost";

        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static Task<Folder> NewFolderAsync(WebApplication app, string role = RoleNames.Admin) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var folders = sp.GetRequiredService<IFolderRepository>();
                var root = (await folders.GetFoldersAsync(alias.SiteId)).Single(folder => folder.ParentId == null);
                var name = "datasets-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var created = await folders.AddFolderAsync(new Folder
                {
                    SiteId = alias.SiteId, ParentId = root.FolderId, Name = name, Type = FolderTypes.Private, Path = name + "/", Order = 1, ImageSizes = "", Capacity = 0, IsSystem = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.Browse, role, true),
                        new Permission(PermissionNames.View, role, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true),
                    },
                });
                Directory.CreateDirectory(await folders.GetFolderPathAsync(created));
                return created;
            });

        /// <summary>The framework's upload API, as the Files admin sends it: one part, the folder by id.</summary>
        public static async Task<int> UploadAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, Folder folder, string fileName, byte[] content, ITestOutputHelper output)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(folder.FolderId.ToString()), "folder");
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "formfile", fileName);
            var uri = new Uri(new Uri("http://" + Host), "/api/File/upload");
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            request.Headers.TryAddWithoutValidation("PartCount", "1");
            request.Headers.TryAddWithoutValidation("TotalParts", "1");
            var response = await client.SendAsync(request);
            output.WriteLine($"[upload {fileName} {content.Length} bytes] -> {(int)response.StatusCode}");
            Assert.True(response.IsSuccessStatusCode, $"upload of {fileName}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IFileRepository>().GetFilesAsync(folder.FolderId)).Single(row => row.Name == fileName).FileId);
        }

        public static Task<(HttpStatusCode Status, string Body)> RegisterAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, int fileId, string? name, ITestOutputHelper output, DataClassification classification = DataClassification.Internal) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, "/api/Datasets/upload", new UploadDatasetRequest { FileId = fileId, Name = name, Classification = classification }, output, "register upload " + fileId);
    }

    /// <summary>
    /// CSV, XLSX and JSON uploads on installed hosts on PostgreSQL and RavenDB: each file
    /// goes through the framework's own file API into a folder the Host may view, then <c>POST api/Datasets/upload</c>
    /// registers a dataset from it with the file's stem as the name, the origin <c>file:&lt;id&gt;</c>, the inferred
    /// schema and the FULL row count; the same file registered again refreshes the one row; an XLSX renamed .csv is
    /// refused with nothing registered.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DatasetsUploadDriveTests(ITestOutputHelper output)
    {
        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Csv_xlsx_and_json_uploads_become_datasets_with_their_full_row_counts(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), UploadDrive.Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, UploadDrive.Host);
                var cookies = await GateDrive.LoginAsync(app, client, UploadDrive.Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, UploadDrive.Host, GateDrive.HostUsername);
                var folder = await UploadDrive.NewFolderAsync(app);

                var csv = new StringBuilder("sku,amount,email\n");
                for (var i = 0; i < 120; i++)
                {
                    csv.Append("A").Append(i).Append(',').Append(i * 10).Append(",user").Append(i).Append("@example.com\n");
                }

                var csvId = await UploadDrive.UploadAsync(app, client, cookies, hostId, folder, "sales.csv", Encoding.UTF8.GetBytes(csv.ToString()), output);
                var xlsxId = await UploadDrive.UploadAsync(app, client, cookies, hostId, folder, "people.xlsx", UploadedDatasetParserTests.BuildXlsx(new[] { "age", "name" }, new[] { new[] { "30", "Ada" }, new[] { "40", "Bo" } }), output);
                var jsonId = await UploadDrive.UploadAsync(app, client, cookies, hostId, folder, "orders.json", Encoding.UTF8.GetBytes("[ { \"id\": \"1\", \"amount\": \"9.5\" }, { \"id\": \"2\", \"amount\": \"12\" } ]"), output);

                var sales = await UploadDrive.RegisterAsync(app, client, cookies, hostId, csvId, null, output);
                Assert.Equal(HttpStatusCode.OK, sales.Status);
                var salesView = JsonSerializer.Deserialize<DatasetDescriptor>(sales.Body, UploadDrive.Json)!;
                Assert.Equal(("sales", OriginKind.Upload, "file:" + csvId, 120, DataClassification.Restricted), (salesView.Name, salesView.OriginKind, salesView.OriginReference, salesView.Summary.RowCountEstimate, salesView.Classification)); // the e-mail column made it PII
                Assert.Equal(new[] { "sku", "amount", "email" }, salesView.Schema.Columns.Select(column => column.Name));
                Assert.DoesNotContain("@example.com", sales.Body);

                var people = JsonSerializer.Deserialize<DatasetDescriptor>((await UploadDrive.RegisterAsync(app, client, cookies, hostId, xlsxId, "People", output)).Body, UploadDrive.Json)!;
                Assert.Equal(("People", 2, ColumnDataType.Number), (people.Name, people.Summary.RowCountEstimate, people.Schema.Columns.Single(column => column.Name == "age").DataType));

                var orders = JsonSerializer.Deserialize<DatasetDescriptor>((await UploadDrive.RegisterAsync(app, client, cookies, hostId, jsonId, null, output)).Body, UploadDrive.Json)!;
                Assert.Equal(("orders", 2, "currency"), (orders.Name, orders.Summary.RowCountEstimate, orders.Schema.Columns.Single(column => column.Name == "amount").SemanticType));

                // The same file registered again is the same dataset (the origin is the file).
                var again = JsonSerializer.Deserialize<DatasetDescriptor>((await UploadDrive.RegisterAsync(app, client, cookies, hostId, jsonId, "Orders renamed", output)).Body, UploadDrive.Json)!;
                Assert.Equal((orders.Id, "Orders renamed"), (again.Id, again.Name));
                Assert.Equal(3, JsonSerializer.Deserialize<List<DatasetDescriptor>>((await GateDrive.GetAsync(client, cookies, UploadDrive.Host, "/api/Datasets", output, "list")).Body, UploadDrive.Json)!.Count);

                // An XLSX renamed .csv: uploaded (the file manager takes it), refused as a dataset, nothing registered.
                var renamedId = await UploadDrive.UploadAsync(app, client, cookies, hostId, folder, "renamed.csv", UploadedDatasetParserTests.BuildXlsx(new[] { "a" }, new[] { new[] { "1" } }), output);
                Assert.Equal(HttpStatusCode.BadRequest, (await UploadDrive.RegisterAsync(app, client, cookies, hostId, renamedId, null, output)).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await UploadDrive.RegisterAsync(app, client, cookies, hostId, 987654, null, output)).Status);
                Assert.Equal(3, JsonSerializer.Deserialize<List<DatasetDescriptor>>((await GateDrive.GetAsync(client, cookies, UploadDrive.Host, "/api/Datasets", output, "list after refusals")).Body, UploadDrive.Json)!.Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }

    /// <summary>
    /// The bombs, DRIVEN with crafted files through the framework's file API
    /// and the upload command on an installed SQLite host: an XLSX with an oversized shared-string part, one with
    /// thousands of parts, one with a high-ratio part, an XLSX renamed .csv, a 60 MB JSON body and a deeply nested JSON
    /// document - each refused with nothing registered.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class UploadedDatasetBombTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task Every_crafted_file_is_refused_and_nothing_is_registered()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), UploadDrive.Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, UploadDrive.Host);
                var cookies = await GateDrive.LoginAsync(app, client, UploadDrive.Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, UploadDrive.Host, GateDrive.HostUsername);
                var folder = await UploadDrive.NewFolderAsync(app);

                var bombs = new (string Name, byte[] Bytes)[]
                {
                    ("oversized-sst.xlsx", XlsxWithParts(("xl/sharedStrings.xml", Encoding.UTF8.GetBytes("<sst>" + new string('a', 20 * 1024 * 1024) + "</sst>")))),
                    ("thousands-of-parts.xlsx", XlsxWithParts(Enumerable.Range(0, 2000).Select(i => ($"xl/part{i}.xml", Encoding.UTF8.GetBytes("<x/>"))).ToArray())),
                    ("high-ratio.xlsx", XlsxWithParts(("xl/worksheets/sheet1.xml", new byte[48 * 1024 * 1024]))),
                    ("renamed.csv", UploadedDatasetParserTests.BuildXlsx(new[] { "a" }, new[] { new[] { "1" } })),
                    ("sixty-mb.json", SixtyMegabytesOfJson()),
                    ("deep.json", Encoding.UTF8.GetBytes(new string('[', 200) + new string(']', 200))),
                };

                foreach (var (name, bytes) in bombs)
                {
                    var fileId = await UploadDrive.UploadAsync(app, client, cookies, hostId, folder, name, bytes, output);
                    var response = await UploadDrive.RegisterAsync(app, client, cookies, hostId, fileId, null, output);
                    output.WriteLine($"[{name}] {bytes.Length} bytes -> {(int)response.Status} {GateDrive.Truncate(response.Body)}");
                    Assert.Equal(HttpStatusCode.BadRequest, response.Status);
                }

                Assert.Empty(JsonSerializer.Deserialize<List<DatasetDescriptor>>((await GateDrive.GetAsync(client, cookies, UploadDrive.Host, "/api/Datasets", output, "list")).Body, UploadDrive.Json)!);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A ZIP with the XLSX signature and whatever parts the case needs - enough to pass the file manager and reach the parser's checks.</summary>
        private static byte[] XlsxWithParts(params (string Name, byte[] Content)[] parts)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var types = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(types.Open()))
                {
                    writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
                }

                foreach (var (name, content) in parts)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(content, 0, content.Length);
                }
            }

            return ms.ToArray();
        }

        private static byte[] SixtyMegabytesOfJson()
        {
            var row = Encoding.UTF8.GetBytes("{\"n\":\"" + new string('x', 100) + "\"},");
            var target = 60 * 1024 * 1024;
            using var ms = new MemoryStream(target + 64);
            ms.WriteByte((byte)'[');
            while (ms.Length < target)
            {
                ms.Write(row, 0, row.Length);
            }

            ms.Write(Encoding.UTF8.GetBytes("{\"n\":\"end\"}]"));
            return ms.ToArray();
        }
    }

    /// <summary>
    /// The managed-file reader on an installed SQLite host, acting as a settable current user:
    /// a file of another site is not found; a file in a folder the user may not view is refused; a file over the
    /// server's upload cap is refused; a file the user may view opens as a stream of its bytes.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ManagedFileReaderTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task A_cross_site_id_a_folder_without_view_and_a_file_over_the_cap_are_refused_and_a_viewable_file_opens()
        {
            var user = new TestCurrentUser { UserId = 0, SiteId = 1 };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), UploadDrive.Host, output, colocateModuleAggregates: false,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Files"] = new System.Text.Json.Nodes.JsonObject { ["MaxUploadBytes"] = 4096 } });
            var app = await GateDrive.InstallAsync(scratch, output, builder =>
            {
                builder.Services.RemoveAll<ICurrentUser>();
                builder.Services.AddScoped<ICurrentUser>(_ => user);
            });
            try
            {
                var alias = await GateDrive.AliasAsync(app, UploadDrive.Host);
                user.UserId = await GateDrive.UserIdAsync(app, UploadDrive.Host, GateDrive.HostUsername);
                user.RoleSet.Add(RoleNames.Host);

                var viewable = await UploadDrive.NewFolderAsync(app);
                var hidden = await UploadDrive.NewFolderAsync(app, role: "Nobody");
                var small = await PlaceAsync(app, viewable, "small.csv", "a,b\n1,2\n");
                var secret = await PlaceAsync(app, hidden, "secret.csv", "a\n1\n");
                var big = await PlaceAsync(app, viewable, "big.csv", "a\n" + new string('1', 5000) + "\n");

                await GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
                {
                    var reader = sp.GetRequiredService<Stomme.Modules.Datasets.Application.IManagedFileReader>();
                    using (var file = await reader.OpenAsync(small))
                    {
                        Assert.Equal("small.csv", file.Name);
                        Assert.Equal("a,b\n1,2\n", await new StreamReader(file.Content).ReadToEndAsync());
                    }

                    var unauthorised = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.OpenAsync(secret));
                    Assert.Contains("may not view", unauthorised.Message);
                    var over = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.OpenAsync(big));
                    Assert.Contains("cap", over.Message);
                    var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => reader.OpenAsync(987654));
                    Assert.Contains("does not exist", missing.Message);
                    return 0;
                });

                // Another site's file id: the file is real, the REQUEST's site is another, so it does not exist for it -
                // the same reader over a SiteState routed to a different site (a folder cannot be moved to a site that
                // does not exist: the foreign key refuses it, measured).
                await GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
                {
                    var elsewhere = new Stomme.Modules.Datasets.Application.ManagedFileReader(
                        sp.GetRequiredService<IFileRepository>(), sp.GetRequiredService<Stomme.Security.IUserPermissions>(), sp.GetRequiredService<ICurrentUser>(),
                        sp.GetRequiredService<Stomme.Infrastructure.IFileIngestionGuard>(), new SiteState { Alias = new Alias { AliasId = alias.AliasId, TenantId = alias.TenantId, SiteId = alias.SiteId + 1, Name = "elsewhere.localhost" } });
                    var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => elsewhere.OpenAsync(small));
                    Assert.Contains("does not exist", missing.Message);
                    return 0;
                });

                // No user at all: a headless caller is refused, whatever the folder grants.
                user.UserId = null;
                await GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
                {
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sp.GetRequiredService<Stomme.Modules.Datasets.Application.IManagedFileReader>().OpenAsync(big));
                    return 0;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A file row with its bytes on disk, placed directly (the framework's own file rows, not the upload path).</summary>
        private static Task<int> PlaceAsync(WebApplication app, Folder folder, string name, string content) =>
            GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
            {
                var files = sp.GetRequiredService<IFileRepository>();
                var folders = sp.GetRequiredService<IFolderRepository>();
                var bytes = Encoding.UTF8.GetBytes(content);
                var path = Path.Combine(await folders.GetFolderPathAsync(folder), name);
                await System.IO.File.WriteAllBytesAsync(path, bytes);
                var row = await files.AddFileAsync(new Models.File { FolderId = folder.FolderId, Name = name, Extension = Path.GetExtension(name).TrimStart('.'), Size = bytes.Length, ImageHeight = 0, ImageWidth = 0 });
                return row.FileId;
            });
    }
}
