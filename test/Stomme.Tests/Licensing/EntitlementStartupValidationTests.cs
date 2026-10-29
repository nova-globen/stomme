using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure.Licensing;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// The post-build validation of the entitlement wiring: a stray or replaced entitlement
    /// provider, conflicting feature descriptors and a non-canonical <c>RequiredEntitlement</c> each fail startup, in both
    /// directions, and the failure is NOT swallowed (it surfaces from <see cref="Program.BuildAppAsync"/>, before
    /// <c>FeatureSeeder.StartAsync</c>'s catch-all could see it).
    /// </summary>
    /// <remarks>
    /// TRAP: every <c>IFeatureContributor</c> with a public parameterless constructor in this assembly is discovered into
    /// EVERY host the suite composes. The planted contributors below are therefore <see cref="FeatureContributor"/>
    /// INSTANCES registered on one test's own builder, and the planted provider is a private nested class. The class sits in
    /// the non-parallel render-guard collection because its last facts set the process-wide appsettings override variable.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class EntitlementStartupValidationTests : IDisposable
    {
        private const string ConflictKey = "licensing-validation-test.conflict";
        private const string PremiumKey = "licensing-validation-test.premium";

        private readonly LicensingHostComposition _hosts = new();

        public void Dispose() => _hosts.Dispose();

        private static Dictionary<string, string?> Mode(string? mode) =>
            mode is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [DeploymentMode.ConfigKey] = mode };

        private void BuildAndValidate(string? mode, Action<IServiceCollection>? afterAddStomme = null)
        {
            var (builder, configuration) = _hosts.ComposeHostBuilder(Mode(mode));
            afterAddStomme?.Invoke(builder.Services);
            using var app = builder.Build();
            EntitlementStartupValidation.Validate(app.Services, configuration);
        }

        [Fact]
        public void A_stock_composition_passes_with_the_mode_absent()
        {
            BuildAndValidate(null);
        }

        [Fact]
        public void OnPrem_with_nothing_extra_passes()
        {
            BuildAndValidate("OnPrem");
        }

        [Fact]
        public void A_second_provider_with_the_mode_absent_fails()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => BuildAndValidate(null, services => services.AddScoped<IEntitlementProvider, PlantedProvider>()));

            Assert.Contains(nameof(IEntitlementProvider), ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PlantedProvider), ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_replaced_provider_under_OnPrem_fails_even_though_the_count_is_one()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BuildAndValidate("OnPrem", services =>
            {
                services.RemoveAll<IEntitlementProvider>();
                services.AddScoped<IEntitlementProvider, PlantedProvider>();
            }));

            Assert.Contains(nameof(OfflineLicenceEntitlementProvider), ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PlantedProvider), ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_contributors_declaring_one_key_with_different_entitlements_fail_naming_the_key_and_both_values()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BuildAndValidate(null, services =>
            {
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[] { new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.one") }));
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[] { new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.two") }));
            }));

            Assert.Contains(ConflictKey, ex.Message, StringComparison.Ordinal);
            Assert.Contains("novagloben.test.one", ex.Message, StringComparison.Ordinal);
            Assert.Contains("novagloben.test.two", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Byte_identical_duplicate_descriptors_are_tolerated()
        {
            BuildAndValidate(null, services =>
            {
                var descriptor = new FeatureDescriptor(ConflictKey, "same", RequiredEntitlement: "novagloben.test.same");
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[] { descriptor }));
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[] { descriptor with { } }));
            });
        }

        [Fact]
        public void A_non_canonical_required_entitlement_fails_naming_the_key_and_value()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BuildAndValidate(null, services =>
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[] { new FeatureDescriptor(PremiumKey, "premium", RequiredEntitlement: "Premium.Feature") }))));

            Assert.Contains(PremiumKey, ex.Message, StringComparison.Ordinal);
            Assert.Contains("Premium.Feature", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_violation_is_reported_in_one_exception()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BuildAndValidate(null, services =>
            {
                services.AddScoped<IEntitlementProvider, PlantedProvider>();
                services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[]
                {
                    new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.one"),
                    new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.two"),
                    new FeatureDescriptor(PremiumKey, "premium", RequiredEntitlement: "Premium.Feature"),
                }));
            }));

            Assert.Contains(nameof(PlantedProvider), ex.Message, StringComparison.Ordinal);
            Assert.Contains(ConflictKey, ex.Message, StringComparison.Ordinal);
            Assert.Contains("Premium.Feature", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// NOT SWALLOWED: the real <see cref="Program.BuildAppAsync"/> (which ends in <c>UseStommeAsync</c>) throws. Were
        /// the check in the lazily built <c>FeatureRegistry</c>, its first resolver would be <c>FeatureSeeder.StartAsync</c>,
        /// whose catch-all logs and continues — and BuildAppAsync would return a host here.
        /// </summary>
        [Fact]
        public async Task A_conflict_fails_Program_BuildAppAsync_itself()
        {
            var contentRoot = Path.Combine(Path.GetTempPath(), "stomme-entitlement-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(contentRoot, "appsettings.json"));
            var previousOverride = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, contentRoot);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.BuildAppAsync(
                    new[] { "--contentRoot", contentRoot, "--environment", "Development" },
                    builder =>
                    {
                        builder.WebHost.UseTestServer();
                        builder.Services.AddSingleton<IFeatureContributor>(new FeatureContributor(new[]
                        {
                            new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.one"),
                            new FeatureDescriptor(ConflictKey, "one", RequiredEntitlement: "novagloben.test.two"),
                        }));
                    }));

                Assert.Contains(ConflictKey, ex.Message, StringComparison.Ordinal);
                Assert.Contains("novagloben.test.two", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, previousOverride);
                try { Directory.Delete(contentRoot, true); } catch { /* best effort: a host we never got back may hold a file */ }
            }
        }

        private sealed class PlantedProvider : IEntitlementProvider
        {
            public Task<bool> IsEntitledAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
        }
    }
}