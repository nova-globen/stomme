using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Stomme.Tests.Fixtures.PaidFeatureTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// Driven over real HTTP on an installed SQLite site: a paid feature is a
    /// <see cref="FeatureDescriptor"/> with a required entitlement, a <c>[Requires(Feature)]</c> on the mediator REQUEST, and
    /// a controller that merely dispatches it (<see cref="PaidFeatureTestController"/>). The paywall is the mediator's
    /// <c>AuthorizationBehavior</c> -&gt; <c>PermissionRowAccessGuard</c> -&gt; <c>FeatureEvaluatorBridge</c> -&gt;
    /// <c>FeatureEvaluator</c> -&gt; <see cref="OfflineLicenceEntitlementProvider"/>, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every step asserts the status AND the handler-invocation delta, so a 403 that still ran the handler (or a 200 that
    /// did not) cannot pass.
    /// </para>
    /// <para>
    /// <see cref="LicenceKeyRing.OverrideForTests"/> is process-wide and refuses to nest. This class shares the
    /// render-guard collection, which is declared <c>DisableParallelization = true</c> and therefore never runs beside
    /// the <c>LicenceRing</c> collection (<see cref="OfflineEntitlementProviderTests"/>), the only other user of the override.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RequiresFeatureEntitlementBridgeTests
    {
        private const string Host = "localhost";
        private const string RunPath = "/api/PaidFeatureTest/run";

        private readonly ITestOutputHelper _output;

        public RequiresFeatureEntitlementBridgeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task OnPrem_denies_the_paid_feature_without_a_key_grants_it_on_a_live_written_key_and_denies_it_after_expiry()
        {
            using var issuer = new TestLicenceIssuer();
            using var ring = LicenceKeyRing.OverrideForTests(issuer.Ring);
            var clock = new MutableTime(DateTimeOffset.UtcNow);

            using var scratch = new ScratchGateInstall(
                new SqliteGateStore(),
                Host,
                new JsonObject { ["Deployment"] = new JsonObject { ["Mode"] = "OnPrem" } });

            var app = await GateDrive.InstallAsync(scratch, _output, builder => builder.Services.AddSingleton<TimeProvider>(clock));
            try
            {
                // Precondition: the composed host IS the OnPrem host - the one provider, ours, on our clock. Without this a
                // 403 below could be FeatureManagement being absent (the bridge fails closed) rather than the licence gate.
                using (var scope = app.Services.CreateScope())
                {
                    var providers = scope.ServiceProvider.GetServices<IEntitlementProvider>().ToList();
                    Assert.IsType<OfflineLicenceEntitlementProvider>(Assert.Single(providers));
                    Assert.Same(clock, scope.ServiceProvider.GetRequiredService<TimeProvider>());
                    Assert.NotNull(scope.ServiceProvider.GetRequiredService<IFeatureRegistry>().Find(PaidFeatureTestFeatures.Key));
                }

                using var client = GateDrive.NewClient(app, Host);

                // ---- (i) no key -> 403 ProblemDetails, handler did not run ------------------------------------------
                await AssertDeniedAsync(client, "OnPrem, no key");

                // ---- (ii) a Valid key written LIVE through IConfigManager -> 200, handler ran, no restart -------------
                var configManager = app.Services.GetRequiredService<IConfigManager>();
                var installationId = configManager.GetInstallationId();
                _output.WriteLine($"[OnPrem] InstallationId = {installationId}");
                Assert.False(string.IsNullOrWhiteSpace(installationId), "The installer minted no InstallationId, so no key can be bound to this site.");

                var expires = clock.Now.AddDays(30);
                var payload = issuer.Payload(
                    installationId,
                    ent: new[] { PaidFeatureTestFeatures.Entitlement },
                    nbf: clock.Now.AddMinutes(-5),
                    exp: expires);
                var configKey = $"{OfflineLicenceEntitlementProvider.KeysSection}:{payload.Lid}";
                configManager.AddOrUpdateSetting(configKey, issuer.Issue(payload), true);

                // AddOrUpdateSetting swallows its own errors and returns void: prove the write reached the live root.
                Assert.False(string.IsNullOrEmpty(app.Services.GetRequiredService<IConfigurationRoot>()[configKey]),
                    $"'{configKey}' is not visible in the host's IConfigurationRoot after AddOrUpdateSetting(reload: true).");

                await AssertAllowedAsync(client, "OnPrem, live-written key");

                // ---- (iii) the clock passes exp -> 403 again, handler did not run ------------------------------------
                clock.Now = expires.AddSeconds(1);
                await AssertDeniedAsync(client, "OnPrem, key expired");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_mode_unset_the_paid_feature_is_granted_with_no_key()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using (var scope = app.Services.CreateScope())
                {
                    Assert.Empty(scope.ServiceProvider.GetServices<IEntitlementProvider>());
                    Assert.Empty(app.Services.GetRequiredService<IConfigurationRoot>().GetSection(OfflineLicenceEntitlementProvider.KeysSection).GetChildren());
                }

                using var client = GateDrive.NewClient(app, Host);

                // ---- (iv) no provider -> the entitlement leg is permissive, exactly as a stock install -----------------
                await AssertAllowedAsync(client, "mode unset, no key");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task AssertDeniedAsync(System.Net.Http.HttpClient client, string label)
        {
            var before = PaidFeatureTestHandler.Invocations;
            var response = await GateDrive.GetAsync(client, null, Host, RunPath, _output, label);
            var delta = PaidFeatureTestHandler.Invocations - before;
            _output.WriteLine($"[{label}] handler delta = {delta}");
            DumpErrorsOnUnexpected(response.Status, HttpStatusCode.Forbidden, label);

            Assert.Equal(HttpStatusCode.Forbidden, response.Status);
            Assert.Equal("application/problem+json", response.ContentType);
            Assert.DoesNotContain(PaidFeatureTestHandler.Marker, response.Body);
            Assert.Equal(0, delta);
        }

        private async Task AssertAllowedAsync(System.Net.Http.HttpClient client, string label)
        {
            var before = PaidFeatureTestHandler.Invocations;
            var response = await GateDrive.GetAsync(client, null, Host, RunPath, _output, label);
            var delta = PaidFeatureTestHandler.Invocations - before;
            _output.WriteLine($"[{label}] handler delta = {delta}");
            DumpErrorsOnUnexpected(response.Status, HttpStatusCode.OK, label);

            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Contains(PaidFeatureTestHandler.Marker, response.Body);
            Assert.Equal(1, delta);
        }

        private void DumpErrorsOnUnexpected(HttpStatusCode actual, HttpStatusCode expected, string label)
        {
            if (actual == expected)
            {
                return;
            }

            foreach (var error in GateDrive.DrainCapturedErrors())
            {
                _output.WriteLine($"[{label}] host log: {error}");
            }
        }
    }
}