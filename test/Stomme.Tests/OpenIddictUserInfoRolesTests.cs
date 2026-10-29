#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// M2 row 2 (ruling C34): <c>/connect/userinfo</c> answers a <c>profile</c>-scoped access token with the user's role
    /// names on the issuer's site (<c>role</c>, an array), so Fravashi's broker can entitle by role instead of a hand-kept
    /// allow-list. Driven as the studio does it: a public PKCE client configured by environment variables on an installed
    /// host, a cookie sign-in, authorize, the code exchanged at the token endpoint, then userinfo with the bearer token.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class OpenIddictUserInfoRolesTests
    {
        private const string ClientId = "drive-studio";
        private const string Redirect = "http://localhost:9090/auth/callback/idp";

        private readonly ITestOutputHelper _output;

        public OpenIddictUserInfoRolesTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Userinfo_carries_the_users_roles_on_the_site_for_a_profile_scoped_token_and_none_without_profile()
        {
            using var scratch = new FolderSiteCookieTests.Scratch();
            using var env = new FolderSiteCookieTests.EnvironmentVariables(new Dictionary<string, string>
            {
                ["OpenIddict__Enabled"] = "true",
                ["OpenIddict__Applications__0__ClientId"] = ClientId,
                ["OpenIddict__Applications__0__Type"] = "public",
                ["OpenIddict__Applications__0__RequirePkce"] = "true",
                ["OpenIddict__Applications__0__GrantTypes__0"] = "authorization_code",
                ["OpenIddict__Applications__0__GrantTypes__1"] = "refresh_token",
                ["OpenIddict__Applications__0__Scopes__0"] = "openid",
                ["OpenIddict__Applications__0__Scopes__1"] = "profile",
                ["OpenIddict__Applications__0__Scopes__2"] = "email",
                ["OpenIddict__Applications__0__Scopes__3"] = "offline_access",
                ["OpenIddict__Applications__0__RedirectUris__0"] = Redirect
            });
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = GateDrive.NewClient(app, "localhost");
                var jar = await GateDrive.LoginAsync(app, client, "localhost", UserNames.Host, FolderSiteCookieTests.Password, _output);

                var withProfile = await UserInfoAsync(client, jar, "openid profile");
                var roles = withProfile["role"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
                Assert.Contains(RoleNames.Host, roles);
                Assert.Contains(RoleNames.Admin, roles);
                Assert.Contains(RoleNames.Registered, roles);
                Assert.Equal(roles.Distinct().Count(), roles.Count);

                var withoutProfile = await UserInfoAsync(client, jar, "openid");
                Assert.Null(withoutProfile["role"]);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<JsonObject> UserInfoAsync(HttpClient client, System.Net.CookieContainer jar, string scope)
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorize = QueryHelpers.AddQueryString("http://localhost/connect/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = ClientId,
                ["response_type"] = "code",
                ["scope"] = scope,
                ["redirect_uri"] = Redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "drive"
            });
            var request = new HttpRequestMessage(HttpMethod.Get, authorize);
            request.Headers.TryAddWithoutValidation("Cookie", jar.GetCookieHeader(new Uri("http://localhost/")));
            var response = await client.SendAsync(request);
            var location = response.Headers.Location?.ToString() ?? "";
            _output.WriteLine($"authorize ({scope}) -> {(int)response.StatusCode} {location}");
            Assert.StartsWith(Redirect, location);
            var code = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
            Assert.False(string.IsNullOrEmpty(code), "no authorization code");

            var token = await client.PostAsync("http://localhost/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Redirect,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier
            }));
            var tokenBody = await token.Content.ReadAsStringAsync();
            Assert.True(token.IsSuccessStatusCode, $"token: {(int)token.StatusCode} {tokenBody}");
            var accessToken = JsonNode.Parse(tokenBody)!["access_token"]!.GetValue<string>();

            var userinfo = new HttpRequestMessage(HttpMethod.Get, "http://localhost/connect/userinfo");
            userinfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var answer = await client.SendAsync(userinfo);
            var body = await answer.Content.ReadAsStringAsync();
            _output.WriteLine($"userinfo ({scope}) -> {(int)answer.StatusCode} {body}");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            return JsonNode.Parse(body)!.AsObject();
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
