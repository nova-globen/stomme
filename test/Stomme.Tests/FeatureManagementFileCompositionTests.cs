#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Feature flags on the FILE store. The module chose its flag store by "relational or not", and the "not"
    /// branch composed <c>RavenRepository&lt;,&gt;</c> - so with <c>DefaultDBType</c> naming the File store, the first flag
    /// evaluation threw (measured: "Unable to resolve service for type IDocumentStore"). The two aggregates now take the
    /// storage-blind File path every other module takes.
    /// </summary>
    /// <remarks>
    /// Composed through the module's own <c>ServerStartup</c>, not an install: the File store is a module-aggregate
    /// provider with no framework-store twin and no wizard entry (<c>FileDatabase</c>'s own remarks), so there is no File
    /// INSTALL to drive - the composition is what a File-configured host would run.
    /// </remarks>
    public sealed class FeatureManagementFileCompositionTests : IDisposable
    {
        private const string FileType = "Stomme.Database.File.FileDatabase, Stomme.Server";

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-features-file-{Guid.NewGuid():N}");

        [Fact]
        public async Task Flags_evaluate_toggle_and_persist_on_the_File_store()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddHttpContextAccessor();   // the host always has one; the module's context provider reads it
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = FileType,
                ["ModuleStore:FilePath"] = _root,
            }).Build());
            new Stomme.Modules.FeatureManagement.Startup.ServerStartup().ConfigureServices(services);

            await using var provider = services.BuildServiceProvider();
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(default);
            }

            await using (var scope = provider.CreateAsyncScope())
            {
                // The store is the File one - for BOTH aggregates - not a Raven repository with no Raven store behind it.
                Assert.Equal("FileRepository`2", scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>().GetType().Name);
                Assert.Equal("FileRepository`2", scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlagAudit, Guid>>().GetType().Name);

                Assert.False(await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync("tests.file.flag"));
                await scope.ServiceProvider.GetRequiredService<IFeatureAdminService>().ToggleAsync("tests.file.flag", true);
            }

            // A FRESH scope reads it back from the store.
            await using (var scope = provider.CreateAsyncScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync("tests.file.flag"));
            }

            Assert.NotEmpty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }
    }
}
