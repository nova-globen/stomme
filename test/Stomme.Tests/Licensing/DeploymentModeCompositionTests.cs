using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure.Licensing;
using Stomme.Infrastructure.SiteTemplates;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// The composition half of the deployment-mode gate, over the REAL <c>AddStomme</c>. The refutation to rule out: no hook composes AddStomme
    /// WITHOUT the licensing call, so "equal to a composition without licensing" is measured as: absent == <c>Hosted</c>
    /// descriptor-for-descriptor, with ZERO licensing descriptors in either, and <c>OnPrem</c> == <c>Hosted</c> once the
    /// licensing descriptors are removed. The admin page template half is <see cref="The_admin_site_template_offers_admin_licensing_only_under_OnPrem"/>; the <c>/api/Licence</c> 404 half is driven\n    /// over real HTTP by <c>LicenceControllerAuthorizationTests.With_the_mode_unset_the_Host_gets_404_on_every_verb</c> (as the Host - F-J).
    /// </summary>
    public sealed class DeploymentModeCompositionTests : IDisposable
    {
        private readonly LicensingHostComposition _hosts = new();

        public void Dispose() => _hosts.Dispose();

        [Fact]
        public void Absent_and_Hosted_compose_identical_descriptor_lists_with_nothing_licensing_related()
        {
            // Warm-up: the first composition in a process can load assemblies the scan then sees; compose once so the two
            // measured compositions start from the same loaded set.
            _hosts.ComposeHostBuilder(new Dictionary<string, string?>());

            var absent = _hosts.ComposeHostBuilder(new Dictionary<string, string?>()).Builder.Services.ToList();
            var hosted = _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [DeploymentMode.ConfigKey] = "Hosted" }).Builder.Services.ToList();

            Assert.NotEmpty(absent);
            Assert.Equal(LicensingHostComposition.Signature(absent), LicensingHostComposition.Signature(hosted));
            Assert.DoesNotContain(absent, LicensingHostComposition.IsLicensingDescriptor);
            Assert.DoesNotContain(hosted, LicensingHostComposition.IsLicensingDescriptor);
        }

        [Fact]
        public void OnPrem_composes_exactly_one_scoped_offline_entitlement_provider_and_nothing_else_changes()
        {
            _hosts.ComposeHostBuilder(new Dictionary<string, string?>());

            var hosted = _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [DeploymentMode.ConfigKey] = "Hosted" }).Builder.Services.ToList();
            var onPrem = _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [DeploymentMode.ConfigKey] = "OnPrem" }).Builder.Services.ToList();

            var provider = Assert.Single(onPrem, d => d.ServiceType == typeof(IEntitlementProvider));
            Assert.Equal(typeof(OfflineLicenceEntitlementProvider), provider.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, provider.Lifetime);

            var state = Assert.Single(onPrem, d => d.ServiceType == typeof(LicenceState));
            Assert.Equal(ServiceLifetime.Singleton, state.Lifetime);

            // OnPrem adds exactly those two descriptors and changes nothing else, in order. (A first implementation's
            // TryAddSingleton(TimeProvider.System) failed THIS line: the host TryAdds a TimeProvider later, so OnPrem moved it.)
            Assert.Equal(2, onPrem.Count(LicensingHostComposition.IsLicensingDescriptor));
            var remainder = onPrem.Where(d => !LicensingHostComposition.IsLicensingDescriptor(d)).ToList();
            Assert.Equal(LicensingHostComposition.Signature(hosted), LicensingHostComposition.Signature(remainder));
        }

        [Fact]
        public void An_unrecognised_mode_fails_composition_naming_the_key()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [DeploymentMode.ConfigKey] = "OnPremise" }));

            Assert.Contains("Deployment:Mode", ex.Message, StringComparison.Ordinal);
            Assert.Contains("OnPremise", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_admin_site_template_offers_admin_licensing_only_under_OnPrem()
        {
            foreach (var (mode, expected) in new[] { ((string?)null, 0), ("Hosted", 0), ("OnPrem", 1) })
            {
                var settings = new Dictionary<string, string?>();
                if (mode != null)
                {
                    settings[DeploymentMode.ConfigKey] = mode;
                }

                var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
                var pages = await new AdminSiteTemplate(new EchoLocalizer(), configuration).CreateSiteAsync(new Site());

                Assert.True(pages.Count > 10, $"mode {mode ?? "(absent)"}: the template produced {pages.Count} pages - not the admin template.");
                var licensing = pages.Where(p => p.Path == "admin/licensing").ToList();
                Assert.True(expected == licensing.Count, $"mode {mode ?? "(absent)"}: expected {expected} 'admin/licensing' page(s), found {licensing.Count}.");

                if (expected == 1)
                {
                    var page = licensing[0];
                    Assert.Equal("Admin", page.Parent);
                    Assert.Contains(page.PermissionList, p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Host && p.IsAuthorized);
                    Assert.Contains(page.PermissionList, p => p.PermissionName == PermissionNames.Edit && p.RoleName == RoleNames.Host && p.IsAuthorized);
                    Assert.All(page.PermissionList, p => Assert.Equal(RoleNames.Host, p.RoleName));
                    var module = Assert.Single(page.PageTemplateModules);
                    Assert.StartsWith("Stomme.Modules.Admin.Licensing,", module.ModuleDefinitionName, StringComparison.Ordinal);
                    Assert.All(module.PermissionList, p => Assert.Equal(RoleNames.Host, p.RoleName));
                }
            }
        }

        private sealed class EchoLocalizer : IStringLocalizer<AdminSiteTemplate>
        {
            public LocalizedString this[string name] => new(name, name);

            public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }
    }
}