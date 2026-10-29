#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Infrastructure;
using System.Collections.Generic;
using Stomme.Mediator;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Application;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.DataExport.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.DataExport.Startup
{
    /// <summary>
    /// Wires the data-export module: two storage-blind aggregates, the renderers and the queue
    /// processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered by the framework's assembly scan like every other module startup. Each aggregate takes ONE
    /// <c>AddModuleAggregatePersistence</c> call and the module authors no persistence code: on a relational
    /// install the host maps them into the shared context, on RavenDB or the File store they round-trip through
    /// the install's own store.
    /// </para>
    /// <para>
    /// <b>Off by default, and closed in three places.</b> <c>DataExport:Enabled</c> is false, so the queue job's body
    /// returns at once and no <c>Exports/</c> folder is created; a job is re-authorised from STORED rows when it runs,
    /// and a site has no <c>export:*</c> rows until the module's client half seeds them; and the artifact folder is
    /// private and Administrators-only. Every collaborator is registered, because the host validates its container at
    /// build - a processor with an unregistered dependency is a host that does not boot (measured: 219 tests red).
    /// </para>
    /// </remarks>
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
            services.AddModuleAggregatePersistence<ExportJobAggregate, Guid>();
            services.AddModuleAggregatePersistence<ExportAuditEntryAggregate, Guid>();

            services.AddScoped<IExportJobRepository, ExportJobRepository>();
            services.AddScoped<IExportAuditRepository, ExportAuditRepository>();
            services.AddModuleAggregatePersistence<ExportScheduleAggregate, Guid>(); // Recurring exports, run as their owner
            services.AddScoped<IExportScheduleRepository, ExportScheduleRepository>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Application.DataExportErasureParticipant>(); // The subject's jobs go; the export audit is retained one year

            // One renderer per format. TryAddEnumerable keys on the implementation type, so a second startup pass
            // cannot double them; ExportRendererRegistry refuses two renderers claiming one format.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITabularRenderer, CsvTabularRenderer>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITabularRenderer, JsonTabularRenderer>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITabularRenderer, XlsxTabularRenderer>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITabularRenderer, PdfTabularRenderer>());
            services.TryAddSingleton<ExportRendererRegistry>();

            // Sources are contributed by whoever has data to export (IExportSource registrations); the registry
            // refuses a bad or duplicate one at construction.
            // SCOPED, not singleton: a source may need scoped services (the built-in one reads ILogRepository), and a
            // singleton registry holding it would be a captive dependency - which this host's scope validation refuses.
            services.TryAddScoped<ExportSourceRegistry>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportSource, Sources.EventLogExportSource>());

            services.TryAddSingleton<IExportClock, SystemExportClock>();
            services.TryAddSingleton(provider =>
            {
                var options = new DataExportOptions();
                // IConfigurationRoot FIRST: AddStomme registers the fully-merged configuration under it (every layered
                // source, and the override file a driven install uses). A plain IConfiguration is the host's default
                // config without those layers, so DataExport:Enabled would read false where it was switched on.
                var configuration = HostConfiguration.Resolve(provider);
                configuration?.GetSection(DataExportOptions.SectionName).Bind(options);
                return options;
            });

            services.TryAddSingleton<IExportCellFormatter, InvariantExportCellFormatter>();
            // Re-authorised from STORED rows when the job runs. With no export:* rows on a site - the
            // state of every install until the module's client half seeds them - this denies, which is the right way
            // round for it to fail.
            services.TryAddScoped<Stomme.Infrastructure.Security.IStoredRowPermissionEvaluator, Stomme.Infrastructure.Security.StoredRowPermissionEvaluator>();
            services.TryAddScoped<IExportJobAuthorizer, StoredRowExportJobAuthorizer>();
            // Artifacts are File rows in the site's private Exports/ folder, created additively on the boot path.
            services.TryAddScoped<ExportsFolder>();
            services.TryAddScoped<IExportArtifactStore, FolderExportArtifactStore>();
            services.AddScoped<ISiteContentReconciler, DataExportSiteReconciler>();
            // The ready notice rides the notification producer port when Notifications is present, and its one template joins that
            // module's per-site template reconciliation through its own source seam.
            services.TryAddScoped<ExportReadyNotifier>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Modules.Notifications.Application.Templates.INotificationTemplateSource, ExportTemplateSource>());
            services.TryAddScoped<IExportSiteSettings, SettingRepositoryExportSiteSettings>();
            services.TryAddScoped<ExportQueueProcessor>();
            services.TryAddScoped<ExportExpiryPass>();

            // The user-facing CQRS surface. Handlers are registered EXPLICITLY - never AddMediator() here:
            // Guestbook's startup already composed the mediator, and a second AddMediator would double every pipeline
            // behavior. Nothing on the JOB path carries [Requires]: a job has no HttpContext.
            services.TryAddScoped<ExportCaller>();
            services.AddScoped<IRequestHandler<RequestExportCommand, Guid>, RequestExportCommandHandler>();
            services.AddScoped<IRequestHandler<CancelExportCommand, bool>, CancelExportCommandHandler>();
            services.AddScoped<IRequestHandler<PrepareExportDownloadCommand, ExportDownload?>, PrepareExportDownloadCommandHandler>();
            services.AddScoped<IRequestHandler<ListMyExportsQuery, IReadOnlyList<ExportJobView>>, ListMyExportsQueryHandler>();
            services.AddScoped<IRequestHandler<ListSiteExportsQuery, IReadOnlyList<ExportJobView>>, ListSiteExportsQueryHandler>();
            services.AddScoped<IRequestHandler<GetExportQuery, ExportJobView?>, GetExportQueryHandler>();
            services.AddScoped<IRequestHandler<ListExportSourcesQuery, IReadOnlyList<ExportSourceView>>, ListExportSourcesQueryHandler>();
            // Recurring exports, authored by a person who may make the export now, run as their owner.
            services.AddScoped<IRequestHandler<CreateExportScheduleCommand, ExportScheduleView>, CreateExportScheduleCommandHandler>();
            services.AddScoped<IRequestHandler<ListExportSchedulesQuery, IReadOnlyList<ExportScheduleView>>, ListExportSchedulesQueryHandler>();
            services.AddScoped<IRequestHandler<ChangeExportScheduleCommand, bool>, ChangeExportScheduleCommandHandler>();
            services.AddScoped<IRequestHandler<TakeOverExportScheduleCommand, bool>, TakeOverExportScheduleCommandHandler>();
            services.TryAddScoped<ExportSchedulePass>();

            // The module's server IService (dispatches via IMediator). Explicit AddTransient so it wins over the client
            // HTTP implementation on the server - the Guestbook / Metering pattern.
            services.AddTransient<Services.IDataExportService, Services.ServerDataExportService>();
        }
    }

    /// <summary>
    /// The Wave-1 formatter: declines every value, so each renderer writes its own culture-invariant text.
    /// </summary>
    /// <remarks>The real, per-site formatter arrives with Formats and replaces this registration.</remarks>
    public sealed class InvariantExportCellFormatter : IExportCellFormatter
    {
        public string? Format(object? value, ExportColumn column) => null;
    }
}
