using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// Driven over real HTTP on an installed SQLite site: the Host-only licensing
    /// surface <c>api/Licence</c>. POST verifies FIRST, writes <c>Licensing:Keys:&lt;lid&gt;</c> through ConfigManager and READS
    /// IT BACK (refutation 7: <c>AddOrUpdateSetting</c> swallows every error and returns void, so a 200 without a read-back
    /// would lie about a read-only settings file); GET reports each key's reason and CONFIGURATION SOURCE; DELETE refuses a
    /// key a non-writable source supplies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authorization runs BEFORE any action code (falsification F-J), so anonymous is 403 (the cookie
    /// <c>OnRedirectToLogin</c> answers 403 for API calls, refutation 4), an Administrator is 403 in every mode, and the
    /// mode-unset 404 is observable only as the Host.
    /// </para>
    /// <para>
    /// <see cref="LicenceKeyRing.OverrideForTests"/> is process-wide and refuses to nest; this class shares the
    /// render-guard collection (<c>DisableParallelization = true</c>) with the other override user that installs a site,
    /// and that collection never runs beside the <c>LicenceRing</c> collection. The same collection serialises the
    /// process-environment write in <see cref="An_environment_supplied_key_reports_its_source_and_cannot_be_deleted_from_the_page"/>.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LicenceControllerAuthorizationTests
    {
        private const string Host = "localhost";
        private const string ApiPath = "/api/Licence";
        private const string OverrideFileName = "appsettings.override.json";

        private const string AdminUsername = "licenceadmin";
        private const string AdminPassword = "L1cence!Admin$2026";
        private const string AdminEmail = "licenceadmin@gate.test";

        private const string Entitlement = "novagloben.licencetest.pro";

        private readonly ITestOutputHelper _output;

        public LicenceControllerAuthorizationTests(ITestOutputHelper output) => _output = output;

        private static JsonObject OnPrem() => new() { ["Deployment"] = new JsonObject { ["Mode"] = "OnPrem" } };

        // ---- (1) ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Host_posts_a_valid_key_which_is_written_read_back_reported_with_its_source_and_deletable()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, OnPrem());

            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var (cookies, hostId) = await LoginHostAsync(app, client);

                var installationId = InstallationId(app);
                var payload = issuer.Payload(installationId, ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30), lbl: "Controller test licence");
                var blob = issuer.Issue(payload);
                var lid = payload.Lid.ToString("D");
                var configKey = $"{OfflineLicenceEntitlementProvider.KeysSection}:{lid}";

                var post = await SendAsync(app, client, cookies, hostId, HttpMethod.Post, ApiPath, new { Key = blob }, "Host POST valid");
                Assert.Equal(HttpStatusCode.OK, post.Status);
                AssertNoBlob(post.Body, blob);

                // The write reached the WRITABLE file (the override, under the harness) and the live root.
                var file = JsonNode.Parse(File.ReadAllText(OverridePath(scratch)))!;
                Assert.Equal(blob, file["Licensing"]?["Keys"]?[lid]?.GetValue<string>());
                Assert.Equal(blob, app.Services.GetRequiredService<IConfigurationRoot>()[configKey]);

                var get = await SendAsync(app, client, cookies, null, HttpMethod.Get, ApiPath, null, "Host GET after POST");
                Assert.Equal(HttpStatusCode.OK, get.Status);
                AssertNoBlob(get.Body, blob);

                var status = JsonNode.Parse(get.Body)!;
                Assert.Equal(installationId, Str(status, "InstallationId"));
                Assert.True(Prop(status, "InstallationInitialised")!.GetValue<bool>());
                var key = FindKey(status, lid);
                Assert.Equal("Valid", Str(key, "Reason"));
                Assert.Equal("Controller test licence", Str(key, "Label"));
                Assert.Equal(OverrideFileName, Str(key, "Source"));
                Assert.True(Prop(key, "Deletable")!.GetValue<bool>());
                Assert.Contains(Entitlement, Prop(key, "Entitlements")!.AsArray().Select(e => e!.GetValue<string>()));

                // DELETE from the writable source: gone from the file and from the root.
                var delete = await SendAsync(app, client, cookies, hostId, HttpMethod.Delete, $"{ApiPath}/{lid}", null, "Host DELETE");
                Assert.Equal(HttpStatusCode.OK, delete.Status);
                var fileAfter = JsonNode.Parse(File.ReadAllText(OverridePath(scratch)))!;
                Assert.Null(fileAfter["Licensing"]?["Keys"]?[lid]);
                Assert.Null(app.Services.GetRequiredService<IConfigurationRoot>()[configKey]);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- (2) + (8) ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Host_posts_of_a_tampered_key_and_of_a_key_for_another_installation_are_400_with_the_reason_and_write_nothing()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, OnPrem());

            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var (cookies, hostId) = await LoginHostAsync(app, client);
                var installationId = InstallationId(app);

                // (2) one signature character changed, everything else intact -> BadSignature (not Malformed).
                var valid = issuer.Issue(issuer.Payload(installationId, ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30)));
                var tampered = TamperSignature(valid);
                var before = File.ReadAllBytes(OverridePath(scratch));

                var badSignature = await SendAsync(app, client, cookies, hostId, HttpMethod.Post, ApiPath, new { Key = tampered }, "Host POST tampered");
                Assert.Equal(HttpStatusCode.BadRequest, badSignature.Status);
                Assert.Contains("BadSignature", badSignature.Body, StringComparison.Ordinal);
                Assert.Equal(before, File.ReadAllBytes(OverridePath(scratch)));

                // (8) a genuinely signed key minted for ANOTHER installation id -> WrongInstallation.
                var foreign = issuer.Issue(issuer.Payload(Guid.NewGuid().ToString(), ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30)));
                var wrongInstallation = await SendAsync(app, client, cookies, hostId, HttpMethod.Post, ApiPath, new { Key = foreign }, "Host POST foreign installation");
                Assert.Equal(HttpStatusCode.BadRequest, wrongInstallation.Status);
                Assert.Contains("WrongInstallation", wrongInstallation.Body, StringComparison.Ordinal);
                Assert.Equal(before, File.ReadAllBytes(OverridePath(scratch)));

                Assert.Empty(app.Services.GetRequiredService<IConfigurationRoot>().GetSection(OfflineLicenceEntitlementProvider.KeysSection).GetChildren());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- (3) ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_valid_key_posted_while_the_settings_file_is_read_only_is_a_failure_response_and_not_a_false_200()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, OnPrem());

            var app = await GateDrive.InstallAsync(scratch, _output);
            var overridePath = OverridePath(scratch);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var (cookies, hostId) = await LoginHostAsync(app, client);

                var payload = issuer.Payload(InstallationId(app), ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30));
                var blob = issuer.Issue(payload);
                var configKey = $"{OfflineLicenceEntitlementProvider.KeysSection}:{payload.Lid:D}";

                var before = File.ReadAllBytes(overridePath);
                File.SetAttributes(overridePath, File.GetAttributes(overridePath) | FileAttributes.ReadOnly);

                var post = await SendAsync(app, client, cookies, hostId, HttpMethod.Post, ApiPath, new { Key = blob }, "Host POST valid, read-only settings");
                Assert.NotEqual(HttpStatusCode.OK, post.Status);
                Assert.Equal(HttpStatusCode.InternalServerError, post.Status);
                Assert.Contains(OverrideFileName, post.Body, StringComparison.Ordinal);
                Assert.Contains("read-only", post.Body, StringComparison.Ordinal);
                AssertNoBlob(post.Body, blob);

                Assert.Null(app.Services.GetRequiredService<IConfigurationRoot>()[configKey]);
                Assert.Equal(before, File.ReadAllBytes(overridePath));
            }
            finally
            {
                try { File.SetAttributes(overridePath, File.GetAttributes(overridePath) & ~FileAttributes.ReadOnly); } catch { /* best effort */ }
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- (4) + (5) ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_Administrator_and_an_anonymous_caller_are_403_on_every_verb()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, OnPrem());

            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                await GateDrive.AddUserAsync(app, Host, AdminUsername, AdminPassword, AdminEmail, RoleNames.Admin);

                using var client = GateDrive.NewClient(app, Host);
                var payload = issuer.Payload(InstallationId(app), ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30));
                var blob = issuer.Issue(payload);
                var lid = payload.Lid.ToString("D");
                var before = File.ReadAllBytes(OverridePath(scratch));

                // (4) Administrator: authenticated, not Host.
                var adminCookies = await GateDrive.LoginAsync(app, client, Host, AdminUsername, AdminPassword, _output);
                var adminId = await GateDrive.UserIdAsync(app, Host, AdminUsername);
                Assert.Contains(RoleNames.Admin, await GateDrive.RolesAsync(app, Host, adminId));
                Assert.DoesNotContain(RoleNames.Host, await GateDrive.RolesAsync(app, Host, adminId));

                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, adminCookies, null, HttpMethod.Get, ApiPath, null, "Admin GET")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, adminCookies, adminId, HttpMethod.Post, ApiPath, new { Key = blob }, "Admin POST")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, adminCookies, adminId, HttpMethod.Delete, $"{ApiPath}/{lid}", null, "Admin DELETE")).Status);

                // (5) anonymous: no cookie at all.
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, null, null, HttpMethod.Get, ApiPath, null, "anonymous GET")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, null, null, HttpMethod.Post, ApiPath, new { Key = blob }, "anonymous POST")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, null, null, HttpMethod.Delete, $"{ApiPath}/{lid}", null, "anonymous DELETE")).Status);

                // Nothing any of them sent was written.
                Assert.Equal(before, File.ReadAllBytes(OverridePath(scratch)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- (6) ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task With_the_mode_unset_the_Host_gets_404_on_every_verb()
        {
            using var issuer = new TestLicenceIssuer();
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);

            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var (cookies, hostId) = await LoginHostAsync(app, client);
                var payload = issuer.Payload(InstallationId(app), ent: new[] { Entitlement });
                var before = File.ReadAllBytes(OverridePath(scratch));

                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, client, cookies, null, HttpMethod.Get, ApiPath, null, "unset Host GET")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, client, cookies, hostId, HttpMethod.Post, ApiPath, new { Key = issuer.Issue(payload) }, "unset Host POST")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, client, cookies, hostId, HttpMethod.Delete, $"{ApiPath}/{payload.Lid:D}", null, "unset Host DELETE")).Status);

                // F-J: authorization still answers first - anonymous is 403, not 404, in this mode too.
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, client, null, null, HttpMethod.Get, ApiPath, null, "unset anonymous GET")).Status);

                Assert.Equal(before, File.ReadAllBytes(OverridePath(scratch)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- (7) ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_environment_supplied_key_reports_its_source_and_cannot_be_deleted_from_the_page()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);

            // Minted before the install, so it cannot name this site's (not yet minted) InstallationId; its reason is not
            // what this fact measures - its SOURCE is.
            var payload = issuer.Payload(Guid.NewGuid().ToString(), ent: new[] { Entitlement }, exp: DateTimeOffset.UtcNow.AddDays(30));
            var lid = payload.Lid.ToString("D");
            var variable = $"Licensing__Keys__{lid}";
            var previous = Environment.GetEnvironmentVariable(variable);

            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, OnPrem());
            Environment.SetEnvironmentVariable(variable, issuer.Issue(payload));
            WebApplication? app = null;
            try
            {
                app = await GateDrive.InstallAsync(scratch, _output);
                using var client = GateDrive.NewClient(app, Host);
                var (cookies, hostId) = await LoginHostAsync(app, client);
                var configKey = $"{OfflineLicenceEntitlementProvider.KeysSection}:{lid}";
                Assert.False(string.IsNullOrEmpty(app.Services.GetRequiredService<IConfigurationRoot>()[configKey]),
                    "Precondition: the environment variable did not reach the host's IConfigurationRoot.");

                var get = await SendAsync(app, client, cookies, null, HttpMethod.Get, ApiPath, null, "Host GET env key");
                Assert.Equal(HttpStatusCode.OK, get.Status);
                var key = FindKey(JsonNode.Parse(get.Body)!, lid);
                Assert.Equal("environment", Str(key, "Source"));
                Assert.False(Prop(key, "Deletable")!.GetValue<bool>());

                var delete = await SendAsync(app, client, cookies, hostId, HttpMethod.Delete, $"{ApiPath}/{lid}", null, "Host DELETE env key");
                Assert.Equal(HttpStatusCode.Conflict, delete.Status);
                Assert.Contains("environment", delete.Body, StringComparison.Ordinal);

                Assert.False(string.IsNullOrEmpty(app.Services.GetRequiredService<IConfigurationRoot>()[configKey]),
                    "The environment-supplied key disappeared after a refused DELETE.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
                if (app != null)
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private async Task<(System.Net.CookieContainer Cookies, int UserId)> LoginHostAsync(WebApplication app, HttpClient client)
        {
            var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
            return (cookies, await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername));
        }

        private static string InstallationId(WebApplication app)
        {
            var id = app.Services.GetRequiredService<IConfigManager>().GetInstallationId();
            Assert.False(string.IsNullOrWhiteSpace(id), "The installer minted no InstallationId, so no key can be bound to this site.");
            return id;
        }

        private static string OverridePath(ScratchGateInstall scratch) => Path.Combine(scratch.ContentRoot, OverrideFileName);

        /// <summary>
        /// One request the way a browser sends it: the caller's Identity cookie, and on an unsafe method a matched
        /// antiforgery cookie + header pair (the <see cref="GateDrive.PutDefinitionAsync"/> recipe). Logs status and body.
        /// </summary>
        private async Task<(HttpStatusCode Status, string Body)> SendAsync(
            WebApplication app, HttpClient client, System.Net.CookieContainer? cookies, int? userId, HttpMethod method, string path, object? body, string label)
        {
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(method, uri);
            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }

            var cookieHeader = cookies?.GetCookieHeader(uri) ?? "";
            if (method != HttpMethod.Get)
            {
                var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
                cookieHeader = string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie;
                request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            }

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"[{label}] {method} {path} -> {(int)response.StatusCode} {GateDrive.Truncate(text)}");
            if (cookies != null)
            {
                GateDrive.CaptureCookies(cookies, uri, response);
            }

            if ((int)response.StatusCode >= 500)
            {
                foreach (var error in GateDrive.DrainCapturedErrors())
                {
                    _output.WriteLine($"[{label}] host log: {error}");
                }
            }

            return (response.StatusCode, text);
        }

        private static string TamperSignature(string blob)
        {
            var segments = blob.Split('.');
            var signature = segments[2].ToCharArray();
            var middle = signature.Length / 2;
            signature[middle] = signature[middle] == 'A' ? 'B' : 'A';
            return segments[0] + "." + segments[1] + "." + new string(signature);
        }

        /// <summary>No response ever carries a key: neither the whole blob nor its signature segment.</summary>
        private static void AssertNoBlob(string body, string blob)
        {
            Assert.DoesNotContain(blob, body, StringComparison.Ordinal);
            Assert.DoesNotContain(blob.Split('.')[2], body, StringComparison.Ordinal);
        }

        private static JsonNode FindKey(JsonNode status, string lid)
        {
            var keys = Prop(status, "Keys")!.AsArray();
            var match = keys.FirstOrDefault(k => string.Equals(Str(k!, "Lid"), lid, StringComparison.OrdinalIgnoreCase));
            Assert.True(match != null, $"No key with lid {lid} in the status: {status.ToJsonString()}");
            return match!;
        }

        /// <summary>A property looked up case-insensitively, so the assertions do not depend on the host's JSON naming policy.</summary>
        private static JsonNode? Prop(JsonNode node, string name) =>
            node.AsObject().FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        private static string? Str(JsonNode node, string name) => Prop(node, name)?.GetValue<string>();
    }
}