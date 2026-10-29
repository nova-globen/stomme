using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Server
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var app = await BuildAppAsync(args);

            var install = await InstallAsync(app);
            if (!string.IsNullOrEmpty(install.Message))
            {
                var filelogger = app.Services.GetRequiredService<ILogger<Program>>();
                if (filelogger != null)
                {
                    filelogger.LogError($"[Stomme.Server.Program.Main] {install.Message}");
                }
            }
            else
            {
                await app.RunAsync();
            }
        }

        /// <summary>
        /// Builds the fully-configured host — DI composition and the middleware pipeline — WITHOUT running it
        /// or installing the database.
        /// </summary>
        /// <param name="args">Command line arguments, as passed to <see cref="Main"/>.</param>
        /// <param name="configureBuilder">
        /// Optional hook invoked after the builder is created and before <c>AddStomme</c> composes the
        /// container. Production passes nothing; tests use it to swap in the TestServer transport and to point
        /// the content root at a throwaway directory.
        /// </param>
        /// <remarks>
        /// Split out of <see cref="Main"/> so the host can be built and driven in-process (build, then run)
        /// rather than only as a process entry point. This is what lets the boot/install/render guard in
        /// Stomme.Tests stand up the REAL pipeline — the same DI composition, the same middleware order and
        /// the same <c>MapRazorComponents&lt;App&gt;</c> endpoint that serves production — and assert that an
        /// installed site actually renders. Main's behaviour is unchanged: build, install, run.
        /// </remarks>
        public static async Task<WebApplication> BuildAppAsync(string[] args, Action<WebApplicationBuilder> configureBuilder = null)
        {
            var builder = WebApplication.CreateBuilder(args);

            configureBuilder?.Invoke(builder);
            EnsureWebRoot(builder.Environment);

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(builder.Environment.ContentRootPath, "Data"));
            var overrideSettings = ConfigManager.GetAppSettingsOverrideFilename();
            var configurationBuilder = new ConfigurationBuilder()
                .SetBasePath(builder.Environment.ContentRootPath)
                .AddJsonFile("appsettings.json", false, true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true, true)
                // An optional dedicated feature-flag file. Its "FeatureManagement" section (Microsoft's
                // default section name) feeds the config-backed feature source, so bundled modules can be
                // enabled/disabled by config without touching appsettings. Reloads on change; env vars still
                // win over it.
                .AddJsonFile("features.json", true, true)
                .AddEnvironmentVariables();
            if (!string.IsNullOrEmpty(overrideSettings))
            {
                configurationBuilder.AddJsonFile(overrideSettings, true, true);
                // The web builder's own configuration gets the override file too, last, as in the root above. It is
                // what logging, the container's IConfiguration and any library reading builder.Configuration see, and
                // without it a value the installation keeps there - Logging:LogLevel, a claim type - was ignored by them.
                builder.Configuration.AddJsonFile(overrideSettings, true, true);
            }
            var configuration = configurationBuilder.Build();

            // A tenant Installation:Sites creates resolves its database by the name ConnectionStrings:<Tenant>. The entry's own
            // ConnectionString supplies it, at the LOWEST precedence, so a container recreated from its image (whose files the
            // install wrote to are gone) still finds it in its environment - measured: without this the host refused to start.
            var siteConnectionStrings = ConfiguredSites.ConnectionStrings(configuration);
            if (siteConnectionStrings.Count > 0)
            {
                (configuration as IDisposable)?.Dispose();
                configurationBuilder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = siteConnectionStrings });
                configuration = configurationBuilder.Build();
            }

            builder.Services.AddStomme(configuration, builder.Environment);

            var app = builder.Build();

            var corsService = app.Services.GetRequiredService<ICorsService>();
            var corsPolicyProvider = app.Services.GetRequiredService<ICorsPolicyProvider>();
            var syncManager = app.Services.GetRequiredService<ISyncManager>();

            await app.UseStommeAsync(configuration, builder.Environment, corsService, corsPolicyProvider, syncManager);

            return app;
        }

        /// <summary>
        /// Gives the host a web root when its content root has none. The framework writes and probes files under
        /// <see cref="IWebHostEnvironment.WebRootPath"/> - uploads, installed packages, the seeded site logo, the module
        /// templates - and ASP.NET leaves it NULL when the folder does not exist.
        /// </summary>
        /// <remarks>
        /// An app built on the Stomme.Server package has no web root of its own under <c>dotnet run</c>: the
        /// framework's static files are served from the package. Its first install failed creating the site,
        /// <c>ArgumentNullException</c> for <c>path1</c> in <c>DefaultSiteTemplate</c> (measured 2026-09-30); some thirty
        /// test harnesses had created an empty <c>wwwroot</c> to get past the same line. The folder is created and named,
        /// and a provider for it is put in front of the existing one, which is the shape ASP.NET builds when the folder
        /// exists - so files written there are served, and the static web assets still are.
        /// </remarks>
        internal static void EnsureWebRoot(IWebHostEnvironment environment)
        {
            if (!string.IsNullOrEmpty(environment.WebRootPath))
            {
                return;
            }

            var webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
            Directory.CreateDirectory(webRoot);
            environment.WebRootPath = webRoot;
            environment.WebRootFileProvider = new CompositeFileProvider(new PhysicalFileProvider(webRoot), environment.WebRootFileProvider);
        }

        /// <summary>
        /// Runs the installer against the built host — the same call, in the same place in the startup
        /// sequence, that <see cref="Main"/> makes before serving traffic. A non-empty
        /// <c>Installation.Message</c> means the install did not complete and the app must not serve.
        /// </summary>
        public static async Task<Installation> InstallAsync(WebApplication app)
        {
            var databaseManager = app.Services.GetService<IDatabaseManager>();
            var install = await databaseManager.InstallAsync();

            // THE BOOT PHASE - a staged restore is applied here, after the install/upgrade path and
            // before hosted services start or the first request is served (Main calls RunAsync only after this returns).
            if (install.Success && string.IsNullOrEmpty(install.Message))
            {
                var backup = app.Services.GetService<Stomme.Modules.Backup.Application.BackupOptions>();
                using var scope = app.Services.CreateScope();
                if (backup is { Enabled: true } && scope.ServiceProvider.GetService<Stomme.Modules.Backup.Application.BackupEngine>() is { } engine
                    && await engine.ApplyStagedRestoreAsync() is { Maintenance: true } report)
                {
                    install.Success = false;
                    install.Message = $"A staged restore of backup {report.SnapshotId} failed and so did its safety snapshot ({report.Reason}); " +
                        "the installation is in maintenance and will not serve. See the log, and restore a snapshot by hand.";
                }
            }

            // The sites Installation:Sites lists beyond the first, created on any boot that finds one missing. An invalid
            // list refuses to serve, by name, with nothing created.
            if (install.Success && string.IsNullOrEmpty(install.Message))
            {
                var sites = await databaseManager.ReconcileConfiguredSitesAsync();
                if (!sites.Success)
                {
                    install.Success = false;
                    install.Message = sites.Message;
                }
            }

            // The backup schedule Backup:Schedule:IntervalHours configures, seeded on a boot that finds none and the
            // job enabled with it. An invalid one refuses to serve, by name, with nothing written.
            if (install.Success && string.IsNullOrEmpty(install.Message)
                && await Stomme.Modules.Backup.Application.ConfiguredBackupSchedule.SeedAsync(app.Services, DateTime.UtcNow) is { } refusal)
            {
                install.Success = false;
                install.Message = refusal;
            }

            return install;
        }
    }
}
