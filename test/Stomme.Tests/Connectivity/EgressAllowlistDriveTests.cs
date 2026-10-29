#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Controllers;
using Stomme.Infrastructure.Connectivity;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// Driven over HTTP on an installed host: which private hosts a site may reach is set on the Connections page by
    /// the Host and nobody else. A REST connection to a loopback server is refused by the egress; an Administrator reads
    /// the allowlist and is refused changing it; a range is refused; the Host allows the loopback address and the same
    /// connection's test reaches the server. And the settings API refuses the three egress allowlists to an Administrator
    /// through every door, while a settings page carrying them back unchanged still saves the rest.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class EgressAllowlistDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Connections";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task The_host_allows_a_private_host_on_the_connections_page_and_the_connection_then_reaches_it()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var site = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail, RoleNames.Admin);
                var adminId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                using var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);

                using var server = new LoopbackStatusServer();
                var created = Ok<ConnectionView>(await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, Api,
                    new ConnectionRequest { Name = "Local status", ConnectorType = "rest", Endpoint = $"http://127.0.0.1:{server.Port}/status", TimeoutSeconds = 5 }, output, "a REST connection to a loopback server"), "create");

                // Loopback is refused by the site's egress where the bytes would go.
                var denied = Ok<ConnectionTestView>(await Test(app, client, cookies, hostId, created.Id, "test before the allowlist"), "test before");
                Assert.Equal((false, ConnectorOutcome.DeniedByPolicy), (denied.Success, denied.Outcome));
                Assert.Equal(0, server.Requests);

                // An Administrator reads the allowlist - empty - and cannot change it.
                var read = await GateDrive.GetAsync(adminClient, adminCookies, Host, $"{Api}/egress", output, "the allowlist as an administrator");
                var adminView = Ok<ConnectionEgressView>((read.Status, read.Body), "the allowlist as an administrator");
                Assert.Equal((0, false), (adminView.AllowedHosts.Count, adminView.CanEdit));
                var refused = await GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, adminId, HttpMethod.Put, $"{Api}/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, output, "an administrator allows loopback");
                Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

                // A range would match nothing: refused, with the reason.
                var range = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"{Api}/egress", new ConnectionEgressRequest { AllowedHosts = "10.0.0.0/8" }, output, "a range");
                Assert.Equal(HttpStatusCode.BadRequest, range.Status);
                Assert.Contains("10.0.0.0/8", range.Body, StringComparison.Ordinal);

                // The Host allows the loopback address; the same connection now reaches the server.
                var saved = Ok<ConnectionEgressView>(await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"{Api}/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, output, "the Host allows loopback"), "save");
                Assert.Equal(new[] { "127.0.0.1" }, saved.AllowedHosts);
                Assert.True(saved.CanEdit);
                var admitted = Ok<ConnectionTestView>(await Test(app, client, cookies, hostId, created.Id, "test after the allowlist"), "test after");
                Assert.True(admitted.Success, admitted.Message);
                Assert.Equal(1, server.Requests);

                // What the egress reads is the site's PRIVATE row.
                var row = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, site, HostConnectorEgress.AllowedHostsSettingName));
                Assert.Equal(("127.0.0.1", true), (row?.SettingValue, row?.IsPrivate ?? false));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_administrator_cannot_widen_a_sites_egress_through_any_door_of_the_settings_API()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var site = (await GateDrive.AliasAsync(app, Host)).SiteId;
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail, RoleNames.Admin);
                var adminId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                using var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var failures = new List<string>();

                Task<(HttpStatusCode Status, string Body)> Admin(HttpMethod method, string path, object? body, string label) =>
                    GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, adminId, method, path, body, output, label);

                var index = 0;
                foreach (var reserved in SettingController.HostReservedSiteSettingNames)
                {
                    index++;
                    foreach (var name in new[] { reserved, reserved.ToUpperInvariant() })
                    {
                        if ((await Admin(HttpMethod.Post, "/api/Setting", new Setting { EntityName = EntityNames.Site, EntityId = site, SettingName = name, SettingValue = "10.0.0.9", IsPrivate = true }, $"POST {name}")).Status != HttpStatusCode.Forbidden) failures.Add($"POST {name} was not refused");
                        if ((await Admin(HttpMethod.Put, $"/api/Setting/Site/{site}/{name}", new Setting { SettingValue = "10.0.0.9", IsPrivate = true }, $"PUT by name {name}")).Status != HttpStatusCode.Forbidden) failures.Add($"PUT by name {name} was not refused");
                    }

                    if (await Row(app, site, reserved) is not null) failures.Add($"{reserved} was written by an administrator");

                    // The bulk save a settings page makes: the reserved row skipped, the ordinary one saved.
                    await Admin(HttpMethod.Put, $"/api/Setting/Site/{site}", new List<Setting>
                    {
                        new() { EntityName = EntityNames.Site, EntityId = site, SettingName = reserved, SettingValue = "10.0.0.9", IsPrivate = true },
                        new() { EntityName = EntityNames.Site, EntityId = site, SettingName = $"Bl180Ordinary{index}", SettingValue = "saved", IsPrivate = false },
                    }, $"bulk save with {reserved}");
                    if (await Row(app, site, reserved) is not null) failures.Add($"the bulk save wrote {reserved}");
                    if ((await Row(app, site, $"Bl180Ordinary{index}"))?.SettingValue != "saved") failures.Add($"the bulk save beside {reserved} lost the ordinary row");

                    // Set by the Host (non-vacuity: the Host still writes it, through the same door).
                    var hostWrite = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"/api/Setting/Site/{site}/{reserved}", new Setting { SettingValue = "10.0.0.1", IsPrivate = true }, output, $"the Host sets {reserved}");
                    var row = await Row(app, site, reserved);
                    if (hostWrite.Status != HttpStatusCode.OK || row?.SettingValue != "10.0.0.1")
                    {
                        failures.Add($"the Host could not set {reserved} ({(int)hostWrite.Status})");
                        continue;
                    }

                    // An Administrator's settings page carries it back UNCHANGED beside a real edit: the edit saves, the row stays.
                    var unchanged = await Admin(HttpMethod.Put, $"/api/Setting/Site/{site}", new List<Setting>
                    {
                        new() { EntityName = EntityNames.Site, EntityId = site, SettingName = reserved, SettingValue = "10.0.0.1", IsPrivate = true },
                        new() { EntityName = EntityNames.Site, EntityId = site, SettingName = $"Bl180Ordinary{index}", SettingValue = "edited", IsPrivate = false },
                    }, $"a settings page carrying {reserved} back");
                    if (unchanged.Status != HttpStatusCode.OK || (await Row(app, site, $"Bl180Ordinary{index}"))?.SettingValue != "edited") failures.Add($"a settings page carrying {reserved} back was refused ({(int)unchanged.Status})");

                    // Changed by id, deleted by name, deleted by id: refused, the Host's value intact.
                    var asLoaded = new Setting
                    {
                        SettingId = row.SettingId, EntityName = row.EntityName, EntityId = row.EntityId, SettingName = row.SettingName, SettingValue = "10.0.0.9",
                        IsPrivate = true, CreatedBy = row.CreatedBy, CreatedOn = row.CreatedOn, ModifiedBy = row.ModifiedBy, ModifiedOn = row.ModifiedOn,
                    };
                    if ((await Admin(HttpMethod.Put, $"/api/Setting/{row.SettingId}", asLoaded, $"PUT by id {reserved}")).Status != HttpStatusCode.Forbidden) failures.Add($"PUT by id {reserved} was not refused");
                    var renamed = new Setting
                    {
                        SettingId = row.SettingId, EntityName = row.EntityName, EntityId = row.EntityId, SettingName = "Bl180Innocent", SettingValue = "x",
                        IsPrivate = false, CreatedBy = row.CreatedBy, CreatedOn = row.CreatedOn, ModifiedBy = row.ModifiedBy, ModifiedOn = row.ModifiedOn,
                    };
                    if ((await Admin(HttpMethod.Put, $"/api/Setting/{row.SettingId}", renamed, $"PUT by id, renamed, {reserved}")).Status != HttpStatusCode.Forbidden) failures.Add($"renaming {reserved} by id was not refused");
                    if ((await Admin(HttpMethod.Delete, $"/api/Setting/Site/{site}/{reserved}", null, $"DELETE by name {reserved}")).Status != HttpStatusCode.Forbidden) failures.Add($"DELETE by name {reserved} was not refused");
                    if ((await Admin(HttpMethod.Delete, $"/api/Setting/{row.SettingId}/Site", null, $"DELETE by id {reserved}")).Status != HttpStatusCode.Forbidden) failures.Add($"DELETE by id {reserved} was not refused");
                    if ((await Row(app, site, reserved))?.SettingValue != "10.0.0.1") failures.Add($"the Host's {reserved} did not survive the administrator");
                }

                Assert.True(failures.Count == 0, string.Join("; ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// The gate: every egress allowlist the server declares (a constant named <c>AllowedHostsSettingName</c>) is reserved
        /// for the Host by the settings API - a fourth one added without it fails the build rather than opening a door.
        /// </summary>
        [Fact]
        public void Every_egress_allowlist_the_server_declares_is_the_hosts_to_set()
        {
            var declared = typeof(SettingController).Assembly.GetTypes()
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name == "AllowedHostsSettingName")
                .Select(field => (string)field.GetRawConstantValue()!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(declared.Count >= 3, $"only {declared.Count} allowlist setting(s) found - the scan proves nothing");
            Assert.Empty(declared.Except(SettingController.HostReservedSiteSettingNames, StringComparer.OrdinalIgnoreCase));
        }

        private static Task<Setting?> Row(WebApplication app, int site, string name) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (Setting?)await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, site, name));

        private Task<(HttpStatusCode Status, string Body)> Test(WebApplication app, HttpClient client, CookieContainer cookies, int userId, Guid id, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, $"{Api}/{id}/test", null, output, label);

        private static T Ok<T>((HttpStatusCode Status, string Body) answer, string label)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        /// <summary>A loopback HTTP server answering 200 to every request, counting them.</summary>
        private sealed class LoopbackStatusServer : IDisposable
        {
            private readonly HttpListener _listener;
            private int _requests;

            public LoopbackStatusServer()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public int Requests => Volatile.Read(ref _requests);

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _requests);
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
