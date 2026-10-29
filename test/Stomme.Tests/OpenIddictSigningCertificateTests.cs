using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// PERSISTENT OPENIDDICT SIGNING CERTIFICATE, DRIVEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authority signs tokens with a SHARED EPHEMERAL key — regenerated every process — so the
    /// JWKS <c>kid</c> and every issued token change on each restart, which breaks a real deployment. The
    /// authority can load a signing certificate from configuration (<c>OpenIddict:SigningCertificatePath</c>)
    /// and fall back to the ephemeral key when none is configured.
    /// </para>
    /// <para>
    /// This drive stands up the REAL Stomme host (<see cref="Program.BuildAppAsync"/> + the real pipeline,
    /// only the transport swapped for TestServer), installs ONE SQLite tenant, then BOOTS THE HOST FOUR TIMES
    /// against that same install and reads the live JWKS <c>kid</c> off the authority's own discovery document
    /// each time:
    /// </para>
    /// <list type="bullet">
    /// <item>two boots configured with the SAME persistent signing certificate — the <c>kid</c> is IDENTICAL
    /// (the mechanism under test: a persisted key survives a restart);</item>
    /// <item>two boots with NO certificate — the ephemeral <c>kid</c> ROTATES between them (proves the control
    /// is genuinely ephemeral and that the fallback path is unchanged);</item>
    /// <item>the persistent <c>kid</c> differs from the ephemeral one (the cert is actually in use, not a
    /// coincidental match).</item>
    /// </list>
    /// <para>
    /// SQLite is sufficient cover here: the signing key lives in the OpenIddict server options, ABOVE the
    /// database, so it is provider-independent (same rationale as the per-tenant authority tests). The install
    /// exists only to get the pipeline past tenant resolution so the well-known endpoints serve.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class OpenIddictSigningCertificateTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public OpenIddictSigningCertificateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Persistent_signing_certificate_keeps_jwks_kid_stable_while_ephemeral_rotates()
        {
            using var scratch = new ScratchInstallation();

            var certPath = Path.Combine(scratch.ContentRoot, "openiddict-signing.pfx");
            const string certPassword = "St0mme!Signing$2026";
            WriteSelfSignedPfx(certPath, certPassword);

            // First boot installs the SQLite tenant; the three later boots reuse the same install + DB files.
            var kidCert1 = await BootAndReadKidAsync(scratch, certPath, certPassword, install: true);
            var kidCert2 = await BootAndReadKidAsync(scratch, certPath, certPassword, install: false);
            var kidEphemeral1 = await BootAndReadKidAsync(scratch, certPath: null, certPassword: null, install: false);
            var kidEphemeral2 = await BootAndReadKidAsync(scratch, certPath: null, certPassword: null, install: false);

            _output.WriteLine($"[cert]      boot 1 kid = {kidCert1}");
            _output.WriteLine($"[cert]      boot 2 kid = {kidCert2}");
            _output.WriteLine($"[ephemeral] boot 1 kid = {kidEphemeral1}");
            _output.WriteLine($"[ephemeral] boot 2 kid = {kidEphemeral2}");

            Assert.False(string.IsNullOrEmpty(kidCert1), "The authority served no signing key (kid) on the first cert boot.");

            // The whole point: a persisted cert yields a STABLE kid across process restarts.
            Assert.Equal(kidCert1, kidCert2);

            // Control: the ephemeral fallback is genuinely ephemeral — a fresh key (and kid) every process.
            Assert.NotEqual(kidEphemeral1, kidEphemeral2);

            // And the cert is actually the key in use, not coincidentally equal to an ephemeral one.
            Assert.NotEqual(kidCert1, kidEphemeral1);
        }

        /// <summary>
        /// Builds the host with (optionally) a persistent signing cert, installs on the first call, starts the
        /// pipeline, and returns the <c>kid</c> of the first key the live JWKS endpoint advertises.
        /// </summary>
        private async Task<string> BootAndReadKidAsync(ScratchInstallation scratch, string? certPath, string? certPassword, bool install)
        {
            scratch.WriteOverrides(certPath, certPassword);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                if (install)
                {
                    var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
                    await InstallAsync(databaseManager, scratch.TenantConnectionString);
                }

                await app.StartAsync();

                using var client = app.GetTestClient();
                return await ReadJwksKidAsync(client);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private async Task InstallAsync(IDatabaseManager databaseManager, string connectionString)
        {
            var config = new InstallConfig
            {
                DatabaseType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                ConnectionString = connectionString,
                Aliases = Host,
                TenantName = TenantNames.Master,
                IsNewTenant = true,
                SiteName = Constants.DefaultSite,
                HostUsername = "host",
                HostPassword = "R3nder!Guard$2026",
                HostEmail = "host@example.com",
                HostName = "Host",
                SiteTemplate = Constants.DefaultSiteTemplate,
                RenderMode = "",
                Runtime = ""
            };

            var installTask = Task.Run(() => databaseManager.InstallAsync(config));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"Install did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
            }

            var install = await installTask;
            _output.WriteLine($"[install] Success={install.Success} Message={install.Message}");
            Assert.True(install.Success, $"Install failed: {install.Message}");
        }

        /// <summary>
        /// Reads the authority's own OIDC discovery document to find its <c>jwks_uri</c>, fetches the JWKS, and
        /// returns the first key's <c>kid</c> — exactly the hop an OIDC consumer performs to learn the signing
        /// key. Going through discovery (rather than assuming the default path) also proves the well-known
        /// endpoints serve on the installed host.
        /// </summary>
        private static async Task<string> ReadJwksKidAsync(HttpClient client)
        {
            var discoveryResponse = await client.GetAsync($"http://{Host}/.well-known/openid-configuration");
            discoveryResponse.EnsureSuccessStatusCode();
            var discovery = JsonNode.Parse(await discoveryResponse.Content.ReadAsStringAsync());

            var jwksUri = discovery?["jwks_uri"]?.GetValue<string>();
            if (string.IsNullOrEmpty(jwksUri))
            {
                throw new InvalidOperationException("The discovery document exposes no jwks_uri.");
            }

            var jwksResponse = await client.GetAsync(jwksUri);
            jwksResponse.EnsureSuccessStatusCode();
            var jwks = JsonNode.Parse(await jwksResponse.Content.ReadAsStringAsync());

            return jwks?["keys"]?[0]?["kid"]?.GetValue<string>()!;
        }

        private static void WriteSelfSignedPfx(string path, string password)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Stomme OpenIddict Signing", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        /// <summary>
        /// A throwaway content root that persists ONE SQLite tenant DB across the four boots and carries the
        /// OpenIddict override (Enabled + the signing cert path for the persistent boots, or Enabled alone for
        /// the ephemeral control). Mirrors the ScratchOpenIddictInstallation pattern in the per-tenant test.
        /// </summary>
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-oidc-cert-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                TenantConnectionString = $"Data Source={Path.Combine(ContentRoot, "tenant.db")};";

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public string TenantConnectionString { get; }

            /// <summary>Rewrites appsettings.override.json for the next boot, enabling OpenIddict and pointing
            /// (or not) at the persistent signing cert.</summary>
            public void WriteOverrides(string? certPath, string? certPassword)
            {
                var openIddict = new JsonObject { ["Enabled"] = true };
                if (!string.IsNullOrEmpty(certPath))
                {
                    openIddict["SigningCertificatePath"] = certPath;
                    openIddict["SigningCertificatePassword"] = certPassword;
                }

                var overrides = new JsonObject { ["OpenIddict"] = openIddict };

                File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
