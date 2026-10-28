#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Whether COMPOSITION-time code can reach the host's own content root (BL-153, the remaining readers).
    /// </summary>
    /// <remarks>
    /// The DI-capable readers of the <c>|DataDirectory|</c> token now ask <c>IConfigManager.GetDataDirectory</c>,
    /// which answers from the injected <see cref="IWebHostEnvironment"/>. Four readers are NOT DI-capable — they
    /// run while the container is still being composed (<c>ModuleAggregatePersistenceExtensions</c>,
    /// <c>ModuleRepositoryProviderRegistry</c>, FeatureManagement's <c>ServerStartup</c>, and the tenant
    /// connection resolver's static helper) and so still read the process-global slot.
    /// <para>
    /// Converting them depends on a claim worth checking rather than assuming: that an
    /// <see cref="IWebHostEnvironment"/> is present in the <see cref="IServiceCollection"/> AS AN INSTANCE at that
    /// point, the way <c>ResolveConfiguredDatabaseType</c> already pulls <c>IConfigurationRoot</c> off the
    /// descriptor list. If it is, the conversion is mechanical; if it is not, those four need the value threaded
    /// in from the caller instead, which is a different change. This measures which.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class CompositionSeamSeesHostEnvironmentTests
    {
        private readonly ITestOutputHelper _output;

        public CompositionSeamSeesHostEnvironmentTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_service_collection_carries_the_host_environment_while_composing()
        {
            var scratch = Path.Combine(Path.GetTempPath(), "stomme-compseam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(scratch, "wwwroot"));

            // A content root is not usable without the shipped settings file - Program's configuration builder
            // takes it as non-optional. Every other scratch fixture copies it in; so does this one.
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                Path.Combine(scratch, "appsettings.json"));

            string? descriptorKind = null;
            string? contentRootFromInstance = null;

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();

                    var descriptor = builder.Services
                        .LastOrDefault(d => d.ServiceType == typeof(IWebHostEnvironment));

                    descriptorKind = descriptor is null
                        ? "(absent)"
                        : descriptor.ImplementationInstance is not null ? "instance"
                        : descriptor.ImplementationFactory is not null ? "factory"
                        : descriptor.ImplementationType is not null ? "type"
                        : "(empty)";

                    contentRootFromInstance =
                        (descriptor?.ImplementationInstance as IWebHostEnvironment)?.ContentRootPath;
                });

            try
            {
                _output.WriteLine($"[descriptor] IWebHostEnvironment -> {descriptorKind}");
                _output.WriteLine($"[content root] from instance: {contentRootFromInstance ?? "(null)"}");
                _output.WriteLine($"[content root] the host's own: {app.Environment.ContentRootPath}");

                // Whatever the answer, it is recorded. The assertion is only that the host itself knows its
                // content root, which is the value the conversion needs to reach one way or another.
                Assert.Equal(
                    Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(app.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar));
            }
            finally
            {
                try { await app.DisposeAsync(); } catch { /* best effort */ }
                ScratchSqlite.ReleaseInstall(scratch);
                try { Directory.Delete(scratch, true); } catch { /* best effort */ }
            }
        }
    }
}
