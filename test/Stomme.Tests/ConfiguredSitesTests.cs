#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>Installation:Sites</c>: the sites beyond the first, created unattended from configuration on any boot that finds
    /// one missing - a folder site on the installation's own tenant, and a site on a new tenant with a SQLite file of its own.
    /// </summary>
    /// <remarks>
    /// The list is given as ENVIRONMENT VARIABLES (<c>Installation__Sites__0__Name</c>, ...), the way a compose file declares
    /// it. An invalid list refuses the boot by name with nothing created.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ConfiguredSitesTests
    {
        private const string Root = "localhost";
        private const string Folder = "localhost/sv";
        private const string Shop = "shop.example.com";
        private const string Password = "S1tes!Config$2026";

        private readonly ITestOutputHelper _output;

        public ConfiguredSitesTests(ITestOutputHelper output) => _output = output;

        // ---- the plan: the whole list is checked before anything is written -------------------------------------------

        private static ConfiguredSitesState State(bool hostPassword = true, string? dataDirectory = null) => new(
            Aliases: new[] { (Root, 1), ("www.example.com", 1) },
            Tenants: new[] { TenantNames.Master, "Other" },
            SiteNamesByTenant: new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [TenantNames.Master] = new[] { "Default Site" }
            },
            HostPasswordAvailable: hostPassword,
            DataDirectory: dataDirectory ?? Path.GetTempPath(),
            DefaultDatabaseType: "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server");

        [Fact]
        public void A_valid_list_plans_every_missing_site_and_skips_one_that_exists()
        {
            var entries = new List<ConfiguredSite>
            {
                new() { Name = "Done", Aliases = "www.example.com" }, // its first alias already names a site
                new() { Name = "Svenska", Aliases = Folder },
                new() { Name = "Shop", Aliases = Shop + ", www." + Shop, Tenant = "Shop", ConnectionString = "Data Source=|DataDirectory|/shop.db;" },
                new() { Name = "Shop B", Aliases = "b." + Shop, Tenant = "Shop" } // joins the tenant the entry before creates
            };

            var (toCreate, problems) = ConfiguredSites.Plan(entries, State());
            Assert.Empty(problems);
            Assert.Equal(new[] { 1, 2, 3 }, toCreate.Select(item => item.Index));
        }

        [Theory]
        [InlineData("noname", "Name is required")]
        [InlineData("noaliases", "Aliases is required")]
        [InlineData("duplicate", "is also listed by Installation:Sites:0")]
        [InlineData("taken", "already exists on this installation")]
        [InlineData("notenant", "does not exist; give a ConnectionString")]
        [InlineData("nameless-tenant", "a ConnectionString needs a Tenant name")]
        [InlineData("master", "is the installation's own")]
        [InlineData("existing-tenant", "already exists; leave out the ConnectionString")]
        [InlineData("site-name", "already has a site named 'Default Site'")]
        [InlineData("no-password", "needs the host account's password")]
        [InlineData("database-type", "is not a database type this host knows")]
        [InlineData("unwritable", "cannot be written")]
        public void An_invalid_entry_is_refused_by_name(string fault, string expected)
        {
            var blocker = Path.Combine(Path.GetTempPath(), "stomme-sites-blocker-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, ""); // a FILE where the SQLite folder would have to be created
            try
            {
                var entry = fault switch
                {
                    "noname" => new ConfiguredSite { Aliases = Folder },
                    "noaliases" => new ConfiguredSite { Name = "Svenska" },
                    "taken" => new ConfiguredSite { Name = "Svenska", Aliases = Folder + ",www.example.com" },
                    "notenant" => new ConfiguredSite { Name = "Svenska", Aliases = Folder, Tenant = "Nowhere" },
                    "nameless-tenant" => new ConfiguredSite { Name = "Shop", Aliases = Shop, ConnectionString = "Data Source=shop.db;" },
                    "master" => new ConfiguredSite { Name = "Shop", Aliases = Shop, Tenant = "Master", ConnectionString = "Data Source=shop.db;" },
                    "existing-tenant" => new ConfiguredSite { Name = "Shop", Aliases = Shop, Tenant = "other", ConnectionString = "Data Source=shop.db;" },
                    "site-name" => new ConfiguredSite { Name = "Default Site", Aliases = Folder },
                    "no-password" => new ConfiguredSite { Name = "Shop", Aliases = Shop, Tenant = "Shop", ConnectionString = "Data Source=|DataDirectory|/shop.db;" },
                    "database-type" => new ConfiguredSite { Name = "Shop", Aliases = Shop, Tenant = "Shop", ConnectionString = "x", DatabaseType = "No.Such.Database, Nowhere" },
                    "unwritable" => new ConfiguredSite { Name = "Shop", Aliases = Shop, Tenant = "Shop", ConnectionString = $"Data Source={Path.Combine(blocker, "shop.db")};" },
                    _ => new ConfiguredSite { Name = "Svenska Two", Aliases = Folder } // "duplicate": the same alias as entry 0
                };
                var entries = fault == "duplicate"
                    ? new List<ConfiguredSite> { new() { Name = "Svenska", Aliases = Folder }, entry }
                    : new List<ConfiguredSite> { entry };

                var (_, problems) = ConfiguredSites.Plan(entries, State(hostPassword: fault != "no-password"));
                _output.WriteLine(string.Join(Environment.NewLine, problems));
                Assert.Contains(problems, problem => problem.Contains(expected) && problem.StartsWith("Installation:Sites:"));
            }
            finally
            {
                File.Delete(blocker);
            }
        }

        // ---- the drive: root + a /sv folder site + a second tenant's SQLite site, unattended, from environment variables -

        [Fact]
        public async Task Root_a_folder_site_and_a_second_tenants_sqlite_site_are_created_unattended_and_each_serves_itself()
        {
            using var scratch = new Scratch();
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Svenska",
                ["Installation__Sites__0__Aliases"] = Folder,
                ["Installation__Sites__1__Name"] = "Shop",
                ["Installation__Sites__1__Aliases"] = Shop,
                ["Installation__Sites__1__Tenant"] = "Shop",
                ["Installation__Sites__1__ConnectionString"] = "Data Source=|DataDirectory|/shop.db;"
            });

            var app = await BootAsync(scratch, expectSuccess: true);
            try
            {
                var aliases = await AliasesAsync(app);
                _output.WriteLine("[aliases] " + string.Join(", ", aliases.Select(alias => $"{alias.Name}=t{alias.TenantId}/s{alias.SiteId}")));
                var root = aliases.Single(alias => alias.Name == Root);
                var folder = aliases.Single(alias => alias.Name == Folder);
                var shop = aliases.Single(alias => alias.Name == Shop);
                Assert.Equal(root.TenantId, folder.TenantId);
                Assert.NotEqual(root.SiteId, folder.SiteId);
                Assert.NotEqual(root.TenantId, shop.TenantId);

                // The second tenant has a SQLite file of its own, beside the installation's.
                Assert.True(File.Exists(Path.Combine(scratch.ContentRoot, "Data", "shop.db")), "the Shop tenant's own SQLite file was not created");

                using var client = app.GetTestClient();
                Assert.Contains("Default Site", await TitleAsync(client, "http://localhost/"));
                Assert.Contains("Svenska", await TitleAsync(client, "http://localhost/sv/"));
                Assert.Contains("Shop", await TitleAsync(client, "http://" + Shop + "/"));

                // A folder site's API call lands on its own site.
                Assert.Equal("Svenska", await SiteNameAsync(client, $"http://localhost/sv/api/Site/{folder.SiteId}"));
                Assert.Equal("Default Site", await SiteNameAsync(client, $"http://localhost/api/Site/{root.SiteId}"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }

            // A restart creates nothing twice - and survives losing the file the install wrote the new tenant's connection
            // string to, as a container recreated from its image does: the entry in the environment still supplies it.
            var overrideFile = Path.Combine(scratch.ContentRoot, "appsettings.override.json");
            var settings = JsonNode.Parse(File.ReadAllText(overrideFile))!.AsObject();
            Assert.True(settings["ConnectionStrings"]!.AsObject().Remove("Shop"), "the install did not write the Shop tenant's connection string");
            File.WriteAllText(overrideFile, settings.ToJsonString());

            var restarted = await BootAsync(scratch, expectSuccess: true);
            try
            {
                var again = await AliasesAsync(restarted);
                Assert.Equal(3, again.Count);
                Assert.Equal(1, again.Count(alias => alias.Name == Folder));
                using var client = restarted.GetTestClient();
                Assert.Contains("Shop", await TitleAsync(client, "http://" + Shop + "/"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(restarted);
            }
        }

        [Fact]
        public async Task An_invalid_list_refuses_the_boot_by_name_and_creates_nothing()
        {
            using var scratch = new Scratch();
            // The first boot installs the root site alone.
            var first = await BootAsync(scratch, expectSuccess: true);
            var before = await AliasesAsync(first);
            await GateDrive.ShutdownAsync(first);

            // The second boot's list is valid in its first entry (a new tenant) and invalid in its second (a duplicate alias).
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Shop",
                ["Installation__Sites__0__Aliases"] = Shop,
                ["Installation__Sites__0__Tenant"] = "Shop",
                ["Installation__Sites__0__ConnectionString"] = "Data Source=|DataDirectory|/shop.db;",
                ["Installation__Sites__1__Name"] = "Shop Again",
                ["Installation__Sites__1__Aliases"] = Shop
            });
            var second = await BootAsync(scratch, expectSuccess: false);
            try
            {
                Assert.Equal(before.Select(alias => alias.Name), (await AliasesAsync(second)).Select(alias => alias.Name));
                Assert.False(File.Exists(Path.Combine(scratch.ContentRoot, "Data", "shop.db")), "the valid entry was half-created beside the invalid one");
                using var scope = second.Services.CreateScope();
                Assert.Single(await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync());
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------

        private async Task<WebApplication> BootAsync(Scratch scratch, bool expectSuccess)
        {
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            _output.WriteLine($"[boot] success={install.Success} message='{install.Message}'");
            if (expectSuccess)
            {
                Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
                await app.StartAsync();
            }
            else
            {
                Assert.False(install.Success);
                Assert.Contains("Installation:Sites is not valid, so no site was created", install.Message);
                Assert.Contains("Installation:Sites:1: the alias 'shop.example.com' is also listed by Installation:Sites:0", install.Message);
                Assert.DoesNotContain("shop.db", install.Message); // a connection string is never echoed
            }
            return app;
        }

        private static async Task<List<Alias>> AliasesAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICacheManager>().RemoveCacheAsync("Aliases");
            return (await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync()).ToList();
        }

        private async Task<string> TitleAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            var html = await response.Content.ReadAsStringAsync();
            var title = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline).Groups[1].Value;
            _output.WriteLine($"GET {url} -> {(int)response.StatusCode} '{title}'");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return title;
        }

        private async Task<string?> SiteNameAsync(HttpClient client, string url)
        {
            var body = await client.GetStringAsync(url);
            var name = JsonNode.Parse(body)?["name"]?.GetValue<string>();
            _output.WriteLine($"GET {url} -> '{name}'");
            return name;
        }

        /// <summary>Process environment variables for one test, restored after it (the collection runs one at a time).</summary>
        private sealed class EnvironmentVariables : IDisposable
        {
            private readonly Dictionary<string, string?> _previous = new();

            public EnvironmentVariables(Dictionary<string, string> values)
            {
                foreach (var (key, value) in values)
                {
                    _previous[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            public void Dispose()
            {
                foreach (var (key, value) in _previous)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        /// <summary>A content root whose override file holds the installation's own settings (the password as a file).</summary>
        private sealed class Scratch : IDisposable
        {
            private readonly string? _previousOverridePath;

            public Scratch()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-sites-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "Data"));
                System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));
                var passwordFile = Path.Combine(ContentRoot, "host-password");
                System.IO.File.WriteAllText(passwordFile, Password + "\n");

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = "Data Source=|DataDirectory|/root.db;" },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = Root,
                        ["HostEmail"] = GateDrive.HostEmail,
                        ["HostPasswordFile"] = passwordFile
                    }
                };
                System.IO.File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);
                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort - SQLite may still hold a file
                }
            }
        }
    }
}
