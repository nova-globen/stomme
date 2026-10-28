using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace NovaGloben.Module.Blogs.Drive
{
    /// <summary>
    /// THE FINALE (D82) — drives the INDEPENDENT, STORAGE-BLIND Blogs module through the REAL Stomme
    /// InstallationManager on the production host (Program.BuildAppAsync composing the real DI container +
    /// middleware pipeline + MapRazorComponents&lt;App&gt; endpoint, transport swapped for TestServer), and
    /// proves the never-before-driven external-install path end to end, ON EACH REACHABLE PROVIDER CLASS:
    /// <list type="number">
    ///   <item>the packaged NovaGloben.Module.Blogs.2.0.0.nupkg (packages-only build, SPLIT
    ///     Client/Server/Shared per BL-13) is UNPACKED by InstallationManager — all three DLLs into /bin,
    ///     .log manifest written, nupkg deleted;</item>
    ///   <item>the module is DISCOVERED by convention (every assembly name contains "Stomme") and
    ///     self-places a public "blogs" page via its ModuleDefinition.PageTemplates (ModuleInfo lives in the
    ///     CLIENT assembly beside Index.razor);</item>
    ///   <item>the site RENDERS the Blog UI at /blogs;</item>
    ///   <item>a post CREATED through the module controller PERSISTS on the host's provider and RE-READS on a
    ///     fresh HTTP request — proving storage-blind persistence for an externally-installed module;</item>
    ///   <item>the module's schema was CREATED (relational providers: EnsureCreated via the storage-blind
    ///     helper; RavenDB is schemaless, so persistence is proven by the fresh-request re-read alone).</item>
    /// </list>
    /// The SAME unchanged package runs on SQLite (a module-aggregate SQLite sidecar), PostgreSQL (a co-located
    /// relational module store) and RavenDB (the install's document store) — the flexible-persistence claim.
    /// The write is driven over HTTP with the framework's MAUI user-agent, which the host's
    /// AutoValidateAntiforgeryTokenFilter exempts from antiforgery (a native-client path).
    /// </summary>
    [Collection("BlogsDrive")] // the drive classes install into the SHARED harness /bin and flip the
                               // process-wide STOMME_APPSETTINGS_PATH override, so they must not run in
                               // parallel with each other
    public sealed class BlogsExternalInstallDriveTests
    {
        // BL-13: the split module ships THREE assemblies in one nupkg; all three are extracted into /bin and
        // loaded by convention. (WASM streaming serves only Client + Shared — see WasmStreamingDriveTests.)
        private static readonly string[] ModuleAssemblyDlls =
        {
            "NovaGloben.Module.Blogs.Client.Stomme.dll",
            "NovaGloben.Module.Blogs.Server.Stomme.dll",
            "NovaGloben.Module.Blogs.Shared.Stomme.dll"
        };

        private const int BlogModuleId = 5000; // a blog instance id for the persistence drive (decoupled from placement)

        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public BlogsExternalInstallDriveTests(ITestOutputHelper output) => _output = output;

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { "SQLite" };
            if (TcpReachable("127.0.0.1", 5432))
            {
                yield return new object[] { "PostgreSQL" };
            }
            if (RavenReachable())
            {
                yield return new object[] { "RavenDB" };
            }
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task External_storage_blind_module_installs_renders_and_persists(string providerName)
        {
            var provider = ResolveProvider(providerName);
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;

            using var scratch = new ScratchInstall(binPath, provider);
            _output.WriteLine($"[{providerName}] Scratch content root: {scratch.ContentRoot}");
            Assert.True(File.Exists(scratch.StagedNupkg), "The packaged module nupkg was not staged.");

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                // ---- 1. INSTALLED ---------------------------------------------------------------------------
                Assert.False(File.Exists(scratch.StagedNupkg),
                    "InstallationManager did not consume the nupkg (it should be deleted after unpacking).");
                foreach (var dll in ModuleAssemblyDlls)
                {
                    Assert.True(File.Exists(Path.Combine(binPath, dll)),
                        $"The module assembly was not extracted into /bin ({dll}).");
                }
                Assert.True(File.Exists(Path.Combine(scratch.PackagesFolder, "NovaGloben.Module.Blogs.2.0.0.log")),
                    "The install manifest (.log) was not written.");
                _output.WriteLine($"[{providerName}] INSTALLED: all three split DLLs in bin, nupkg consumed, manifest written.");

                // ---- 2. DISCOVERED --------------------------------------------------------------------------
                foreach (var assemblyName in ModuleAssemblyDlls.Select(Path.GetFileNameWithoutExtension))
                {
                    Assert.True(
                        AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                            string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal)),
                        $"The module assembly {assemblyName} was not loaded into the AppDomain by the convention scan.");
                }
                _output.WriteLine($"[{providerName}] DISCOVERED: Client, Server and Shared assemblies loaded into the AppDomain.");

                // ---- Install the site (also seeds the module's self-declared /blogs page) --------------------
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException("The installer did not finish in time.");
                }
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");
                Assert.True(string.IsNullOrEmpty(install.Message), $"Installer reported: {install.Message}");
                _output.WriteLine($"[{providerName}] Site installed.");

                await app.StartAsync();
                using var client = app.GetTestClient();

                // ---- 3. RENDERED ----------------------------------------------------------------------------
                var blogsPage = await client.GetAsync("/blogs");
                var blogsHtml = await blogsPage.Content.ReadAsStringAsync();
                _output.WriteLine($"[{providerName}] GET /blogs -> {(int)blogsPage.StatusCode}, {blogsHtml.Length} chars");
                Assert.Equal(HttpStatusCode.OK, blogsPage.StatusCode);
                Assert.DoesNotContain("Installation Wizard", blogsHtml, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Publish post", blogsHtml, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("An independent, storage-blind sample module", blogsHtml, StringComparison.OrdinalIgnoreCase);
                _output.WriteLine($"[{providerName}] RENDERED: the Blog module UI is present in the /blogs page HTML.");

                // ---- 4a. CREATE a post through the module controller (MAUI UA bypasses antiforgery) ----------
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.MauiUserAgent);
                var newPost = new JsonObject
                {
                    ["moduleId"] = BlogModuleId,
                    ["title"] = "Reliable modularity, driven",
                    ["body"] = "This post was created through an externally-installed, storage-blind Stomme module.",
                    ["author"] = "Ada Lovelace"
                };
                var createResponse = await client.PostAsJsonAsync("/api/Blog", newPost);
                var createdBody = await createResponse.Content.ReadAsStringAsync();
                _output.WriteLine($"[{providerName}] POST /api/Blog -> {(int)createResponse.StatusCode}: {createdBody}");
                Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
                var created = JsonNode.Parse(createdBody);
                var createdId = (string)created["id"];
                Assert.False(string.IsNullOrEmpty(createdId) || createdId == Guid.Empty.ToString(),
                    "The created post did not come back with a real id.");
                Assert.Equal("Ada Lovelace", (string)created["author"]);
                _output.WriteLine($"[{providerName}] CREATED: post {createdId} persisted through the storage-blind repository.");

                // ---- 4b. RE-READ on a FRESH HTTP request (new scope, new session/DbContext) -----------------
                var listed = await client.GetFromJsonAsync<JsonArray>($"/api/Blog?moduleid={BlogModuleId}");
                Assert.NotNull(listed);
                Assert.Contains(listed.Select(node => (string)node["title"]), t => t == "Reliable modularity, driven");
                _output.WriteLine($"[{providerName}] RE-READ: {listed.Count} post(s) listed on a fresh request; " +
                                  "the PostTitle value object round-tripped intact.");

                // ---- 5. SCHEMA CREATED (relational) / persistence proven (document) --------------------------
                provider.VerifySchema(scratch, _output);

                _output.WriteLine($"[{providerName}] VERDICT: install -> discover -> render -> create -> persist -> schema — ALL DRIVEN GREEN.");
            }
            finally
            {
                await SafeShutdownAsync(app);
                foreach (var dll in ModuleAssemblyDlls)
                {
                    TryDelete(Path.Combine(binPath, dll));
                    TryDelete(Path.Combine(binPath, dll.Replace(".dll", ".pdb")));
                }
            }
        }

        // ---- Provider abstraction -----------------------------------------------------------------------

        internal interface IProvider
        {
            string Name { get; }
            string DatabaseType { get; }
            string DefaultConnection(string contentRoot);
            /// <summary>Optional ConnectionStrings:ModuleAggregates (co-locates the module store); null = default sidecar.</summary>
            string ModuleAggregatesConnection(string contentRoot);
            void VerifySchema(ScratchInstall scratch, ITestOutputHelper output);
            void Cleanup(ScratchInstall scratch);
        }

        private static IProvider ResolveProvider(string name) => name switch
        {
            "SQLite" => new SqliteProvider(),
            "PostgreSQL" => new PostgresProvider(),
            "RavenDB" => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        private sealed class SqliteProvider : IProvider
        {
            public string Name => "SQLite";
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string DefaultConnection(string contentRoot) => $"Data Source={Path.Combine(contentRoot, "stomme-blogs.db")};";
            public string ModuleAggregatesConnection(string contentRoot) => null; // default SQLite sidecar
            public void VerifySchema(ScratchInstall scratch, ITestOutputHelper output)
            {
                var moduleDb = Path.Combine(scratch.ContentRoot, "Data", "Stomme.ModuleAggregates.db");
                Assert.True(File.Exists(moduleDb), $"The module-aggregate store was not created at {moduleDb}.");
                var tables = ReadSqliteTables(moduleDb);
                output.WriteLine($"[SQLite] module-aggregate store tables: {string.Join(", ", tables)}");
                Assert.Contains(tables, IsBlogPostTable);
                output.WriteLine("[SQLite] SCHEMA CREATED: the BlogPost table exists (EnsureCreated, SQLite sidecar).");
            }
            public void Cleanup(ScratchInstall scratch) { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { } }
        }

        private sealed class PostgresProvider : IProvider
        {
            private const string Host = "127.0.0.1";
            private const int Port = 5432;
            private const string User = "postgres";
            private static readonly string Password = Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD") ?? Environment.GetEnvironmentVariable("DEFAULT_PASSWORD") ?? string.Empty;
            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private string SiteDb => "stomme_blogs_drive_" + _suffix;
            private string ModuleDb => "stomme_blogs_modstore_" + _suffix;

            public string Name => "PostgreSQL";
            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string DefaultConnection(string contentRoot) => $"Server={Host};Port={Port};Database={SiteDb};User Id={User};Password={Password};";
            // Co-locate the module store on PostgreSQL, in a SEPARATE database so EnsureCreated (which is a no-op
            // against an already-created database — the D79 standalone-store requirement) actually creates it.
            public string ModuleAggregatesConnection(string contentRoot) => $"Server={Host};Port={Port};Database={ModuleDb};User Id={User};Password={Password};";
            public void VerifySchema(ScratchInstall scratch, ITestOutputHelper output)
            {
                using var connection = new Npgsql.NpgsqlConnection(ModuleAggregatesConnection(scratch.ContentRoot));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema='public' ORDER BY table_name;";
                var tables = new List<string>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) tables.Add(reader.GetString(0));
                }
                output.WriteLine($"[PostgreSQL] co-located module store ({ModuleDb}) tables: {string.Join(", ", tables)}");
                // PostgreSQL snake-cases the entity table name (blog_post), SQLite keeps it (BlogPost); normalize.
                Assert.Contains(tables, IsBlogPostTable);
                output.WriteLine("[PostgreSQL] SCHEMA CREATED: the blog_post table exists in PostgreSQL (EnsureCreated, co-located).");
            }
            public void Cleanup(ScratchInstall scratch)
            {
                DropDatabase(SiteDb);
                DropDatabase(ModuleDb);
            }
            private void DropDatabase(string database)
            {
                try
                {
                    using var connection = new Npgsql.NpgsqlConnection($"Server={Host};Port={Port};Database=postgres;User Id={User};Password={Password};");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                    command.ExecuteNonQuery();
                }
                catch { /* best-effort */ }
            }
        }

        private sealed class RavenProvider : IProvider
        {
            private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
            private readonly string _database = "stomme_blogs_drive_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public string Name => "RavenDB";
            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string DefaultConnection(string contentRoot) => $"Url={Url};Database={_database}";
            public string ModuleAggregatesConnection(string contentRoot) => null; // Raven install store; schemaless
            public void VerifySchema(ScratchInstall scratch, ITestOutputHelper output)
            {
                // RavenDB is schemaless: there is no table/collection schema to create. The fresh-request RE-READ
                // above already proved the BlogPost document persisted in and re-read from the install's document
                // store through the generic RavenRepository — the storage-blind guarantee, with zero module code.
                output.WriteLine("[RavenDB] SCHEMA: RavenDB is schemaless — persistence proven by the fresh-request re-read " +
                                 "(BlogPost round-tripped through the generic RavenRepository over the install's document store).");
            }
            public void Cleanup(ScratchInstall scratch)
            {
                try
                {
                    using var store = new DocumentStore { Urls = new[] { Url }, Database = _database };
                    store.Initialize();
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(_database, hardDelete: true));
                }
                catch { /* best-effort */ }
            }
        }

        // ---- Reachability + helpers ---------------------------------------------------------------------

        private static bool TcpReachable(string host, int port)
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch { return false; }
        }

        private static bool RavenReachable()
        {
            var url = Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
            try
            {
                var uri = new Uri(url);
                return TcpReachable(uri.Host, uri.Port);
            }
            catch { return false; }
        }

        // The BlogPost aggregate maps to a table named for the entity; PostgreSQL snake-cases it (blog_post),
        // SQLite keeps it (BlogPost). Normalize (drop underscores, ignore case) so the check is provider-neutral.
        private static bool IsBlogPostTable(string tableName) =>
            tableName.Replace("_", "").Equals("blogpost", StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> ReadSqliteTables(string dbPath)
        {
            var tables = new List<string>();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return tables;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }

        /// <summary>A throwaway content root + a Packages folder holding the module nupkg, mirroring the render guard.</summary>
        internal sealed class ScratchInstall : IDisposable
        {
            private readonly string _previousOverridePath;
            private readonly IProvider _provider;

            public ScratchInstall(string binPath, IProvider provider)
            {
                _provider = provider;
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-blogs-drive-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                PackagesFolder = Path.Combine(ContentRoot, Constants.PackagesFolder);
                Directory.CreateDirectory(PackagesFolder);

                var sourceNupkg = Directory.GetFiles(binPath, "NovaGloben.Module.Blogs.*.nupkg").FirstOrDefault()
                                  ?? throw new FileNotFoundException(
                                      "The packaged module nupkg was not found beside the harness. Run examples/Modules/Blogs/pack-blogs.ps1 first.");
                StagedNupkg = Path.Combine(PackagesFolder, Path.GetFileName(sourceNupkg));
                File.Copy(sourceNupkg, StagedNupkg, overwrite: true);

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var connectionStrings = new JsonObject { ["DefaultConnection"] = provider.DefaultConnection(ContentRoot) };
                var moduleAggregates = provider.ModuleAggregatesConnection(ContentRoot);
                if (!string.IsNullOrEmpty(moduleAggregates))
                {
                    connectionStrings["ModuleAggregates"] = moduleAggregates;
                }

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = provider.DatabaseType },
                    ["ConnectionStrings"] = connectionStrings,
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "Bl0g$Drive!2026",
                        ["HostEmail"] = "blogs-drive@example.com",
                        ["HostName"] = "Host"
                    }
                };
                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }
            public string PackagesFolder { get; }
            public string StagedNupkg { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                try { _provider.Cleanup(this); } catch { }
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }
    }
}
