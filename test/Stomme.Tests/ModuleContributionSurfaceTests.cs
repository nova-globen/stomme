using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Two contribution contracts an EXTERNAL module could not use, both found by the exemplar
    /// (<c>examples/Modules/KnowledgeBase</c>) and fixed at the framework: the feature-flag contributor, which lived in a
    /// package that carries EF Core (the slim SDK leaves it out), and the sitemap contract, which was synchronous.
    /// The exemplar's own drive proves both from a packed module; these keep the framework's half from sliding back.
    /// </summary>
    public sealed class ModuleContributionSurfaceTests
    {
        [Fact]
        public void The_feature_contributor_contract_lives_in_the_dependency_free_security_abstractions()
        {
            var home = typeof(IFeatureContributor).Assembly;
            Assert.Equal("Stomme.Security.Abstractions", home.GetName().Name);
            Assert.Same(home, typeof(FeatureDescriptor).Assembly);
            Assert.Same(home, typeof(FeatureContributor).Assembly);

            // The slim SDK's promise: what a module compiles against for a flag drags no framework package and no EF Core.
            Assert.All(home.GetReferencedAssemblies(), reference =>
                Assert.False(reference.Name!.StartsWith("Stomme", StringComparison.Ordinal) || reference.Name.Contains("EntityFramework", StringComparison.Ordinal),
                    $"Stomme.Security.Abstractions references {reference.Name}."));
        }

        [Theory]
        [InlineData("Stomme.FeatureManagement.Application.FeatureDescriptor")]
        [InlineData("Stomme.FeatureManagement.Application.IFeatureContributor")]
        [InlineData("Stomme.FeatureManagement.Application.FeatureContributor")]
        public void A_module_compiled_against_the_old_home_still_binds(string typeName)
        {
            // A package built before the move names Stomme.FeatureManagement; the assembly's forward must answer it.
            var bound = Type.GetType(typeName + ", Stomme.FeatureManagement", throwOnError: true)!;
            Assert.Equal("Stomme.Security.Abstractions", bound.Assembly.GetName().Name);
        }

        [Fact]
        public void A_module_startup_reads_the_hosts_layered_configuration_off_the_collection()
        {
            // A module decides at COMPOSITION whether to register its demo content or its MCP contribution,
            // and must read the layered root (the override file's switches), not the builder's default configuration.
            var services = new ServiceCollection();
            var builderDefault = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DemoContent:Enabled"] = "false" }).Build();
            var layered = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DemoContent:Enabled"] = "true" }).Build();

            Assert.Null(HostConfiguration.Resolve(services));
            services.AddSingleton<IConfiguration>(builderDefault);
            Assert.Same(builderDefault, HostConfiguration.Resolve(services));
            services.AddSingleton<IConfigurationRoot>(layered);
            Assert.Same(layered, HostConfiguration.Resolve(services));
            Assert.Equal("true", HostConfiguration.Resolve(services)!["DemoContent:Enabled"]);
        }

        [Fact]
        public void Every_sitemap_contract_member_is_asynchronous() =>
            Assert.All(typeof(ISitemap).GetMethods(), method =>
                Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType), $"ISitemap.{method.Name} returns {method.ReturnType.Name}: a module reading a store could only block on it (D33)."));
    }

    /// <summary>
    /// The sitemap page, driven: nothing in the tree covered <c>/sitemap.xml</c>, and the page's module
    /// loop changed with the contract. A fresh SQLite install serves the pages a visitor may view and none an administrator
    /// alone may. A module's own entries are driven by the exemplar, whose manager is the first implementation.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SitemapDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly XNamespace Sitemaps = "http://www.sitemaps.org/schemas/sitemap/0.9";

        [Fact]
        public async Task The_sitemap_lists_the_pages_a_visitor_may_view_and_no_administration_page()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var response = await client.GetAsync("/sitemap.xml");
                var body = await response.Content.ReadAsStringAsync();
                output.WriteLine($"GET /sitemap.xml -> {(int)response.StatusCode}\n{body}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var locations = XDocument.Parse(body).Descendants(Sitemaps + "loc").Select(element => element.Value).ToList();
                Assert.Contains("http://" + Host + "/", locations);
                Assert.DoesNotContain(locations, location => location.Contains("/admin", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
