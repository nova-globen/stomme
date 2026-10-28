using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace NovaGloben.Module.Blogs.Drive
{
    /// <summary>
    /// BL-13 — the WASM STREAMING drive. Installs a REAL site with <c>Runtime=WebAssembly</c> and
    /// <c>TestMode=true</c> (the sanctioned test seam: <c>api/Installation/list</c> then returns un-hashed
    /// <c>{assembly}.{timestamp}.dll</c> names) with the SPLIT Blogs module nupkg AND the external Aurora
    /// theme nupkg staged for the real InstallationManager, and drives the assembly-streaming API the
    /// WebAssembly client boots from (<c>Stomme.Client/Program.cs LoadClientAssemblies</c>):
    /// <list type="bullet">
    ///   <item><c>GET /api/Installation/list</c> must include the module's CLIENT assembly (the
    ///     ModuleDefinition's AssemblyName) and its SHARED assembly (named in
    ///     ModuleDefinition.Dependencies), and must NOT include the SERVER assembly — the WASM-download-safe
    ///     contract of the split;</item>
    ///   <item><c>GET /api/Installation/load?list=...</c> must return a zip actually carrying the client DLL
    ///     bytes;</item>
    ///   <item><c>GET /api/Installation/list</c> must include the external THEME assembly — the pin for the
    ///     InstallationController theme-streaming defect (the discarded
    ///     <c>assemblies.Concat(await _themes.GetAssembliesAsync(...))</c> result), which shipped every
    ///     external theme broken on WebAssembly sites. This test was RED against the unfixed controller.</item>
    /// </list>
    /// SQLite only: the streamed set is computed from ModuleDefinition/Theme metadata + /bin contents, not
    /// from the persistence provider, so one provider is real cover here (unlike the render/persist drive).
    /// </summary>
    [Collection("BlogsDrive")]
    public sealed class WasmStreamingDriveTests
    {
        private const string ClientAssemblyName = "NovaGloben.Module.Blogs.Client.Stomme";
        private const string ServerAssemblyName = "NovaGloben.Module.Blogs.Server.Stomme";
        private const string SharedAssemblyName = "NovaGloben.Module.Blogs.Shared.Stomme";
        private const string ThemeAssemblyName = "Stomme.Theme.Aurora.Stomme";

        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public WasmStreamingDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Wasm_list_streams_client_and_shared_assemblies_and_never_the_server_assembly()
        {
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new WasmScratchInstall(binPath, stageTheme: false);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                await InstallAsync(app);
                await app.StartAsync();
                using var client = app.GetTestClient();

                var list = await GetAssemblyListAsync(client);
                _output.WriteLine("api/Installation/list -> " + string.Join(", ", list));

                var clientEntry = FindEntry(list, ClientAssemblyName);
                var sharedEntry = FindEntry(list, SharedAssemblyName);
                Assert.True(clientEntry != null,
                    $"{ClientAssemblyName}.dll (the ModuleDefinition AssemblyName) is missing from api/Installation/list.");
                Assert.True(sharedEntry != null,
                    $"{SharedAssemblyName}.dll (named in ModuleDefinition.Dependencies) is missing from api/Installation/list.");
                Assert.True(FindEntry(list, ServerAssemblyName) == null,
                    $"{ServerAssemblyName}.dll must NEVER stream to the browser, but api/Installation/list contains it.");

                // The zip really carries the client DLL bytes (byte-identical to the /bin file the host serves).
                var zipBytes = await client.GetByteArrayAsync(
                    $"/api/Installation/load?list={Uri.EscapeDataString(clientEntry + "," + sharedEntry)}");
                using var archive = new ZipArchive(new MemoryStream(zipBytes));
                var entryNames = archive.Entries.Select(e => e.FullName).ToList();
                _output.WriteLine("api/Installation/load zip entries -> " + string.Join(", ", entryNames));

                var clientZipEntry = archive.Entries.FirstOrDefault(e => e.FullName == clientEntry);
                Assert.True(clientZipEntry != null, "The load zip does not contain the requested client assembly entry.");
                using var entryStream = new MemoryStream();
                using (var zipStream = clientZipEntry.Open())
                {
                    await zipStream.CopyToAsync(entryStream);
                }
                var streamedBytes = entryStream.ToArray();
                var binBytes = await File.ReadAllBytesAsync(Path.Combine(binPath, ClientAssemblyName + ".dll"));
                Assert.True(streamedBytes.Length > 0, "The streamed client assembly entry is empty.");
                Assert.Equal(binBytes, streamedBytes);
                Assert.Contains(entryNames, name => name == sharedEntry);

                _output.WriteLine(
                    $"WASM STREAMING: Client + Shared stream ({streamedBytes.Length} bytes for the client DLL), Server excluded.");
            }
            finally
            {
                await SafeShutdownAsync(app);
                CleanupBin(binPath);
            }
        }

        [Fact]
        public async Task Wasm_list_streams_the_external_theme_assembly()
        {
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            using var scratch = new WasmScratchInstall(binPath, stageTheme: true);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                Assert.True(File.Exists(Path.Combine(binPath, ThemeAssemblyName + ".dll")),
                    "The Aurora theme assembly was not extracted into /bin by InstallationManager.");

                await InstallAsync(app);
                await app.StartAsync();
                using var client = app.GetTestClient();

                var list = await GetAssemblyListAsync(client);
                _output.WriteLine("api/Installation/list -> " + string.Join(", ", list));

                // THE PIN for the InstallationController theme-streaming defect: GetAssemblyList discarded the
                // result of assemblies.Concat(await _themes.GetAssembliesAsync(...)), so an external theme's
                // assembly never reached the WASM client and every external theme was dead on a WebAssembly
                // site. RED against the unfixed controller (the entry below was absent), GREEN after the fix.
                Assert.True(FindEntry(list, ThemeAssemblyName) != null,
                    $"{ThemeAssemblyName}.dll (the enabled external theme) is missing from api/Installation/list — " +
                    "theme assemblies are not being streamed to the WebAssembly client.");

                _output.WriteLine("WASM STREAMING: the external theme assembly streams to the browser.");
            }
            finally
            {
                await SafeShutdownAsync(app);
                CleanupBin(binPath);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------

        private static async Task InstallAsync(WebApplication app)
        {
            var installTask = Task.Run(() => Program.InstallAsync(app));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException("The installer did not finish in time.");
            }
            var install = await installTask;
            Assert.True(install.Success, $"Installer failed: {install.Message}");
        }

        private static async Task<List<string>> GetAssemblyListAsync(System.Net.Http.HttpClient client)
        {
            var response = await client.GetAsync("/api/Installation/list");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<string>>(json);
        }

        /// <summary>
        /// TestMode list entries are un-hashed but still timestamped: <c>{assembly}.{yyyyMMddHHmmss}.dll</c>
        /// (<c>InstallationController.ClientAssembly</c>). Match on the assembly-name prefix.
        /// </summary>
        private static string FindEntry(IEnumerable<string> list, string assemblyName) =>
            list.FirstOrDefault(entry =>
                entry.StartsWith(assemblyName + ".", StringComparison.Ordinal) &&
                entry.EndsWith(".dll", StringComparison.Ordinal));

        private static void CleanupBin(string binPath)
        {
            foreach (var name in new[] { ClientAssemblyName, ServerAssemblyName, SharedAssemblyName, ThemeAssemblyName })
            {
                TryDelete(Path.Combine(binPath, name + ".dll"));
                TryDelete(Path.Combine(binPath, name + ".pdb"));
            }
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

        /// <summary>
        /// The Blogs.Drive scratch-install pattern (throwaway content root + staged Packages +
        /// STOMME_APPSETTINGS_PATH override), specialised for a WebAssembly site: the override sets
        /// <c>Runtime=WebAssembly</c> (seeded onto the Site row by DatabaseManager at install) and
        /// <c>TestMode=true</c> as a STRING (a JSON boolean binds as "True" and the controller compares
        /// against lowercase "true" — measured trap). SQLite keeps it self-contained.
        /// </summary>
        private sealed class WasmScratchInstall : IDisposable
        {
            private readonly string _previousOverridePath;

            public WasmScratchInstall(string binPath, bool stageTheme)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-blogs-wasm-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                PackagesFolder = Path.Combine(ContentRoot, Constants.PackagesFolder);
                Directory.CreateDirectory(PackagesFolder);

                Stage(binPath, "NovaGloben.Module.Blogs.*.nupkg",
                    "The packaged module nupkg was not found beside the harness. Run examples/Modules/Blogs/pack-blogs.ps1 first.");
                if (stageTheme)
                {
                    Stage(binPath, "Stomme.Theme.Aurora.*.nupkg",
                        "The packaged Aurora theme nupkg was not found beside the harness. Run examples/Themes/pack-themes.ps1 first.");
                }

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Runtime"] = "WebAssembly",
                    ["TestMode"] = "true",
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = $"Data Source={Path.Combine(ContentRoot, "stomme-blogs-wasm.db")};"
                    },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "Bl0g$Wasm!2026",
                        ["HostEmail"] = "blogs-wasm@example.com",
                        ["HostName"] = "Host"
                    }
                };
                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }
            public string PackagesFolder { get; }

            private void Stage(string binPath, string pattern, string missingMessage)
            {
                var source = Directory.GetFiles(binPath, pattern).FirstOrDefault()
                             ?? throw new FileNotFoundException(missingMessage);
                File.Copy(source, Path.Combine(PackagesFolder, Path.GetFileName(source)), overwrite: true);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }
    }
}
