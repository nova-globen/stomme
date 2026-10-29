using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Infrastructure;
using SearchQuery = Stomme.Models.SearchQuery;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace NovaGloben.Module.KnowledgeBase.Drive
{
    /// <summary>
    /// The exemplar through the REAL host, on each store an installation can choose: the packed module
    /// is unpacked by the InstallationManager, discovered by convention, placed on its page and - its second module - on a
    /// page NESTED under it, and driven over HTTP: anonymously (the pages carry the modules; a visitor reads only what is
    /// published and cannot write) and as the host (a category and an article created through the API, stamped with the
    /// signed-in author, a duplicate slug refused, re-read by any casing of its slug; the draft invisible to a visitor until
    /// it is published; an edit that never moves its address; a second publish refused; the publish metered and the site's
    /// administrators notified through the module's template, and a quota of one refusing the next publish with 429), its
    /// tables on the relational stores. The discovery seams: the module's help topics resolved by a visitor, its
    /// published article found by the site's search and listed in the sitemap (the draft in neither), and its MCP surface
    /// absent until an operator turns its flag on, then reading and searching the published articles only. The
    /// data duties, in a second test of its own: see <see cref="The_exemplar_takes_part_in_the_hosts_data_duties"/>;
    /// the operations, in a third: see <see cref="The_exemplar_takes_part_in_the_hosts_operations"/>. The host's
    /// module template, in a fourth: see <see cref="The_host_creates_a_module_from_its_template_that_builds_installs_and_answers"/>.
    /// PostgreSQL and RavenDB FAIL rather than skip when unreachable: a drive that quietly shrinks proves nothing.
    /// </summary>
    [Collection("KnowledgeBaseDrive")] // the drives install into the shared harness bin and set a process-wide override path
    public sealed class KnowledgeBaseInstallDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "Kn0wledge$Drive!2026"; // a generated test value for this scratch install only
        private const string WriterUsername = "writer";
        private const string WriterPassword = "Wr1ter$Drive!2026"; // a generated test value for this scratch install only
        private const string BackupPassphrase = "knowledge base drive passphrase"; // a scratch archive's only

        private static readonly string[] ModuleAssemblyDlls =
        {
            "NovaGloben.Module.KnowledgeBase.Client.Stomme.dll",
            "NovaGloben.Module.KnowledgeBase.Server.Stomme.dll",
            "NovaGloben.Module.KnowledgeBase.Shared.Stomme.dll",
        };

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

        public static TheoryData<string> Stores() => new() { "SQLite", "PostgreSQL", "RavenDB" };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_exemplar_installs_places_its_page_and_persists_through_its_api(string storeName)
        {
            var store = Store.Resolve(storeName);
            store.RequireReachable();
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new ScratchInstall(binPath, store);
            output.WriteLine($"[{storeName}] content root: {scratch.ContentRoot}");

            var logs = new HostLogCapture();
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Logging.AddProvider(logs);
            });
            try
            {
                // ---- installed and discovered -------------------------------------------------------------------------------
                Assert.False(File.Exists(scratch.StagedNupkg), "The InstallationManager did not consume the package.");
                Assert.All(ModuleAssemblyDlls, dll => Assert.True(File.Exists(Path.Combine(binPath, dll)), $"{dll} was not extracted into the bin."));
                Assert.All(ModuleAssemblyDlls.Select(Path.GetFileNameWithoutExtension), name =>
                    Assert.Contains(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == name));

                var installing = Task.Run(() => Program.InstallAsync(app));
                Assert.True(await Task.WhenAny(installing, Task.Delay(InstallTimeout)) == installing, "The installer did not finish in time.");
                var install = await installing;
                Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"Installer: {install.Message}");
                await app.StartAsync();
                output.WriteLine($"[{storeName}] installed, discovered, site installed.");

                using var client = NewClient(app);

                // ---- anonymous: the page carries the module; the API answers no data -------------------------------------------
                // The site renders modules interactively without prerendering, so the page's HTML carries the module's container
                // and title but not its content (measured) - the content is the API's to prove here, and a browser's.
                var anonymousPage = await client.GetAsync("/knowledge-base");
                var anonymousHtml = await anonymousPage.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, anonymousPage.StatusCode);
                Assert.Contains("<span class=\"app-moduletitle\">Knowledge Base</span>", anonymousHtml);
                Assert.Contains("rendermode: Interactive", anonymousHtml);

                // The NESTED page carries the package's second module and sits under the flat one.
                var nestedHtml = await (await client.GetAsync("/knowledge-base/categories")).Content.ReadAsStringAsync();
                Assert.Contains("<span class=\"app-moduletitle\">Categories</span>", nestedHtml);
                var (parentId, childParentId) = await PagesAsync(app);
                Assert.Equal(parentId, childParentId);
                // Both placements carry the package's keys, so each page's own checks see them - the nested page's did not until
                // its definition declared no keys; found by clicking it in a browser, invisible to this HTTP drive).
                Assert.True(await PlacementCarriesKeyAsync(app, "NovaGloben.Module.KnowledgeBase,", "knowledge-base", "view"), "The flat page's module carries no knowledge-base:view row.");
                Assert.True(await PlacementCarriesKeyAsync(app, "NovaGloben.Module.KnowledgeBase.Categories,", "knowledge-base", "view"), "The nested page's module carries no knowledge-base:view row.");

                // A visitor reads the knowledge base (the view key is every visitor's) - nothing is published yet - and cannot write.
                Assert.Equal("[]", await AnonymousGetAsync(client, "/api/KnowledgeBase/articles", HttpStatusCode.OK));
                var anonymousWrite = await client.PostAsJsonAsync("/api/KnowledgeBase/articles", new { title = "Anonymous", body = "Nope." });
                output.WriteLine($"[{storeName}] anonymous POST /api/KnowledgeBase/articles -> {(int)anonymousWrite.StatusCode}");
                Assert.False(anonymousWrite.IsSuccessStatusCode, "An anonymous caller drafted an article.");

                // ---- the host: a category and an article through the API ---------------------------------------------------------
                var cookies = await LoginAsync(app, client);
                var hostId = await UserIdAsync(app, HostUsername);

                var category = await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/categories", new { name = "Getting started" }, HttpStatusCode.OK);
                Assert.Equal("getting-started", (string?)category["slug"]);
                var categoryId = (string)category["id"]!;

                var article = await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/articles",
                    new { title = "Reset your password", body = "Open your profile and choose Change password.", categoryId }, HttpStatusCode.OK);
                Assert.Equal(("reset-your-password", hostId, "Draft"), ((string?)article["slug"], (int?)article["authorUserId"], StatusName(article["status"])));

                await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/articles",
                    new { title = "Reset Your Password!", body = "A second one with the same slug." }, HttpStatusCode.Conflict);

                // ---- a fresh request re-reads it - through the category, and by any casing of its slug ---------------------
                var inCategory = JsonSerializer.Deserialize<JsonArray>((await GetAsync(client, cookies, $"/api/KnowledgeBase/articles?categoryId={categoryId}")).Body)!;
                Assert.Equal("Reset your password", (string?)Assert.Single(inCategory)!["title"]);
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, cookies, "/api/KnowledgeBase/articles/RESET-Your-Password")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, cookies, "/api/KnowledgeBase/articles/no-such-article")).Status);

                // ---- a draft is the editors' until it is published; an edit never moves the address ---------------
                Assert.Equal("[]", await AnonymousGetAsync(client, "/api/KnowledgeBase/articles", HttpStatusCode.OK));
                await AnonymousGetAsync(client, "/api/KnowledgeBase/articles/reset-your-password", HttpStatusCode.NotFound);
                var articleId = (string)article["id"]!;
                var edited = await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Put, $"/api/KnowledgeBase/articles/{articleId}",
                    new { title = "Resetting a forgotten password", body = "Choose Forgot password on the sign-in page.", categoryId }, HttpStatusCode.OK);
                Assert.Equal(("Resetting a forgotten password", "reset-your-password"), ((string?)edited["title"], (string?)edited["slug"]));
                var published = await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{articleId}/publish", new { }, HttpStatusCode.OK);
                Assert.Equal("Published", StatusName(published["status"]));
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{articleId}/publish", new { }, HttpStatusCode.BadRequest);
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{Guid.NewGuid()}/publish", new { }, HttpStatusCode.NotFound);
                Assert.Contains("Resetting a forgotten password", await AnonymousGetAsync(client, "/api/KnowledgeBase/articles", HttpStatusCode.OK));
                Assert.Contains("Choose Forgot password", await AnonymousGetAsync(client, "/api/KnowledgeBase/articles/reset-your-password", HttpStatusCode.OK));

                // ---- the publish was metered, the administrators told, and a quota refuses the next one ---------------
                var (template, notified, metered) = await PublishEvidenceAsync(app, Guid.Parse(articleId));
                Assert.True(template, "The module's notification template was not contributed to the site.");
                Assert.Equal(hostId, Assert.Single(notified));
                Assert.Equal(1, metered);
                await DefineQuotaAsync(app, 1);
                var second = await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/articles", new { title = "Change your e-mail address", body = "Open your profile." }, HttpStatusCode.OK);
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{(string)second["id"]!}/publish", new { }, (HttpStatusCode)429);
                Assert.DoesNotContain("Change your e-mail address", await AnonymousGetAsync(client, "/api/KnowledgeBase/articles", HttpStatusCode.OK));

                // ---- the discovery seams ---------------------------------------------------------------------------
                // The second article is a draft (the quota refused its publish): it must reach none of them.
                const string publishedUrl = "/knowledge-base/!/article/reset-your-password";
                const string draftSlug = "change-your-e-mail-address";
                Assert.Equal(draftSlug, (string?)second["slug"]);

                // Help: the module's topics were reconciled into the site; a visitor resolves the page's topic and the edit
                // control's, whose anchor is the real control's type name.
                var pageHelp = JsonNode.Parse(await AnonymousGetAsync(client, "/api/Help/topic?anchor=page:knowledge-base&locale=en", HttpStatusCode.OK))!;
                Assert.Equal("The knowledge base", (string?)pageHelp["title"]);
                Assert.Contains("<strong>knowledge base</strong>", (string?)pageHelp["safeHtml"]);
                var editControl = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "NovaGloben.Module.KnowledgeBase.Client.Stomme")
                    .GetType("NovaGloben.Module.KnowledgeBase.Edit", throwOnError: true)!;
                Assert.True(HelpKeys.TryModuleAnchor(editControl.AssemblyQualifiedName, out var editAnchor));
                var editHelp = JsonNode.Parse(await AnonymousGetAsync(client, "/api/Help/topic?anchor=" + Uri.EscapeDataString(editAnchor) + "&locale=en", HttpStatusCode.OK))!;
                Assert.Equal("Writing an article", (string?)editHelp["title"]);

                // Search: the host's SCHEDULED index job (every minute) asks the module's manager (ISearchable). The drive waits
                // for a run that began after the draft existed - it does not run the job itself: a second run beside the
                // scheduler's resets the same index under it (measured: two empty indexes and a set of save errors in three runs).
                // Then a visitor finds the published article at its own address, and nothing of the draft's.
                var siteId = await SiteIdAsync(app);
                var draftCreated = DateTimeOffset.Parse((string)second["createdUtc"]!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
                output.WriteLine($"[{storeName}] index run after the draft: {await WaitForIndexRunAfterAsync(app, siteId, draftCreated)}");
                var found = await SearchAsync(app, client, siteId, "forgotten");
                Assert.Contains(found, result => (string?)result["url"] == publishedUrl && (string?)result["entityName"] == "KnowledgeBaseArticle");
                Assert.DoesNotContain(await SearchAsync(app, client, siteId, "address"), result => (string?)result["entityName"] == "KnowledgeBaseArticle");

                // The sitemap: the host asks the same manager (ISitemap, now asynchronous); the published article is listed at its
                // address, the draft is not.
                var sitemap = await AnonymousGetAsync(client, "/sitemap.xml", HttpStatusCode.OK);
                Assert.Contains("<loc>http://" + Host + publishedUrl + "</loc>", sitemap);
                Assert.DoesNotContain(draftSlug, sitemap);

                // MCP: the flag is off by default, so the host's catalog carries nothing of the module's...
                Assert.DoesNotContain("search_knowledge_base", await McpToolNamesAsync(client, cookies, "tools, flag off"));
                // ...until an operator turns it on (the host's feature API), and then only the PUBLISHED articles are read.
                await Send<JsonObject>(app, client, cookies, hostId, "/api/Feature/knowledge-base.mcp", new { enabled = true }, HttpStatusCode.OK);
                Assert.Contains("search_knowledge_base", await McpToolNamesAsync(client, cookies, "tools, flag on"));
                var mcpResources = await McpAsync(client, cookies, "resources/list", null, "resources");
                Assert.Contains("stomme://knowledge-base/articles", mcpResources?["result"]?["resources"]?.AsArray().Select(resource => (string?)resource?["uri"]) ?? Enumerable.Empty<string?>());
                var mcpTemplates = await McpAsync(client, cookies, "resources/templates/list", null, "templates");
                Assert.Contains("stomme://knowledge-base/articles/{slug}", mcpTemplates?["result"]?["resourceTemplates"]?.AsArray().Select(template => (string?)template?["uriTemplate"]) ?? Enumerable.Empty<string?>());
                var listText = McpText(await McpAsync(client, cookies, "resources/read", new JsonObject { ["uri"] = "stomme://knowledge-base/articles" }, "read the list"), "contents");
                Assert.Contains("reset-your-password", listText);
                Assert.DoesNotContain(draftSlug, listText);
                var articleText = McpText(await McpAsync(client, cookies, "resources/read", new JsonObject { ["uri"] = "stomme://knowledge-base/articles/reset-your-password" }, "read one"), "contents");
                Assert.Contains("Choose Forgot password", articleText);
                var draftRead = await McpPostAsync(client, cookies, McpBody("resources/read", new JsonObject { ["uri"] = "stomme://knowledge-base/articles/" + draftSlug }), "read the draft");
                Assert.DoesNotContain("Open your profile", draftRead.Body);
                var searchText = McpText(await McpAsync(client, cookies, "tools/call", new JsonObject { ["name"] = "search_knowledge_base", ["arguments"] = new JsonObject { ["query"] = "password" } }, "search"), "content");
                Assert.Contains("reset-your-password", searchText);
                var draftSearch = McpText(await McpAsync(client, cookies, "tools/call", new JsonObject { ["name"] = "search_knowledge_base", ["arguments"] = new JsonObject { ["query"] = "address" } }, "search for the draft"), "content");
                Assert.DoesNotContain(draftSlug, draftSearch);

                store.VerifySchema(scratch, output);
                output.WriteLine($"[{storeName}] VERDICT: install, discover, place, gate, create, refuse, re-read - driven.");
            }
            finally
            {
                foreach (var entry in logs.Drain())
                {
                    output.WriteLine($"[{storeName}] host log: {entry}");
                }

                try { await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); } catch { }
                try { await app.DisposeAsync(); } catch { }
                foreach (var dll in ModuleAssemblyDlls)
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, Path.ChangeExtension(dll, ".pdb")));
                }
            }
        }

        /// <summary>
        /// The host's data duties, each driven through the host's own surface: the module's content
        /// exported and re-imported (IPortable; an import drafts, never publishes), the published articles through the export
        /// queue, a writer's articles in their personal data package and in their erasure (the draft
        /// deleted, the published article kept and attributed to nobody), and both aggregates in a backup with no module code,
        /// restored across a host restart.
        /// </summary>
        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_exemplar_takes_part_in_the_hosts_data_duties(string storeName)
        {
            var store = Store.Resolve(storeName);
            store.RequireReachable();
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new ScratchInstall(binPath, store);
            output.WriteLine($"[{storeName}] content root: {scratch.ContentRoot}");
            var logs = new HostLogCapture();
            var app = await BootAsync(scratch, logs);
            try
            {
                using var client = NewClient(app);
                var cookies = await LoginAsync(app, client);
                var hostId = await UserIdAsync(app, HostUsername);
                var siteId = await SiteIdAsync(app);

                // The host's category and published article; a writer - an Administrator of the site - drafts one article
                // and publishes another in that category.
                var category = await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/categories", new { name = "Accounts" }, HttpStatusCode.OK);
                var categoryId = (string)category["id"]!;
                var hostArticle = await Send<JsonObject>(app, client, cookies, hostId, "/api/KnowledgeBase/articles",
                    new { title = "Change your e-mail address", body = "Open your profile and choose Change e-mail.", categoryId }, HttpStatusCode.OK);
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{(string)hostArticle["id"]!}/publish", new { }, HttpStatusCode.OK);

                var writerId = await AddWriterAsync(app);
                using var writerClient = NewClient(app);
                var writerCookies = await LoginAsync(app, writerClient, WriterUsername, WriterPassword);
                await Send<JsonObject>(app, writerClient, writerCookies, writerId, "/api/KnowledgeBase/articles",
                    new { title = "Writing a good title", body = "Short and plain." }, HttpStatusCode.OK);
                var writerPublished = await Send<JsonObject>(app, writerClient, writerCookies, writerId, "/api/KnowledgeBase/articles",
                    new { title = "Choosing a category", body = "One category per article.", categoryId }, HttpStatusCode.OK);
                await Send<JsonObject>(app, writerClient, writerCookies, writerId, HttpMethod.Post, $"/api/KnowledgeBase/articles/{(string)writerPublished["id"]!}/publish", new { }, HttpStatusCode.OK);

                // ---- module content: exported through the host's module export, re-imported with one article added -------
                var (moduleId, pageId) = await PlacementAsync(app);
                var exported = await GetAsync(client, cookies, $"/api/Module/export?moduleid={moduleId}&pageid={pageId}");
                Assert.Equal(HttpStatusCode.OK, exported.Status);
                var wrapper = JsonNode.Parse(exported.Body)!.AsObject();
                var content = JsonNode.Parse((string)wrapper["Content"]!)!.AsObject();
                var exportedSlugs = content["articles"]!.AsArray().Select(article => (string?)article!["slug"]).ToList();
                Assert.Equal(new[] { "change-your-e-mail-address", "writing-a-good-title", "choosing-a-category" }.OrderBy(slug => slug), exportedSlugs.OrderBy(slug => slug));
                content["articles"]!.AsArray().Add(new JsonObject
                {
                    ["title"] = "Signing in with a passkey",
                    ["slug"] = "signing-in-with-a-passkey",
                    ["body"] = "Arrived by import.",
                    ["category"] = "accounts",
                    ["status"] = "Published",
                });
                wrapper["Content"] = content.ToJsonString();
                Assert.True(await Send<bool>(app, client, cookies, hostId, HttpMethod.Post, $"/api/Module/import?moduleid={moduleId}&pageid={pageId}", wrapper.ToJsonString(), HttpStatusCode.OK));
                var imported = JsonNode.Parse((await GetAsync(client, cookies, "/api/KnowledgeBase/articles/signing-in-with-a-passkey")).Body)!;
                Assert.Equal(("Draft", hostId, categoryId), (StatusName(imported["status"]), (int?)imported["authorUserId"], (string?)imported["categoryId"]));
                Assert.Equal(4, JsonSerializer.Deserialize<JsonArray>((await GetAsync(client, cookies, "/api/KnowledgeBase/articles")).Body)!.Count);

                // ---- the export source and the writer's data package, both built by the host's jobs -------------------------
                var exportId = await Send<string>(app, client, cookies, hostId, "/api/DataExport", new { sourceKey = "knowledge-base.articles", format = "Csv" }, HttpStatusCode.OK);
                var package = await Send<JsonObject>(app, client, cookies, hostId, "/api/Portability/subject", new { username = WriterUsername }, HttpStatusCode.OK);
                await UntilAsync(async () => (string?)JsonNode.Parse((await GetAsync(client, cookies, $"/api/DataExport/{exportId}")).Body)?["status"] == "Ready", "the export is Ready");
                var csv = await GetAsync(client, cookies, $"/api/DataExport/{exportId}/download");
                Assert.Equal(HttpStatusCode.OK, csv.Status);
                Assert.Contains("Change your e-mail address", csv.Body);
                Assert.Contains("Choosing a category", csv.Body);
                Assert.DoesNotContain("Writing a good title", csv.Body);
                Assert.DoesNotContain("Signing in with a passkey", csv.Body);

                var packageId = (string)package["id"]!;
                await UntilAsync(async () => JsonSerializer.Deserialize<JsonArray>((await GetAsync(client, cookies, "/api/Portability")).Body)!
                    .Any(row => (string?)row!["id"] == packageId && (string?)row["status"] == "Ready"), "the writer's package is Ready");
                var packageBody = (await GetAsync(client, cookies, $"/api/Portability/{packageId}/download")).Body;
                // the package files each entity under its reference type
                Assert.Contains("\"entityType\": \"knowledge-base-article\"", packageBody);
                Assert.Contains("Writing a good title", packageBody);
                Assert.Contains("Choosing a category", packageBody);
                Assert.DoesNotContain("Change your e-mail address", packageBody);

                // ---- the writer's erasure: the draft deleted, the published article kept and attributed to nobody -----------
                await Send<JsonNode?>(app, client, cookies, hostId, HttpMethod.Delete, $"/api/User/{writerId}?siteid={siteId}", new { }, HttpStatusCode.OK);
                await UntilAsync(async () =>
                {
                    var articles = JsonSerializer.Deserialize<JsonArray>((await GetAsync(client, cookies, "/api/KnowledgeBase/articles")).Body)!;
                    return articles.All(article => (string?)article!["slug"] != "writing-a-good-title")
                        && articles.Any(article => (string?)article!["slug"] == "choosing-a-category" && (int?)article["authorUserId"] == 0);
                }, "the writer's erasure has run");

                // ---- a backup: both aggregates are in it with no module code, and a restore across a restart brings back -----
                // ---- the article as it was at the snapshot ----------------------------------------------------------------------
                var snapshot = await Send<JsonObject>(app, client, cookies, hostId, "/api/Backup", new { passphrase = BackupPassphrase }, HttpStatusCode.Accepted);
                var snapshotId = (string)snapshot["snapshotId"]!;
                await UntilAsync(async () => await SnapshotStatusAsync(client, cookies, snapshotId) == "Available", "the snapshot is Available");
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Put, $"/api/KnowledgeBase/articles/{(string)hostArticle["id"]!}",
                    new { title = "Changed after the snapshot", body = "Open your profile and choose Change e-mail.", categoryId }, HttpStatusCode.OK);
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Post, $"/api/Backup/{snapshotId}/restore", new { passphrase = BackupPassphrase }, HttpStatusCode.Accepted);
                await UntilAsync(async () => await SnapshotStatusAsync(client, cookies, snapshotId) == "Staged", "the restore is staged");
                Assert.Contains(await ParticipantsAsync(app), name => name == "module-aggregate:NovaGloben.Module.KnowledgeBase.Domain.Article");
                Assert.Contains(await ParticipantsAsync(app), name => name == "module-aggregate:NovaGloben.Module.KnowledgeBase.Domain.Category");

                await StopAsync(app);
                app = await BootAsync(scratch, logs);
                using var afterRestore = NewClient(app);
                var restored = JsonNode.Parse(await AnonymousGetAsync(afterRestore, "/api/KnowledgeBase/articles/change-your-e-mail-address", HttpStatusCode.OK))!;
                Assert.Equal("Change your e-mail address", (string?)restored["title"]);
                output.WriteLine($"[{storeName}] VERDICT: content export/import, export source, data package, erasure, backup and restore - driven.");
            }
            finally
            {
                foreach (var entry in logs.Drain())
                {
                    output.WriteLine($"[{storeName}] host log: {entry}");
                }

                await StopAsync(app);
                foreach (var dll in ModuleAssemblyDlls)
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, Path.ChangeExtension(dll, ".pdb")));
                }
            }
        }

        // Builds the host on the scratch content root, runs its installer (a fresh install, or on a restart the upgrade path
        // that applies a staged restore) and starts it.
        private static async Task<WebApplication> BootAsync(ScratchInstall scratch, HostLogCapture logs)
        {
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Logging.AddProvider(logs);
            });
            var installing = Task.Run(() => Program.InstallAsync(app));
            Assert.True(await Task.WhenAny(installing, Task.Delay(InstallTimeout)) == installing, "The installer did not finish in time.");
            var install = await installing;
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"Installer: {install.Message}");
            await app.StartAsync();
            return app;
        }

        private static async Task StopAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }

        // Polls a condition the host's scheduled jobs (every minute) will make true, for up to four minutes.
        private static async Task UntilAsync(Func<Task<bool>> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!await condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        // A second user of the site, made an Administrator (so they hold knowledge-base:edit and :publish) before they sign in.
        private static async Task<int> AddWriterAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            var writer = await scope.ServiceProvider.GetRequiredService<Stomme.Managers.IUserManager>().AddUser(new Stomme.Models.User
            {
                Username = WriterUsername,
                Password = WriterPassword,
                Email = "kb-writer@example.com",
                DisplayName = "Writer",
                SiteId = alias.SiteId,
                EmailConfirmed = true,
            }) ?? throw new InvalidOperationException("The writer was not created.");
            var administrators = (await scope.ServiceProvider.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId)).Single(role => role.Name == RoleNames.Admin);
            await scope.ServiceProvider.GetRequiredService<IUserRoleRepository>().AddUserRoleAsync(new Stomme.Models.UserRole { UserId = writer.UserId, RoleId = administrators.RoleId });
            return writer.UserId;
        }

        // The flat page's placement of the module: the module id and the page id the host's module export and import take.
        private static async Task<(int ModuleId, int PageId)> PlacementAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            var placement = (await scope.ServiceProvider.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                .Single(pageModule => pageModule.Module.ModuleDefinitionName.StartsWith("NovaGloben.Module.KnowledgeBase,", StringComparison.Ordinal));
            return (placement.ModuleId, placement.PageId);
        }

        private async Task<string?> SnapshotStatusAsync(HttpClient client, CookieContainer cookies, string snapshotId) =>
            JsonSerializer.Deserialize<JsonArray>((await GetAsync(client, cookies, "/api/Backup")).Body)!
                .Where(row => (string?)row!["id"] == snapshotId).Select(row => (string?)row!["status"]).SingleOrDefault();

        // The names of the backup participants the host composed - the module registered none of its own.
        private static async Task<IReadOnlyList<string>> ParticipantsAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            await Task.CompletedTask;
            return scope.ServiceProvider.GetServices<Stomme.Backup.IBackupParticipant>().Select(participant => participant.Name).ToList();
        }

        /// <summary>
        /// The module in the host's operations, on an install with the demo switch on: the demo articles
        /// a visitor meets on a new site, seeded once - an operator's edit survives a restart and nothing is seeded twice - and
        /// the stale-drafts probe on the host's health page, Degraded for a draft untouched past the threshold the override
        /// file sets.
        /// </summary>
        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_exemplar_takes_part_in_the_hosts_operations(string storeName)
        {
            var store = Store.Resolve(storeName);
            store.RequireReachable();
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new ScratchInstall(binPath, store, new JsonObject
            {
                ["DemoContent"] = new JsonObject { ["Enabled"] = true },
                ["KnowledgeBase"] = new JsonObject { ["StaleDraftDays"] = 10 },
            });
            output.WriteLine($"[{storeName}] content root: {scratch.ContentRoot}");
            var logs = new HostLogCapture();
            var app = await BootAsync(scratch, logs);
            try
            {
                // ---- demo content: a visitor meets three published articles in one category --------------------------------
                using var visitor = NewClient(app);
                var demo = JsonSerializer.Deserialize<JsonArray>(await AnonymousGetAsync(visitor, "/api/KnowledgeBase/articles", HttpStatusCode.OK))!;
                Assert.Equal(new[] { "finding-an-article", "suggesting-a-change", "welcome-to-the-knowledge-base" },
                    demo.Select(article => (string?)article!["slug"]).OrderBy(slug => slug, StringComparer.Ordinal).ToArray());
                Assert.All(demo, article => Assert.Equal(("Published", 0), (StatusName(article!["status"]), (int?)article["authorUserId"])));
                var demoCategory = Assert.Single(JsonSerializer.Deserialize<JsonArray>(await AnonymousGetAsync(visitor, "/api/KnowledgeBase/categories", HttpStatusCode.OK))!);
                Assert.Equal("getting-started", (string?)demoCategory!["slug"]);
                Assert.Equal(0L, await MeteredAsync(app)); // a seed is not a publish: nothing metered

                // An operator edits a demo article; a restart does not put the demo back, nor seed anything twice.
                using var client = NewClient(app);
                var cookies = await LoginAsync(app, client);
                var hostId = await UserIdAsync(app, HostUsername);
                var welcome = demo.Single(article => (string?)article!["slug"] == "welcome-to-the-knowledge-base")!;
                await Send<JsonObject>(app, client, cookies, hostId, HttpMethod.Put, $"/api/KnowledgeBase/articles/{(string)welcome["id"]!}",
                    new { title = "Welcome to our help centre", body = "Our own words now.", categoryId = (string?)welcome["categoryId"] }, HttpStatusCode.OK);
                await StopAsync(app);
                app = await BootAsync(scratch, logs);
                using var afterRestart = NewClient(app);
                var reread = JsonSerializer.Deserialize<JsonArray>(await AnonymousGetAsync(afterRestart, "/api/KnowledgeBase/articles", HttpStatusCode.OK))!;
                Assert.Equal(3, reread.Count);
                Assert.Equal("Welcome to our help centre", (string?)reread.Single(article => (string?)article!["slug"] == "welcome-to-the-knowledge-base")!["title"]);

                // ---- health: the stale-drafts probe, over a draft untouched for 45 days and a fresh one ----------------------
                using var host = NewClient(app);
                var hostCookies = await LoginAsync(app, host);
                await AddOldDraftAsync(app, hostId, DateTime.UtcNow.AddDays(-45));
                await Send<JsonObject>(app, host, hostCookies, hostId, "/api/KnowledgeBase/articles", new { title = "A fresh draft", body = "Written today." }, HttpStatusCode.OK);
                JsonNode? probe = null;
                await UntilAsync(async () =>
                {
                    var run = await SendRawAsync(app, host, hostCookies, hostId, HttpMethod.Post, "/api/Health/run");
                    if (run != HttpStatusCode.OK && run != HttpStatusCode.Conflict)
                    {
                        throw new InvalidOperationException($"api/Health/run answered {(int)run}.");
                    }

                    var dashboard = JsonNode.Parse((await GetAsync(host, hostCookies, "/api/Health")).Body)!;
                    probe = dashboard["subjects"]!.AsArray().FirstOrDefault(subject => ((string?)subject!["subject"] ?? string.Empty).Contains("knowledge-base.stale-drafts", StringComparison.Ordinal));
                    return probe is not null && StateName(probe["state"]) == "Degraded";
                }, "the stale-drafts probe reports the old draft");
                Assert.Contains("1 of 2 draft(s) untouched for 10 days", (string?)probe!["description"]);
                output.WriteLine($"[{storeName}] VERDICT: demo content seeded once and kept across a restart, the stale-drafts probe on the health page - driven.");
            }
            finally
            {
                foreach (var entry in logs.Drain())
                {
                    output.WriteLine($"[{storeName}] host log: {entry}");
                }

                await StopAsync(app);
                foreach (var dll in ModuleAssemblyDlls)
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, Path.ChangeExtension(dll, ".pdb")));
                }
            }
        }

        private static string StateName(JsonNode? state) => state?.GetValueKind() == JsonValueKind.Number
            ? ((int)state switch { 0 => "Healthy", 1 => "Degraded", _ => "Unhealthy" })
            : (string?)state ?? string.Empty;

        // A draft last touched `touchedUtc`. No API back-dates an article, and the drive does not compile against the module
        // (it installs the package at runtime), so it reaches the LOADED module's domain by reflection: Article.Draft, then the
        // host's storage-blind repository for that aggregate.
        private static async Task AddOldDraftAsync(WebApplication app, int authorId, DateTime touchedUtc)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            var tenantId = scope.ServiceProvider.GetRequiredService<Stomme.Tenancy.Abstractions.ITenantContext>().CurrentTenantId!;
            var articleType = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "NovaGloben.Module.KnowledgeBase.Server.Stomme")
                .GetType("NovaGloben.Module.KnowledgeBase.Domain.Article", throwOnError: true)!;
            var draft = articleType.GetMethod("Draft")!.Invoke(null, new object?[] { Guid.NewGuid(), tenantId, alias.SiteId, null, "A forgotten draft", null, "Started long ago.", authorId, touchedUtc })!;
            var repositoryType = typeof(Stomme.SharedKernel.Domain.IRepository<,>).MakeGenericType(articleType, typeof(Guid));
            var repository = scope.ServiceProvider.GetRequiredService(repositoryType);
            await (Task)repositoryType.GetMethod("AddAsync")!.Invoke(repository, new object?[] { draft, CancellationToken.None })!;
            await scope.ServiceProvider.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
        }

        // The site's usage rows for the module's publish metric.
        private static async Task<long> MeteredAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return (await scope.ServiceProvider.GetRequiredService<Stomme.Metering.Persistence.IUsageRecordRepository>().ListBySiteAsync(alias.SiteId))
                .Where(record => record.Metric == "knowledge-base.articles-published").Sum(record => record.Quantity);
        }

        private static async Task<HttpStatusCode> SendRawAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path)
        {
            var (token, antiforgeryCookie) = Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(new { }) };
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri) + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            return (await client.SendAsync(request)).StatusCode;
        }

        /// <summary>
        /// The host's "Create Module" end to end, on the External template regenerated in the exemplar's
        /// shape: the host generates a module from its template (the page's POST api/ModuleDefinition), the generated module
        /// builds and packs with nothing but the .NET SDK against the slim SDK, the host installs the package on its next
        /// start, and the module's API answers - refusing a visitor, storing an entry for the host. SQLite only: the generated
        /// module is storage-blind exactly as the exemplar is, which the other tests drive on three stores.
        /// </summary>
        [Fact]
        public async Task The_host_creates_a_module_from_its_template_that_builds_installs_and_answers()
        {
            var store = Store.Resolve("SQLite");
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new ScratchInstall(binPath, store, nested: true);
            var repository = RepositoryRoot();
            CopyDirectory(Path.Combine(repository, "src", "Stomme.Server", "wwwroot", "Modules", "Templates"), Path.Combine(scratch.ContentRoot, "wwwroot", "Modules", "Templates"));
            output.WriteLine($"content root: {scratch.ContentRoot}");
            var logs = new HostLogCapture();
            var app = await BootAsync(scratch, logs);
            var generatedDlls = new[] { "Acme.Module.FieldNotes.Client.Stomme.dll", "Acme.Module.FieldNotes.Server.Stomme.dll", "Acme.Module.FieldNotes.Shared.Stomme.dll" };
            try
            {
                // ---- the host generates the module, as its Create Module page does ---------------------------------------------
                using (var client = NewClient(app))
                {
                    var cookies = await LoginAsync(app, client);
                    var hostId = await UserIdAsync(app, HostUsername);
                    var created = await Send<JsonObject>(app, client, cookies, hostId, "/api/ModuleDefinition",
                        new { owner = "Acme", name = "FieldNotes", description = "Notes taken in the field.", template = "External", version = "10.75.2", moduleDefinitionName = "[Owner].Module.[Module]" },
                        HttpStatusCode.OK);
                    Assert.Equal("Acme.Module.FieldNotes.Manager.FieldNotesManager, Acme.Module.FieldNotes.Server.Stomme", (string?)created["serverManagerType"]);
                }

                // Generated two folders above the content root, beside the host - as a developer's host writes it beside its repo.
                var module = Path.Combine(scratch.Top!, "Acme.Module.FieldNotes");
                Assert.True(File.Exists(Path.Combine(module, "Acme.Module.FieldNotes.slnx")), "The module was not generated where the host writes it.");
                Assert.Equal(File.ReadAllBytes(Path.Combine(repository, "src", "Stomme.Server", "wwwroot", "Modules", "Templates", "External", "Package", "icon.png")),
                    File.ReadAllBytes(Path.Combine(module, "Package", "icon.png")));
                var generatedConfig = File.ReadAllText(Path.Combine(module, "nuget.config"));
                const string frameworkSource = "<add key=\"stomme\" value=\"https://api.nuget.org/v3/index.json\" />";
                Assert.Contains(frameworkSource, generatedConfig);

                // The generated module restores the slim SDK from the framework's feed; this drive points it at the SDK packed
                // from THIS tree (examples/local-feed), so it builds against the framework under test and needs no network feed.
                File.WriteAllText(Path.Combine(module, "nuget.config"), generatedConfig.Replace(frameworkSource, $"<add key=\"stomme\" value=\"{Path.Combine(repository, "examples", "local-feed")}\" />"));

                // ---- it builds and packs with nothing but the .NET SDK -----------------------------------------------------------
                var (exit, log) = await RunAsync("dotnet", "pack Package/Acme.Module.FieldNotes.Package.csproj -c Release -nologo -nodeReuse:false", module, TimeSpan.FromMinutes(6));
                output.WriteLine(log.Length > 4000 ? log.Substring(log.Length - 4000) : log);
                Assert.True(exit == 0, "dotnet pack of the generated module failed.");
                var nupkg = Assert.Single(Directory.GetFiles(Path.Combine(module, "Package"), "Acme.Module.FieldNotes.*.nupkg"));

                // ---- the host installs it on its next start, and its API answers ---------------------------------------------------
                await StopAsync(app);
                File.Copy(nupkg, Path.Combine(scratch.PackagesFolder, Path.GetFileName(nupkg)), overwrite: true);
                app = await BootAsync(scratch, logs);
                Assert.All(generatedDlls, dll => Assert.True(File.Exists(Path.Combine(binPath, dll)), $"{dll} was not installed into the bin."));

                using var visitor = NewClient(app);
                await AnonymousGetAsync(visitor, "/api/FieldNotes", HttpStatusCode.Forbidden); // the view key is registered users'
                using var host = NewClient(app);
                var hostCookies = await LoginAsync(app, host);
                var hostUserId = await UserIdAsync(app, HostUsername);
                var entry = await Send<JsonObject>(app, host, hostCookies, hostUserId, "/api/FieldNotes", new { text = "Saw a heron at the lake." }, HttpStatusCode.OK);
                Assert.Equal(hostUserId, (int?)entry["authorUserId"]);
                await Send<JsonObject>(app, host, hostCookies, hostUserId, "/api/FieldNotes", new { text = "  " }, HttpStatusCode.BadRequest);
                var entries = (await GetAsync(host, hostCookies, "/api/FieldNotes")).Body;
                Assert.Contains("Saw a heron at the lake.", entries);
                output.WriteLine("VERDICT: generated from the template, packed with the .NET SDK alone, installed, gated and storing - driven.");
            }
            finally
            {
                foreach (var entry in logs.Drain())
                {
                    output.WriteLine($"host log: {entry}");
                }

                await StopAsync(app);
                foreach (var dll in ModuleAssemblyDlls.Concat(generatedDlls))
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, Path.ChangeExtension(dll, ".pdb")));
                }
            }
        }

        /// <summary>
        /// On a WebAssembly site the host
        /// streams the module's Client assembly (the definition's AssemblyName) and its Shared one (its Dependencies) to the
        /// browser, byte for byte what the host loaded - and NEVER the Server assembly.
        /// </summary>
        [Fact]
        public async Task A_webassembly_site_streams_the_client_and_shared_assemblies_and_never_the_server_one()
        {
            var store = Store.Resolve("SQLite");
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            // TestMode: the host lists the assemblies by NAME rather than by a hashed file name, so they can be found here.
            using var scratch = new ScratchInstall(binPath, store, new JsonObject { ["Runtime"] = "WebAssembly", ["TestMode"] = "true" });
            var logs = new HostLogCapture();
            var app = await BootAsync(scratch, logs);
            try
            {
                using var client = NewClient(app);
                var list = JsonSerializer.Deserialize<List<string>>(await AnonymousGetAsync(client, "/api/Installation/list", HttpStatusCode.OK))!;
                output.WriteLine("api/Installation/list -> " + string.Join(", ", list));
                static string? Find(IEnumerable<string> entries, string assembly) =>
                    entries.FirstOrDefault(entry => entry.StartsWith(assembly + ".", StringComparison.Ordinal) && entry.EndsWith(".dll", StringComparison.Ordinal));
                var clientEntry = Find(list, "NovaGloben.Module.KnowledgeBase.Client.Stomme") ?? throw new InvalidOperationException("The Client assembly is not streamed.");
                var sharedEntry = Find(list, "NovaGloben.Module.KnowledgeBase.Shared.Stomme") ?? throw new InvalidOperationException("The Shared assembly is not streamed.");
                Assert.Null(Find(list, "NovaGloben.Module.KnowledgeBase.Server.Stomme"));

                var zip = await client.GetByteArrayAsync($"/api/Installation/load?list={Uri.EscapeDataString(clientEntry + "," + sharedEntry)}");
                using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
                var streamed = archive.Entries.Single(entry => entry.FullName == clientEntry);
                using var bytes = new MemoryStream();
                using (var stream = streamed.Open())
                {
                    await stream.CopyToAsync(bytes);
                }

                Assert.Equal(File.ReadAllBytes(Path.Combine(binPath, "NovaGloben.Module.KnowledgeBase.Client.Stomme.dll")), bytes.ToArray());
                Assert.Contains(archive.Entries, entry => entry.FullName == sharedEntry);
                output.WriteLine($"VERDICT: Client ({bytes.Length} bytes, as loaded) and Shared stream to the browser; Server never.");
            }
            finally
            {
                foreach (var entry in logs.Drain())
                {
                    output.WriteLine($"host log: {entry}");
                }

                await StopAsync(app);
                foreach (var dll in ModuleAssemblyDlls)
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, Path.ChangeExtension(dll, ".pdb")));
                }
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root (Stomme.slnx) was not found above the drive's bin.");
        }

        private static void CopyDirectory(string source, string target)
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }

        private static async Task<(int Exit, string Log)> RunAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
        {
            var start = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cancel = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancel.Token);
            return (process.ExitCode, await stdout + await stderr);
        }

        private async Task<string> AnonymousGetAsync(HttpClient client, string path, HttpStatusCode expected)
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"anonymous GET {path} -> {(int)response.StatusCode} ({body.Length} chars)");
            Assert.Equal(expected, response.StatusCode);
            return body;
        }

        // What a publish left behind: the contributed template, the notification's recipients, the meter's count.
        private static async Task<(bool Template, IReadOnlyList<int> Notified, long Metered)> PublishEvidenceAsync(WebApplication app, Guid articleId)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<Stomme.Infrastructure.ITenantManager>().SetAliasAsync(alias);
            var template = await scope.ServiceProvider.GetRequiredService<Stomme.Notifications.Persistence.INotificationTemplateRepository>()
                .GetByKeyAsync(alias.SiteId, "knowledge-base.article-published") is not null;
            var requests = (await scope.ServiceProvider.GetRequiredService<Stomme.Notifications.Persistence.INotificationRequestRepository>().ListBySiteAsync(alias.SiteId))
                .Where(request => request.TemplateKey == "knowledge-base.article-published").ToList();
            var notified = requests.SelectMany(request => request.Recipients).Select(recipient => int.Parse(recipient.Address, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var metered = (await scope.ServiceProvider.GetRequiredService<Stomme.Metering.Persistence.IUsageRecordRepository>().ListBySiteAsync(alias.SiteId))
                .Where(record => record.Metric == "knowledge-base.articles-published").Sum(record => record.Quantity);
            return (template, notified, metered);
        }

        // A monthly quota for the publish metric on the site, as an operator would define it (the Metering page's command).
        private static async Task DefineQuotaAsync(WebApplication app, long limit)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<Stomme.Infrastructure.ITenantManager>().SetAliasAsync(alias);
            var tenantId = scope.ServiceProvider.GetRequiredService<Stomme.Tenancy.Abstractions.ITenantContext>().CurrentTenantId!;
            const string metric = "knowledge-base.articles-published";
            var quota = Stomme.Modules.Metering.Domain.QuotaAggregate.Define(
                Stomme.Modules.Metering.Domain.QuotaIds.For(tenantId, alias.SiteId, metric, Stomme.Modules.Metering.Domain.QuotaPeriod.Monthly),
                tenantId, alias.SiteId, metric, Stomme.Modules.Metering.Domain.QuotaPeriod.Monthly, limit, 80, DateTime.UtcNow);
            quota.Repair(1, new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)); // the publish above
            await scope.ServiceProvider.GetRequiredService<Stomme.Metering.Persistence.IQuotaRepository>().AddAsync(quota);
            await scope.ServiceProvider.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
        }

        // Whether a placement of the given module definition carries a key's rows in its permission list - what the module's
        // client-side IsAuthorizedPermission reads.
        private static async Task<bool> PlacementCarriesKeyAsync(WebApplication app, string definitionPrefix, string resource, string operation)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            var placement = (await scope.ServiceProvider.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                .Single(pageModule => pageModule.Module.ModuleDefinitionName.StartsWith(definitionPrefix, StringComparison.Ordinal));
            return placement.Module.PermissionList?.Any(row => row.EntityName == resource && row.PermissionName == operation) == true;
        }

        // The flat page's id and the nested page's parent id, from the site's page repository.
        private static async Task<(int ParentId, int? ChildParentId)> PagesAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<Stomme.Infrastructure.ITenantManager>().SetAliasAsync(alias);
            var pages = scope.ServiceProvider.GetRequiredService<IPageRepository>();
            var parent = await pages.GetPageAsync("knowledge-base", alias.SiteId) ?? throw new InvalidOperationException("No knowledge-base page.");
            var child = await pages.GetPageAsync("knowledge-base/categories", alias.SiteId) ?? throw new InvalidOperationException("No knowledge-base/categories page.");
            return (parent.PageId, child.ParentId);
        }

        // Waits for the host's scheduled search index job to finish a run on the site that BEGAN after `after`: the job records
        // each run's start as the site's Search_LastIndexedOn (in the host's culture, to the second).
        private static async Task<DateTime> WaitForIndexRunAfterAsync(WebApplication app, int siteId, DateTime after)
        {
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (true)
            {
                using (var scope = app.Services.CreateScope())
                {
                    var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.");
                    await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
                    var setting = await scope.ServiceProvider.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, siteId, "Search_LastIndexedOn");
                    if (setting is not null && Convert.ToDateTime(setting.SettingValue) > after)
                    {
                        return Convert.ToDateTime(setting.SettingValue);
                    }
                }

                Assert.True(DateTime.UtcNow < deadline, "The host's search index job did not run on the site within four minutes.");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        private static async Task<int> SiteIdAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host) ?? throw new InvalidOperationException("No alias for the host.")).SiteId;
        }

        // A visitor's search through the host's API: the results it would be shown, already trimmed by permission.
        private async Task<IReadOnlyList<JsonNode>> SearchAsync(WebApplication app, HttpClient client, int siteId, string keywords)
        {
            var (token, antiforgeryCookie) = Antiforgery(app, null);
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/SearchResults")
            {
                Content = JsonContent.Create(new SearchQuery { SiteId = siteId, Keywords = keywords, FromDate = DateTime.MinValue, ToDate = DateTime.MaxValue, PageIndex = 0, PageSize = 20 }),
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"anonymous search '{keywords}' -> {(int)response.StatusCode} {body}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(body)?["results"]?.AsArray().Where(result => result is not null).Select(result => result!).ToList() ?? new List<JsonNode>();
        }

        // ---- MCP over the host's endpoint, as the signed-in host (the framework suite's McpRpc, trimmed) --------------------

        private static JsonObject McpBody(string method, JsonObject? parameters)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
            if (parameters is not null)
            {
                body["params"] = parameters;
            }

            return body;
        }

        private async Task<(HttpStatusCode Status, string Body)> McpPostAsync(HttpClient client, CookieContainer cookies, JsonObject body, string label)
        {
            var uri = new Uri(new Uri("http://" + Host), "/mcp/v1");
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] MCP {body["method"]} -> {(int)response.StatusCode} {(text.Length > 600 ? text.Substring(0, 600) + "..." : text)}");
            return (response.StatusCode, text);
        }

        private async Task<JsonNode?> McpAsync(HttpClient client, CookieContainer cookies, string method, JsonObject? parameters, string label)
        {
            var (status, body) = await McpPostAsync(client, cookies, McpBody(method, parameters), label);
            Assert.Equal(HttpStatusCode.OK, status);
            // Streamable HTTP answers either plain JSON or one SSE event carrying it.
            var payload = body.Contains("data:", StringComparison.Ordinal)
                ? string.Join("", body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line.Substring(5).Trim()))
                : body;
            var node = JsonNode.Parse(payload);
            Assert.Null(node?["error"]);
            return node;
        }

        private async Task<List<string?>> McpToolNamesAsync(HttpClient client, CookieContainer cookies, string label) =>
            (await McpAsync(client, cookies, "tools/list", null, label))?["result"]?["tools"]?.AsArray().Select(tool => (string?)tool?["name"]).ToList() ?? new();

        private static string McpText(JsonNode? response, string member) => (string?)response?["result"]?[member]?[0]?["text"] ?? string.Empty;

        private static string StatusName(JsonNode? status) => status?.GetValueKind() == JsonValueKind.Number ? ((int)status == 1 ? "Draft" : "Published") : (string?)status ?? string.Empty;

        // ---- HTTP (the framework test suite's helpers, trimmed) -------------------------------------------------------------

        private static HttpClient NewClient(WebApplication app) =>
            new(app.GetTestServer().CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Parse("10.86.0.1"))) { BaseAddress = new Uri("http://" + Host) };

        private async Task<CookieContainer> LoginAsync(WebApplication app, HttpClient client, string username = HostUsername, string password = HostPassword)
        {
            var cookies = new CookieContainer();
            var (token, antiforgeryCookie) = Antiforgery(app, null);
            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = token,
                }),
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            var response = await client.SendAsync(request);
            CaptureCookies(cookies, response);
            var names = cookies.GetCookies(new Uri("http://" + Host)).Cast<Cookie>().Select(cookie => cookie.Name).ToList();
            output.WriteLine($"[login] -> {(int)response.StatusCode}; cookies: {string.Join(", ", names)}");
            Assert.Contains(names, name => name.Contains("Identity.Application", StringComparison.Ordinal));
            return cookies;
        }

        private static async Task<int> UserIdAsync(WebApplication app, string username)
        {
            using var scope = app.Services.CreateScope();
            var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host)
                ?? throw new InvalidOperationException("No alias for the host.");
            await scope.ServiceProvider.GetRequiredService<Stomme.Infrastructure.ITenantManager>().SetAliasAsync(alias);
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetUserAsync(username)
                ?? throw new InvalidOperationException($"No user '{username}'.");
            return user.UserId;
        }

        private async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, CookieContainer cookies, string path)
        {
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"GET {path} -> {(int)response.StatusCode} ({body.Length} chars)");
            return (response.StatusCode, body);
        }

        private Task<T> Send<T>(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path, object body, HttpStatusCode expected) =>
            Send<T>(app, client, cookies, userId, HttpMethod.Post, path, body, expected);

        private async Task<T> Send<T>(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object body, HttpStatusCode expected)
        {
            var (token, antiforgeryCookie) = Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body, body.GetType()) };
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri) + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"{method} {path} -> {(int)response.StatusCode} {text}");
            Assert.Equal(expected, response.StatusCode);
            return string.IsNullOrEmpty(text) ? default! : JsonSerializer.Deserialize<T>(text, Json)!;
        }

        private static (string RequestToken, string Cookie) Antiforgery(WebApplication app, int? userId)
        {
            using var scope = app.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (userId is { } id)
            {
                var identity = new ClaimsIdentity(Constants.AuthenticationScheme);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                context.User = new ClaimsPrincipal(identity);
            }

            var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
            return (tokens.RequestToken!, context.Response.Headers.SetCookie.ToString().Split(';')[0]);
        }

        private static void CaptureCookies(CookieContainer cookies, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return;
            }

            foreach (var pair in setCookies.Select(setCookie => setCookie.Split(';')[0]))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    try { cookies.Add(new Cookie(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim(), "/", Host)); } catch { }
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---- The stores - the shared test servers, read from the environment the broker provides ------------------------------

        internal abstract class Store
        {
            public abstract string Name { get; }

            public abstract string DatabaseType { get; }

            public abstract string DefaultConnection(string contentRoot);

            public virtual string? ModuleAggregatesConnection(string contentRoot) => null;

            public abstract void RequireReachable();

            public abstract void VerifySchema(ScratchInstall scratch, ITestOutputHelper output);

            public virtual void Cleanup()
            {
            }

            public static Store Resolve(string name) => name switch
            {
                "SQLite" => new SqliteStore(),
                "PostgreSQL" => new PostgresStore(),
                "RavenDB" => new RavenStore(),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown store."),
            };

            protected static bool TcpReachable(string host, int port)
            {
                try
                {
                    using var tcp = new TcpClient();
                    return tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)) && tcp.Connected;
                }
                catch
                {
                    return false;
                }
            }

            protected static void AssertTables(IReadOnlyCollection<string> tables, ITestOutputHelper output, string label)
            {
                output.WriteLine($"[{label}] module store tables: {string.Join(", ", tables)}");
                Assert.Contains(tables, table => table.Replace("_", string.Empty).Equals("article", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(tables, table => table.Replace("_", string.Empty).Equals("category", StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class SqliteStore : Store
        {
            public override string Name => "SQLite";

            public override string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

            public override string DefaultConnection(string contentRoot) => $"Data Source={Path.Combine(contentRoot, "stomme-kb.db")};";

            public override void RequireReachable()
            {
            }

            public override void VerifySchema(ScratchInstall scratch, ITestOutputHelper output)
            {
                var moduleDb = Path.Combine(scratch.ContentRoot, "Data", "Stomme.ModuleAggregates.db");
                Assert.True(File.Exists(moduleDb), $"No module store at {moduleDb}.");
                var tables = new List<string>();
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={moduleDb};Mode=ReadOnly;Pooling=False"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                AssertTables(tables, output, Name);
            }
        }

        private sealed class PostgresStore : Store
        {
            private static readonly string Server = Environment.GetEnvironmentVariable("POSTGRES_TEST_HOST") ?? "localhost";
            private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_TEST_PORT"), out var port) ? port : 5433;
            private static readonly string User = Environment.GetEnvironmentVariable("POSTGRES_TEST_USER") ?? "postgres";
            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);

            private static string Password => Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD") ?? Environment.GetEnvironmentVariable("DEFAULT_PASSWORD")
                ?? throw new InvalidOperationException("No PostgreSQL password in the environment - set POSTGRES_TEST_PASSWORD (or DEFAULT_PASSWORD) before running the drive.");

            private string SiteDb => "stomme_kb_drive_" + _suffix;

            private string ModuleDb => "stomme_kb_modstore_" + _suffix;

            public override string Name => "PostgreSQL";

            public override string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";

            public override string DefaultConnection(string contentRoot) => Connection(SiteDb);

            public override string ModuleAggregatesConnection(string contentRoot) => Connection(ModuleDb);

            public override void RequireReachable() =>
                Assert.True(TcpReachable(Server, Port), $"PostgreSQL is not reachable at {Server}:{Port}; the drive FAILS rather than skips (D46).");

            public override void VerifySchema(ScratchInstall scratch, ITestOutputHelper output)
            {
                using var connection = new Npgsql.NpgsqlConnection(Connection(ModuleDb));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema='public'";
                var tables = new List<string>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                AssertTables(tables, output, Name);
            }

            public override void Cleanup()
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
                foreach (var database in new[] { SiteDb, ModuleDb })
                {
                    try
                    {
                        using var connection = new Npgsql.NpgsqlConnection(Connection("postgres"));
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                        command.ExecuteNonQuery();
                    }
                    catch
                    {
                        // best effort: a failed drop leaves a scratch database on the shared server, named by its prefix
                    }
                }
            }

            private static string Connection(string database) => $"Server={Server};Port={Port};Database={database};User Id={User};Password={Password};";
        }

        private sealed class RavenStore : Store
        {
            private static readonly string Url = Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://localhost:8080";
            private readonly string _database = "stomme-kb-drive-" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public override string Name => "RavenDB";

            public override string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

            public override string DefaultConnection(string contentRoot) => $"Url={Url};Database={_database}";

            public override void RequireReachable()
            {
                var uri = new Uri(Url);
                Assert.True(TcpReachable(uri.Host, uri.Port), $"RavenDB is not reachable at {Url}; the drive FAILS rather than skips (D46).");
            }

            // RavenDB is schemaless: the fresh-request re-read above is the persistence proof.
            public override void VerifySchema(ScratchInstall scratch, ITestOutputHelper output) =>
                output.WriteLine("[RavenDB] schemaless - persistence proven by the fresh-request re-read through the generic repository.");

            public override void Cleanup()
            {
                try
                {
                    using var store = new DocumentStore { Urls = new[] { Url }, Database = _database };
                    store.Initialize();
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(_database, hardDelete: true));
                }
                catch
                {
                    // best effort
                }
            }
        }

        /// <summary>A throwaway content root with the packed module in its Packages folder and the store in its override file.</summary>
        internal sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;
            private readonly Store _store;

            public ScratchInstall(string binPath, Store store, JsonObject? extraSettings = null, bool nested = false)
            {
                _store = store;
                // NESTED puts the content root two levels down (Top/host/content): the host's Create Module writes a module
                // two folders above its content root, which must be this scratch folder and not the machine's temp parent.
                Top = nested ? Path.Combine(Path.GetTempPath(), "stomme-kb-create-" + Guid.NewGuid().ToString("N")) : null;
                ContentRoot = Top is not null ? Path.Combine(Top, "host", "content") : Path.Combine(Path.GetTempPath(), "stomme-kb-drive-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                PackagesFolder = Path.Combine(ContentRoot, Constants.PackagesFolder);
                Directory.CreateDirectory(PackagesFolder);

                var nupkg = Directory.GetFiles(binPath, "NovaGloben.Module.KnowledgeBase.*.nupkg").FirstOrDefault()
                    ?? throw new FileNotFoundException("The packed module is not beside the harness. Run examples/Modules/KnowledgeBase/pack-knowledgebase.ps1 first.");
                StagedNupkg = Path.Combine(PackagesFolder, Path.GetFileName(nupkg));
                File.Copy(nupkg, StagedNupkg, overwrite: true);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));

                var connectionStrings = new JsonObject { ["DefaultConnection"] = store.DefaultConnection(ContentRoot) };
                if (store.ModuleAggregatesConnection(ContentRoot) is { } moduleStore)
                {
                    connectionStrings["ModuleAggregates"] = moduleStore;
                }

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = store.DatabaseType },
                    ["ConnectionStrings"] = connectionStrings,
                    // Contextual help and the MCP server half are each off by default; the drive turns both on.
                    ["Help"] = new JsonObject { ["Enabled"] = true },
                    ["Mcp"] = new JsonObject { ["Server"] = new JsonObject { ["Enabled"] = true } },
                    // The data duties, each off by default; erasure has no switch.
                    ["DataExport"] = new JsonObject { ["Enabled"] = true },
                    ["Portability"] = new JsonObject { ["Enabled"] = true },
                    ["Backup"] = new JsonObject { ["Enabled"] = true },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = Host,
                        ["HostUsername"] = HostUsername,
                        ["HostPassword"] = HostPassword,
                        ["HostEmail"] = "kb-drive@example.com",
                        ["HostName"] = "Host",
                    },
                };
                foreach (var (key, value) in extraSettings ?? new JsonObject())
                {
                    overrides[key] = value?.DeepClone(); // a test's own switches, in the override FILE a deployed install reads
                }

                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"), overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            /// <summary>The nested scratch folder, when there is one; deleted whole on dispose.</summary>
            public string? Top { get; }

            public string PackagesFolder { get; }

            public string StagedNupkg { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                try { _store.Cleanup(); } catch { }
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
                try { Directory.Delete(Top ?? ContentRoot, true); } catch { }
            }
        }
    }
}
