#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Identity.OpenIddict;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A client-credentials application's SERVICE ACCOUNT, driven over HTTP on an
    /// installed host with <c>OpenIddict:Enabled</c> and <c>OpenIddict:ServiceAccounts:Enabled</c> in its override file: a
    /// client token signs no one in to <c>api/*</c> until the Host creates the account, then signs in THAT account (its own
    /// roles, read per request); the account is locked out of sign-in for good; a person who holds the derived name first is
    /// never taken over (create refused, and the client's tokens still sign no one in); removing the account ends it. With the
    /// switch off, the endpoints answer 404 and a client token signs no one in.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class OpenIddictServiceAccountTests
    {
        private const string Host = "localhost";
        private const string Secret = "Svc!Client$2026-secret";

        private readonly ITestOutputHelper _output;

        protected OpenIddictServiceAccountTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        private static JsonObject Settings(bool serviceAccounts) => new()
        {
            ["OpenIddict"] = new JsonObject { ["Enabled"] = true, ["ServiceAccounts"] = new JsonObject { ["Enabled"] = serviceAccounts } },
        };

        [Fact]
        public async Task A_client_token_acts_as_its_service_account_and_only_as_one_the_server_made()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(true));
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var host = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await RegisterClientAsync(app, client, host, hostId, "reporting-service");
                await RegisterClientAsync(app, client, host, hostId, "taken-service");

                // No service account yet: the token is valid at the authority and signs no one in.
                var token = await ClientTokenAsync(client, "reporting-service");
                Assert.False((await AuthenticateAsync(client, token))["isAuthenticated"]?.GetValue<bool>());

                // The Host creates it; the same token now signs in that account, with the site's default role only.
                var created = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Post, "/api/OpenIddict/applications/reporting-service/service-account", null, _output, "create");
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var account = JsonNode.Parse(created.Body)!;
                var username = ServiceAccounts.UsernameFor("reporting-service");
                Assert.Equal(username, account["username"]?.GetValue<string>());
                Assert.True(account["exists"]?.GetValue<bool>());

                var signedIn = await AuthenticateAsync(client, token);
                _output.WriteLine($"[{Store().Name}] client token -> {signedIn.ToJsonString()}");
                Assert.True(signedIn["isAuthenticated"]?.GetValue<bool>());
                Assert.Equal(username, signedIn["username"]?.GetValue<string>());
                Assert.Contains(RoleNames.Registered, signedIn["roles"]?.GetValue<string>() ?? "");
                Assert.DoesNotContain(RoleNames.Admin, signedIn["roles"]?.GetValue<string>() ?? "");

                // It can never sign in: locked out for good, by the server.
                Assert.True(await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var identityUsers = sp.GetRequiredService<UserManager<IdentityUser>>();
                    var identityUser = await identityUsers.FindByNameAsync(username);
                    return identityUser != null && await ServiceAccounts.IsLockedOutForGoodAsync(identityUsers, identityUser) && await identityUsers.IsLockedOutAsync(identityUser);
                }));

                // The switch decides at every request: turned off in the override file (which the host reloads), the same
                // token of the same account signs no one in; turned back on, it does again.
                await SetSwitchAsync(scratch.ContentRoot, false);
                Assert.True(await EventuallyAsync(async () => (await AuthenticateAsync(client, token))["isAuthenticated"]?.GetValue<bool>() == false), "the token still signed in with the switch off");
                await SetSwitchAsync(scratch.ContentRoot, true);
                Assert.True(await EventuallyAsync(async () => (await AuthenticateAsync(client, token))["isAuthenticated"]?.GetValue<bool>() == true), "the token signed no one in with the switch back on");

                // A second create is refused.
                var again = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Post, "/api/OpenIddict/applications/reporting-service/service-account", null, _output, "create again");
                Assert.Equal(HttpStatusCode.Conflict, again.Status);

                // A PERSON who holds the derived name first is never taken over: create refused, and the client's tokens
                // still sign no one in.
                var takenName = ServiceAccounts.UsernameFor("taken-service");
                await GateDrive.AddUserAsync(app, Host, takenName, "P3rson!Holds$2026", "person@gate.test");
                var refused = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Post, "/api/OpenIddict/applications/taken-service/service-account", null, _output, "create over a person");
                Assert.Equal(HttpStatusCode.Conflict, refused.Status);
                Assert.Contains("was not made by the server", refused.Body);
                Assert.False((await AuthenticateAsync(client, await ClientTokenAsync(client, "taken-service")))["isAuthenticated"]?.GetValue<bool>());

                // Removing the account ends it.
                var removed = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Delete, "/api/OpenIddict/applications/reporting-service/service-account", null, _output, "remove");
                Assert.Equal(HttpStatusCode.NoContent, removed.Status);
                Assert.False((await AuthenticateAsync(client, token))["isAuthenticated"]?.GetValue<bool>());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_switch_off_there_are_no_service_accounts()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(false));
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var host = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await RegisterClientAsync(app, client, host, hostId, "reporting-service");

                var create = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Post, "/api/OpenIddict/applications/reporting-service/service-account", null, _output, "create while off");
                Assert.Equal(HttpStatusCode.NotFound, create.Status);
                Assert.False((await AuthenticateAsync(client, await ClientTokenAsync(client, "reporting-service")))["isAuthenticated"]?.GetValue<bool>());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task RegisterClientAsync(WebApplication app, HttpClient client, System.Net.CookieContainer host, int hostId, string clientId)
        {
            var seed = new OpenIddictApplicationSeed
            {
                ClientId = clientId,
                ClientSecret = Secret,
                DisplayName = clientId,
                Type = "confidential",
                GrantTypes = new List<string> { "client_credentials" },
                Scopes = new List<string> { "api" },
            };
            var saved = await GateDrive.SendJsonAsync(app, client, host, Host, hostId, HttpMethod.Post, "/api/OpenIddict/applications", seed, _output, "register " + clientId);
            Assert.Equal(HttpStatusCode.OK, saved.Status);
        }

        private static async Task SetSwitchAsync(string contentRoot, bool enabled)
        {
            var file = System.IO.Path.Combine(contentRoot, "appsettings.override.json");
            var root = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file))!.AsObject();
            root["OpenIddict"]!["ServiceAccounts"]!["Enabled"] = enabled;
            await System.IO.File.WriteAllTextAsync(file, root.ToJsonString());
        }

        // The configuration reloads the file when it changes, which takes a moment: poll for up to ten seconds.
        private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (await condition())
                {
                    return true;
                }

                await Task.Delay(250);
            }

            return false;
        }

        private static async Task<string> ClientTokenAsync(HttpClient client, string clientId)
        {
            var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = Secret,
                ["scope"] = "api",
            }));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"the token endpoint answered {(int)response.StatusCode}: {body}");
            return JsonNode.Parse(body)!["access_token"]!.GetValue<string>();
        }

        private static async Task<JsonNode> AuthenticateAsync(HttpClient client, string bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/authenticate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
    }

    public sealed class SqliteOpenIddictServiceAccountTests(ITestOutputHelper output) : OpenIddictServiceAccountTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresOpenIddictServiceAccountTests(ITestOutputHelper output) : OpenIddictServiceAccountTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenOpenIddictServiceAccountTests(ITestOutputHelper output) : OpenIddictServiceAccountTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
