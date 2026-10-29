using System;
using System.IO;
using Stomme.Backup.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Startup
{
    /// <summary>
    /// Wires backup/restore: the installation-wide catalogue (storage-blind, deliberately NOT tenant-owned), the
    /// archive stores - the local folder and an S3-compatible object store - the engine, the job and the surfaces.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<BackupSnapshotAggregate, Guid>();
            services.AddModuleAggregatePersistence<BackupScheduleAggregate, Guid>();
            services.AddScoped<IBackupSnapshotRepository, BackupSnapshotRepository>();
            services.AddScoped<IBackupScheduleRepository, BackupScheduleRepository>();

            // Backup:Archive - which store takes new snapshots, and the object store's settings; bound from the host's
            // own configuration, the override file included.
            services.TryAddSingleton(provider =>
            {
                var options = new BackupArchiveOptions();
                HostConfiguration.Resolve(provider)?.GetSection(BackupArchiveOptions.SectionName).Bind(options);
                return options;
            });
            services.TryAddSingleton<IS3ObjectClient>(provider => new AwsS3ObjectClient(provider.GetRequiredService<BackupArchiveOptions>().S3));

            // The local root is asked of THIS host (IConfigManager.GetDataDirectory), never the AppDomain-global DataDirectory
            // (BL-153); the S3 store stages under it. A snapshot is read from the store its row names, never assumed.
            services.TryAddSingleton(provider =>
            {
                var options = provider.GetRequiredService<BackupArchiveOptions>();
                var config = provider.GetRequiredService<IConfigManager>();
                var local = LocalFileBackupArchiveStore.ForHost(config);
                var problem = options.S3.Problem();
                return new BackupArchiveStores(
                    local,
                    problem is null
                        ? () => new S3BackupArchiveStore(provider.GetRequiredService<IS3ObjectClient>(), options.S3, config.GetInstallationId,
                            Path.Combine(local.Root, S3BackupArchiveStore.StagingFolderName), provider.GetService<ILogger<S3BackupArchiveStore>>())
                        : null,
                    problem,
                    options.Provider);
            });

            // Backup:Enabled (off by default) - bound from the host's own configuration, the override file included.
            services.TryAddSingleton(provider =>
            {
                var options = new Application.BackupOptions();
                HostConfiguration.Resolve(provider)?.GetSection(Application.BackupOptions.SectionName).Bind(options);
                return options;
            });
            services.AddScoped<Application.BackupEngine>();

            // The in-memory hand-off for long work a request starts; BackupWorker (a hosted service) is found by the host's scan.
            services.TryAddSingleton<Application.BackupWorkQueue>();

            // A data snapshot before a framework upgrade - a no-op unless Backup:Enabled.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IPreUpgradeSnapshot, Application.BackupPreUpgradeSnapshot>());

            // The surface: the controller calls the server service directly; it also wins over the client HTTP implementation.
            services.TryAddSingleton<Services.BackupOperationLog>();
            services.AddScoped<Services.ServerBackupService>();
            services.AddScoped<Services.IBackupService>(provider => provider.GetRequiredService<Services.ServerBackupService>());
        }
    }
}
