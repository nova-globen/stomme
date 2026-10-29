#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// A site in a folder (<c>host/sv</c>) keeps the framework's cookies to itself - the
    /// sign-in, antiforgery and release cookies it sets are named <c>&lt;name&gt;-sv</c> and scoped to <c>Path=/sv</c>, and a
    /// same-named cookie the root site set at <c>Path=/</c> is never read there. Without it one sign-in served every site on
    /// the host. The culture cookie, set in the browser, takes the site's path instead (<see cref="Utilities.SitePath"/>).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class FolderSiteCookieTests
    {
        private const string Root = "localhost";
        private const string Folder = "localhost/sv";
        internal const string Password = "F0lder!Cookies$2026";

        private static readonly string[] Names = { ".AspNetCore.Identity.", Constants.AntiForgeryTokenCookieName, "stomme-release" };

        private readonly ITestOutputHelper _output;

        public FolderSiteCookieTests(ITestOutputHelper output) => _output = output;

        // ---- the rules, on their own -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(".AspNetCore.Identity.Application=abc; path=/; secure; samesite=lax; httponly", ".AspNetCore.Identity.Application-sv=abc; secure; samesite=lax; httponly; path=/sv")]
        [InlineData(".AspNetCore.Identity.ApplicationC1=part; path=/; httponly", ".AspNetCore.Identity.ApplicationC1-sv=part; httponly; path=/sv")]
        [InlineData("X-XSRF-TOKEN-COOKIE=tok; path=/; samesite=strict; httponly", "X-XSRF-TOKEN-COOKIE-sv=tok; samesite=strict; httponly; path=/sv")]
        [InlineData("stomme-release=10.75.0; expires=Thu, 01 Jan 2099 00:00:00 GMT; path=/", "stomme-release-sv=10.75.0; expires=Thu, 01 Jan 2099 00:00:00 GMT; path=/sv")]
        [InlineData("module-own=1; path=/", "module-own=1; path=/")]                                  // a module's own cookie is its own business
        [InlineData("X-XSRF-TOKEN-COOKIE-sv=tok; path=/sv", "X-XSRF-TOKEN-COOKIE-sv=tok; path=/sv")]  // already scoped
        public void A_framework_cookie_a_folder_site_sets_is_renamed_and_scoped_to_its_path(string setCookie, string expected)
        {
            Assert.Equal(expected, FolderSiteCookieMiddleware.Scope(setCookie, "sv", "/sv", Names));
        }

        [Fact]
        public void A_folder_site_reads_its_own_framework_cookies_under_their_names_and_never_another_sites()
        {
            // The browser sends the root's Path=/ cookies to /sv as well, in either order.
            var header = ".AspNetCore.Identity.Application=ROOT; X-XSRF-TOKEN-COOKIE=ROOTTOKEN; theme=dark; " +
                ".AspNetCore.Identity.Application-sv=SV; X-XSRF-TOKEN-COOKIE-sv=SVTOKEN";
            Assert.Equal(".AspNetCore.Identity.Application=SV; X-XSRF-TOKEN-COOKIE=SVTOKEN; theme=dark",
                FolderSiteCookieMiddleware.TranslateHeader(header, "sv", Names));

            // Only the root's: the folder site sees no sign-in at all.
            Assert.Equal("theme=dark", FolderSiteCookieMiddleware.TranslateHeader(".AspNetCore.Identity.Application=ROOT; theme=dark", "sv", Names));

            // No framework cookie: nothing to change.
            Assert.Null(FolderSiteCookieMiddleware.TranslateHeader("theme=dark", "sv", Names));

            Assert.Equal("sv", FolderSiteCookieMiddleware.SiteKey("sv"));
            Assert.Equal("shop-se", FolderSiteCookieMiddleware.SiteKey("/Shop/SE/"));
            Assert.Equal("/", Utilities.SitePath(""));
            Assert.Equal("/sv", Utilities.SitePath("sv/"));
        }

        // ---- on a real host: a root site and a folder site on one tenant -------------------------------------------------

        [Fact]
        public async Task A_sign_in_at_the_root_is_not_a_sign_in_in_the_folder_site_and_the_reverse()
        {
            using var scratch = new Scratch();
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Svenska",
                ["Installation__Sites__0__Aliases"] = Folder
            });
            var app = await BootAsync(scratch);
            try
            {
                using var client = GateDrive.NewClient(app, Root);

                // The folder site's page sets its framework cookies under its own names, at its own path.
                var page = await client.GetAsync("http://" + Folder + "/");
                var folderCookies = page.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : new List<string>();
                _output.WriteLine("GET /sv/ Set-Cookie: " + string.Join(" | ", folderCookies));
                Assert.Contains(folderCookies, value => value.StartsWith(Constants.AntiForgeryTokenCookieName + "-sv=", StringComparison.Ordinal) && value.Contains("path=/sv", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(folderCookies, value => value.StartsWith(Constants.AntiForgeryTokenCookieName + "=", StringComparison.Ordinal));

                // Signed in at the ROOT: the root says so; the folder site, sent the same Path=/ cookie, does not.
                var rootCookies = await GateDrive.LoginAsync(app, client, Root, UserNames.Host, Password, _output);
                Assert.True(await AuthenticatedAsync(client, rootCookies, "/api/User/authenticate"), "the root sign-in did not hold at the root");
                Assert.False(await AuthenticatedAsync(client, rootCookies, "/sv/api/User/authenticate"), "the root's sign-in cookie signed the caller in to the folder site");

                // Signed in at the FOLDER site: its cookie is its own, at /sv - the root does not see it.
                var folderJar = await LoginAtFolderAsync(app, client);
                Assert.True(await AuthenticatedAsync(client, folderJar, "/sv/api/User/authenticate"), "the folder site's own sign-in did not hold there");
                Assert.False(await AuthenticatedAsync(client, folderJar, "/api/User/authenticate"), "the folder site's sign-in leaked to the root");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task On_an_interactive_folder_site_the_servers_own_api_calls_carry_the_sites_sign_in_and_antiforgery_cookies()
        {
            // An interactive page's services call the site's API from the server, through the host's HttpClient, with the
            // cookies of the request the circuit started in - which the folder middleware had already handed to the
            // framework under their usual names. Forwarded under those names, the self-call's own translation dropped them as
            // another site's: the sign-in form answered 400 (no antiforgery cookie) and no signed-in call was signed in.
            using var scratch = new Scratch();
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Svenska",
                ["Installation__Sites__0__Aliases"] = Folder
            });
            var app = await BootAsync(scratch);
            try
            {
                using var client = GateDrive.NewClient(app, Root);
                var alias = await GateDrive.AliasAsync(app, Folder);
                var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app);
                var browser = antiforgeryCookie.Replace(Constants.AntiForgeryTokenCookieName + "=", Constants.AntiForgeryTokenCookieName + "-sv=", StringComparison.Ordinal);

                // The sign-in form, as the server calls it.
                var forwarded = SelfCallCookies(app, alias, browser);
                Assert.Contains(forwarded, cookie => cookie.StartsWith(Constants.AntiForgeryTokenCookieName + "-sv=", StringComparison.Ordinal));
                var signIn = await SignInAsync(client, forwarded, requestToken);
                Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
                Assert.True(JsonNode.Parse(await signIn.Content.ReadAsStringAsync())?["isAuthenticated"]?.GetValue<bool>() == true, "the folder site's sign-in through the server did not sign the host in");
                var identity = signIn.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])
                    .Single(pair => pair.StartsWith(".AspNetCore.Identity.Application-sv=", StringComparison.Ordinal));

                // One signed-in call, as the server makes it on behalf of the now signed-in browser.
                var signedIn = SelfCallCookies(app, alias, browser + "; " + identity);
                var request = new HttpRequestMessage(HttpMethod.Get, "http://" + Folder + "/api/User/authenticate");
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", signedIn));
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                _output.WriteLine($"GET /sv/api/User/authenticate (server-side) -> {(int)response.StatusCode} {body}");
                Assert.True(JsonNode.Parse(body)?["isAuthenticated"]?.GetValue<bool>() == true, "a signed-in call the server made for the folder site was not signed in");

                // What it did before: the framework names forwarded as they are - refused for want of the antiforgery cookie.
                var bare = FolderSiteCookieMiddleware.TranslateHeader(browser, "sv", Names)!.Split("; ");
                Assert.Equal(HttpStatusCode.BadRequest, (await SignInAsync(client, bare, requestToken)).StatusCode);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------

        // The Cookie header the host's own HttpClient carries for a circuit started by a browser request to the folder site:
        // that request as the folder middleware left it (framework names), the client taken from the host's container.
        private List<string> SelfCallCookies(WebApplication app, Alias alias, string browserHeader)
        {
            var circuit = new DefaultHttpContext();
            circuit.Request.Scheme = "http";
            circuit.Request.Host = new HostString(Root);
            circuit.Request.Headers.Cookie = FolderSiteCookieMiddleware.TranslateHeader(browserHeader, "sv", Names) ?? browserHeader;
            circuit.Items[Constants.HttpContextAliasKey] = alias;

            var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
            var previous = accessor.HttpContext;
            accessor.HttpContext = circuit;
            try
            {
                using var scope = app.Services.CreateScope();
                var cookies = scope.ServiceProvider.GetRequiredService<HttpClient>().DefaultRequestHeaders.GetValues("Cookie").ToList();
                _output.WriteLine("server-side HttpClient cookies: " + string.Join(", ", cookies.Select(cookie => cookie.Split('=')[0])));
                return cookies;
            }
            finally
            {
                accessor.HttpContext = previous;
            }
        }

        private async Task<HttpResponseMessage> SignInAsync(HttpClient client, IEnumerable<string> cookies, string requestToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://" + Folder + "/api/User/signin?setCookie=true&isPersistent=false")
            {
                Content = JsonContent.Create(new { username = UserNames.Host, password = Password })
            };
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            var response = await client.SendAsync(request);
            _output.WriteLine($"POST /sv/api/User/signin (cookies: {string.Join(", ", cookies.Select(cookie => cookie.Split('=')[0]))}) -> {(int)response.StatusCode}");
            return response;
        }

        private async Task<bool> AuthenticatedAsync(HttpClient client, CookieContainer jar, string path)
        {
            var uri = new Uri("http://" + Root + path);
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = jar.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"GET {path} (cookies: {string.Join(", ", header.Split(';').Select(part => part.Split('=')[0].Trim()))}) -> {(int)response.StatusCode} {body}");
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(body)?["isAuthenticated"]?.GetValue<bool>() == true;
        }

        // The folder site's own sign-in form post: its antiforgery cookie travels under the folder site's name.
        private async Task<CookieContainer> LoginAtFolderAsync(WebApplication app, HttpClient client)
        {
            var jar = new CookieContainer();
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app);
            var cookie = antiforgeryCookie.Replace(Constants.AntiForgeryTokenCookieName + "=", Constants.AntiForgeryTokenCookieName + "-sv=", StringComparison.Ordinal);
            var request = new HttpRequestMessage(HttpMethod.Post, "http://" + Folder + "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = UserNames.Host,
                    ["password"] = Password,
                    ["remember"] = "false",
                    ["returnurl"] = "/sv/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await client.SendAsync(request);
            GateDrive.CaptureCookies(jar, new Uri("http://" + Folder + "/"), response);
            var issued = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : new List<string>();
            _output.WriteLine($"POST /sv/pages/login -> {(int)response.StatusCode}; Set-Cookie: {string.Join(" | ", issued.Select(value => value.Split(';')[0].Split('=')[0] + " " + string.Join(";", value.Split(';').Skip(1).Where(part => part.Trim().StartsWith("path", StringComparison.OrdinalIgnoreCase)))))}");
            Assert.Contains(issued, value => value.StartsWith(".AspNetCore.Identity.Application-sv=", StringComparison.Ordinal) && value.Contains("path=/sv", StringComparison.OrdinalIgnoreCase));
            return jar;
        }

        private async Task<WebApplication> BootAsync(Scratch scratch)
        {
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            _output.WriteLine($"[boot] success={install.Success} message='{install.Message}'");
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            return app;
        }

        /// <summary>Process environment variables for one test, restored after it (the collection runs one at a time).</summary>
        internal sealed class EnvironmentVariables : IDisposable
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
        internal sealed class Scratch : IDisposable
        {
            private readonly string? _previousOverridePath;

            public Scratch()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-folder-cookies-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "Data"));
                File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));
                var passwordFile = Path.Combine(ContentRoot, "host-password");
                File.WriteAllText(passwordFile, Password + "\n");
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
                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"), overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
