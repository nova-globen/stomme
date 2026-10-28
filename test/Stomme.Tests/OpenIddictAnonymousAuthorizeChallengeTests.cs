using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-71 — an ANONYMOUS browser arriving at <c>/connect/authorize</c> must be taken to the LOGIN PAGE,
    /// not answered with a bare 403.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect, reported by the Fravashi integration and reproduced on the UAT host: the D64
    /// authorize endpoint correctly issues <c>Results.Challenge</c> on the Identity cookie scheme for an
    /// anonymous user, but <c>ConfigureStommeCookieOptions</c> overrides
    /// <c>Events.OnRedirectToLogin</c> to a BARE 403 for EVERY challenge (the SPA/XHR behaviour). That
    /// override swallowed the interactive OIDC hop, so an external client's browser window received 403 +
    /// the SPA shell and rendered blank. The D64 comment's "its configured login path" was never reached.
    /// </para>
    /// <para>
    /// Both directions are pinned here, because the fix is a CARVE-OUT and a carve-out can be too wide:
    /// the authorize path must redirect, and any OTHER challenged path must still answer 403. A fix that
    /// restored the framework default globally would break every XHR caller in the SPA — and would pass a
    /// one-sided test.
    /// </para>
    /// <para>
    /// PROVIDER DIMENSION — deliberately SQLite only. The defect and its fix live entirely in the cookie
    /// authentication events (an ASP.NET Core middleware concern); no store is consulted on the path under
    /// test, so a second provider would re-measure the same code. This is NOT a D46 parity surface.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class OpenIddictAnonymousAuthorizeChallengeTests : IClassFixture<OpenIddictAnonymousAuthorizeChallengeTests.AuthorityFixture>
    {
        private readonly AuthorityFixture _fixture;
        private readonly ITestOutputHelper _output;

        public OpenIddictAnonymousAuthorizeChallengeTests(AuthorityFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>
        /// The reported request, verbatim in shape: a public PKCE client, the Fravashi redirect_uri, and the
        /// offline_access scope. Anonymous — no cookie jar.
        /// </summary>
        private static string AuthorizeUrl =>
            "/connect/authorize?" + string.Join('&', new[]
            {
                "client_id=" + Uri.EscapeDataString(AuthorityFixture.ClientId),
                "redirect_uri=" + Uri.EscapeDataString(AuthorityFixture.RedirectUri),
                "response_type=code",
                "scope=" + Uri.EscapeDataString("openid profile email offline_access"),
                "state=x",
                "nonce=y",
                "code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                "code_challenge_method=S256"
            });

        [Fact]
        public async Task Anonymous_authorize_request_is_redirected_to_the_login_page_with_a_returnurl()
        {
            var response = await _fixture.GetAnonymousAsync(AuthorizeUrl);
            var location = response.Headers.Location?.ToString();
            _output.WriteLine($"[authorize] {(int)response.StatusCode} -> {location}");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(location);

            // The login page, carrying the ORIGINAL authorize request as returnurl. The site's own
            // SiteRouter/Login module read "returnurl" (case-insensitively) and post it to /pages/login,
            // which LocalRedirects back here once the cookie is set.
            var uri = new Uri(new Uri("http://" + AuthorityFixture.Host), location!);
            Assert.Equal("/login", uri.AbsolutePath);

            // ParseQuery already percent-decodes ONE layer, which is exactly the layer the redirect added.
            // Decoding twice here would collapse the authorize request's OWN encoding (%252F -> %2F -> /)
            // and silently "prove" a lossless round trip that never happened.
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            Assert.True(query.ContainsKey("returnurl"), $"no returnurl on the login redirect: {location}");
            var returnUrl = query["returnurl"].ToString();
            _output.WriteLine($"[authorize] returnurl = {returnUrl}");

            // The whole authorize request must survive BYTE FOR BYTE — path, every parameter, and the inner
            // encoding of redirect_uri/scope. A naive single-encode corrupts redirect_uri into a value
            // OpenIddict will reject as unregistered, which looks like a client misconfiguration.
            Assert.Equal(AuthorizeUrl, returnUrl);
        }

        [Fact]
        public async Task A_challenge_on_any_other_path_still_answers_403()
        {
            // The SPA/XHR contract this override exists for: an unauthenticated caller of a protected API
            // gets a status code, never an HTML login page. api/job is [Authorize(Roles = Host)], so an
            // anonymous request challenges the SAME cookie scheme the authorize endpoint does.
            var response = await _fixture.GetAnonymousAsync("/api/job");
            _output.WriteLine($"[api/job] {(int)response.StatusCode} -> {response.Headers.Location?.ToString() ?? "(no location)"}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }

        [Fact]
        public async Task The_login_postback_accepts_the_authorize_returnurl_as_a_local_redirect()
        {
            // The other half of the loop the bug report asks to verify: /pages/login must not reject a
            // returnurl that is not a site PAGE. It is a LocalRedirect, so /connect/authorize?... is legal —
            // this fact fails loudly if that ever tightens to page paths only.
            var response = await _fixture.PostLoginAsync(AuthorityFixture.HostUsername, AuthorityFixture.HostPassword, AuthorizeUrl);
            var location = response.Headers.Location?.ToString();
            _output.WriteLine($"[pages/login] {(int)response.StatusCode} -> {location}");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(AuthorizeUrl, location);
        }

        /// <summary>
        /// One installed host for the whole class: a real Stomme host (the real pipeline, transport swapped
        /// for TestServer) with the D64 authority enabled and the Fravashi-shaped PUBLIC PKCE client declared
        /// in config, so the boot-path seeder registers it in the tenant store exactly as an operator's would.
        /// </summary>
        public sealed class AuthorityFixture : IAsyncLifetime
        {
            public const string Host = "localhost";
            public const string ClientId = "fravashi-studio";
            public const string RedirectUri = "http://localhost:9090/auth/callback/idp";
            public const string HostUsername = "hostauthz";
            public const string HostPassword = "R3nder!Guard$2026";
            private const string HostEmail = "hostauthz@example.com";

            private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
            private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

            private string _contentRoot;
            private string _previousOverridePath;
            private WebApplication _app;

            public async Task InitializeAsync()
            {
                _contentRoot = Path.Combine(Path.GetTempPath(), "stomme-authz-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(_contentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["OpenIddict"] = new JsonObject
                    {
                        ["Enabled"] = true,
                        ["Applications"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["ClientId"] = ClientId,
                                ["DisplayName"] = "Fravashi Studio",
                                ["Type"] = "public",
                                ["ConsentType"] = "implicit",
                                ["GrantTypes"] = new JsonArray { "authorization_code", "refresh_token" },
                                ["Scopes"] = new JsonArray { "openid", "profile", "email", "offline_access" },
                                ["RedirectUris"] = new JsonArray
                                {
                                    RedirectUri,
                                    "http://127.0.0.1:9090/auth/callback/idp"
                                },
                                ["PostLogoutRedirectUris"] = new JsonArray { "http://localhost:9090/" },
                                ["RequirePkce"] = true
                            }
                        }
                    },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection_OpenIddict"] = $"Data Source={Path.Combine(_contentRoot, "oidc.db")};"
                    }
                };
                System.IO.File.WriteAllText(
                    Path.Combine(_contentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _contentRoot);

                _app = await Program.BuildAppAsync(
                    new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                    builder =>
                    {
                        builder.WebHost.UseTestServer();
                        // TestServer has no real connection; ValidateUser NREs on a null RemoteIpAddress.
                        builder.Services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    });

                var databaseManager = _app.Services.GetRequiredService<IDatabaseManager>();
                var installTask = Task.Run(() => databaseManager.InstallAsync(new InstallConfig
                {
                    DatabaseType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ConnectionString = $"Data Source={Path.Combine(_contentRoot, "tenant.db")};",
                    Aliases = Host,
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = HostUsername,
                    HostPassword = HostPassword,
                    HostEmail = HostEmail,
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException("Install did not finish in time.");
                }
                var install = await installTask;
                if (!install.Success)
                {
                    throw new InvalidOperationException($"Install failed: {install.Message}");
                }

                await _app.StartAsync();
            }

            /// <summary>A request with NO cookie jar — the anonymous browser of the bug report.</summary>
            public async Task<HttpResponseMessage> GetAnonymousAsync(string url)
            {
                using var client = new HttpClient(_app.GetTestServer().CreateHandler())
                {
                    BaseAddress = new Uri("http://" + Host)
                };
                return await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri("http://" + Host), url)));
            }

            /// <summary>The real <c>/pages/login</c> postback, antiforgery satisfied through the app's own ring.</summary>
            public async Task<HttpResponseMessage> PostLoginAsync(string username, string password, string returnUrl)
            {
                string requestToken;
                string antiforgeryCookie;
                using (var scope = _app.Services.CreateScope())
                {
                    var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
                    var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                    var tokens = antiforgery.GetAndStoreTokens(httpContext);
                    requestToken = tokens.RequestToken;
                    antiforgeryCookie = httpContext.Response.Headers.SetCookie.ToString().Split(';')[0];
                }

                using var client = new HttpClient(_app.GetTestServer().CreateHandler())
                {
                    BaseAddress = new Uri("http://" + Host)
                };
                var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["username"] = username,
                        ["password"] = password,
                        ["remember"] = "false",
                        ["returnurl"] = WebUtility.UrlEncode(returnUrl),
                        ["__RequestVerificationToken"] = requestToken
                    })
                };
                request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
                return await client.SendAsync(request);
            }

            public async Task DisposeAsync()
            {
                if (_app != null)
                {
                    try { await _app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
                    try { await _app.DisposeAsync(); } catch { /* best effort */ }
                }
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(_contentRoot);   // this install's pools only - BL-104
                try { Directory.Delete(_contentRoot, true); } catch { /* best effort */ }
            }

            private sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
            {
                public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
                {
                    app.Use((context, continuation) =>
                    {
                        context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                        context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                        return continuation();
                    });
                    next(app);
                };
            }
        }
    }
}
