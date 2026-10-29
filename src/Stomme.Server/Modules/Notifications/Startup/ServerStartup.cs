using System;
using System.Collections.Generic;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Services;
using FluentValidation;
using Stomme.Modules.Notifications.Application;
using Stomme.Notifications;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Notifications.Startup
{
    /// <summary>
    /// Wires the Notifications module into the host by the startup scan. STORAGE-BLIND: ONE
    /// <c>AddModuleAggregatePersistence&lt;TAggregate, Guid&gt;()</c> per aggregate and ZERO persistence code - no
    /// <c>DbContext</c>, no RavenDB twin, no provider branch. On a relational install the four aggregates join the host's
    /// shared <c>ModuleAggregatesDbContext</c> (value objects as string columns, collections as JSON columns);
    /// on RavenDB and File they round-trip through the generic repositories.
    /// <para>
    /// It deliberately does NOT call <c>AddMediator()</c> or <c>AddMediatorBehavior(...)</c>: Guestbook's startup
    /// already did, and <c>AddMediator</c> is additive, so a second call would double every pipeline behavior
    /// Its handlers and validators are registered explicitly below.
    /// </para>
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
            services.AddModuleAggregatePersistence<NotificationRequestAggregate, Guid>();
            services.AddModuleAggregatePersistence<NotificationTemplateAggregate, Guid>();
            services.AddModuleAggregatePersistence<NotificationChannelAggregate, Guid>();
            services.AddModuleAggregatePersistence<NotificationPreferenceAggregate, Guid>();

            services.AddScoped<INotificationRequestRepository, NotificationRequestRepository>();
            services.AddScoped<INotificationTemplateRepository, NotificationTemplateRepository>();
            services.AddScoped<INotificationChannelRepository, NotificationChannelRepository>();
            services.AddScoped<INotificationPreferenceRepository, NotificationPreferenceRepository>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Application.NotificationPreferenceSubjectContributor>(); // A person's choices, in their data package
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Application.NotificationPreferenceErasureParticipant>(); // The subject's preferences go
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Application.NotificationRequestErasureParticipant>(); // The subject's recipient entries leave every request

            // The channel seam. Scoped: the e-mail and in-app channels write through the
            // scoped INotificationRepository, the webhook channel reads the scoped ISiteSecretStore. SMS has NO transport
            // (the resolver skips it) and SendGrid does not travel. The guarded webhook client is ONE singleton.
            services.AddScoped<INotificationChannel, EmailNotificationChannel>();
            services.AddScoped<INotificationChannel, InAppNotificationChannel>();
            services.AddScoped<INotificationChannel, WebhookNotificationChannel>();
            services.AddScoped<NotificationChannelResolver>();
            services.AddScoped<NotificationChannelDelivery>();
            services.AddScoped<IWebhookHostAllowlist, SiteSettingWebhookHostAllowlist>();
            services.AddSingleton<WebhookHttpClient>();
            services.AddSingleton<NotificationTemplateRenderer>();

            // Templates on the boot/upgrade path: additive,
            // create-if-absent per site, run by the framework's SiteContentReconciliationRunner. A later module adds
            // its own INotificationTemplateSource.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationTemplateSource, BuiltInNotificationTemplateSource>());
            services.AddScoped<ISiteContentReconciler, NotificationTemplateReconciler>();

            // The producer port + the Job-backed pipeline. NotificationDispatchJob (a
            // HostedServiceBase in Infrastructure/Jobs) is auto-registered by the framework's scan, not here. Nothing on
            // this path carries [Requires] (there is no HttpContext in a job). TryAdd for the clock so a host or a test can
            // supply its own TimeProvider first.
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
            services.AddScoped<NotificationDispatchProcessor>();
            services.AddScoped<NotificationPreferenceResolver>();
            services.AddScoped<INotificationSiteSettings, SettingRepositoryNotificationSiteSettings>();
            services.AddScoped<INotificationRecipientDirectory, SiteUserRecipientDirectory>();

            // The USER-FACING CQRS surface: every request carries [Requires] with one of the five
            // split keys; the job path above carries none. Handlers and validators are registered EXPLICITLY - never
            // AddMediator()/AddMediatorBehavior() here (Guestbook's startup already composed the mediator, and a second
            // AddMediator doubles every behavior; the authorization behavior is the host's, composed before any module).
            services.AddScoped<IRequestHandler<ConfigureChannelCommand, Guid>, ConfigureChannelCommandHandler>();
            services.AddScoped<IRequestHandler<SetChannelCredentialCommand, Unit>, SetChannelCredentialCommandHandler>();
            services.AddScoped<IRequestHandler<EnableChannelCommand, Unit>, EnableChannelCommandHandler>();
            services.AddScoped<IRequestHandler<DisableChannelCommand, Unit>, DisableChannelCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteChannelCommand, Unit>, DeleteChannelCommandHandler>();
            services.AddScoped<IRequestHandler<ListNotificationChannelsQuery, IReadOnlyList<NotificationChannelSummary>>, ListNotificationChannelsQueryHandler>();
            services.AddScoped<IRequestHandler<GetNotificationChannelQuery, NotificationChannelDetail>, GetNotificationChannelQueryHandler>();

            services.AddScoped<IRequestHandler<SaveNotificationTemplateCommand, Guid>, SaveNotificationTemplateCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteNotificationTemplateCommand, Unit>, DeleteNotificationTemplateCommandHandler>();
            services.AddScoped<IRequestHandler<ListNotificationTemplatesQuery, IReadOnlyList<NotificationTemplateSummary>>, ListNotificationTemplatesQueryHandler>();
            services.AddScoped<IRequestHandler<GetNotificationTemplateQuery, NotificationTemplateDetail>, GetNotificationTemplateQueryHandler>();
            services.AddScoped<IRequestHandler<PreviewNotificationTemplateQuery, NotificationTemplatePreview>, PreviewNotificationTemplateQueryHandler>();

            services.AddScoped<IRequestHandler<ListNotificationRequestsQuery, IReadOnlyList<NotificationRequestSummary>>, ListNotificationRequestsQueryHandler>();
            services.AddScoped<IRequestHandler<GetNotificationRequestQuery, NotificationRequestDetail>, GetNotificationRequestQueryHandler>();
            services.AddScoped<IRequestHandler<ListMyNotificationsQuery, IReadOnlyList<NotificationRequestSummary>>, ListMyNotificationsQueryHandler>();

            services.AddScoped<IRequestHandler<GetMyNotificationPreferencesQuery, NotificationPreferencesView>, GetMyNotificationPreferencesQueryHandler>();
            services.AddScoped<IRequestHandler<SaveMyNotificationPreferencesCommand, Unit>, SaveMyNotificationPreferencesCommandHandler>();

            services.AddScoped<IRequestHandler<SendTestNotificationCommand, Guid>, SendTestNotificationCommandHandler>();
            services.AddScoped<IRequestHandler<RetryNotificationRequestCommand, Unit>, RetryNotificationRequestCommandHandler>();

            services.AddScoped<IValidator<ConfigureChannelCommand>, ConfigureChannelCommandValidator>();
            services.AddScoped<IValidator<SaveNotificationTemplateCommand>, SaveNotificationTemplateCommandValidator>();
            services.AddScoped<IValidator<SendTestNotificationCommand>, SendTestNotificationCommandValidator>();

            // The module's server IService (dispatches via IMediator). Explicit AddTransient so it wins over the client HTTP
            // implementation on the server - the Guestbook / HtmlText pattern.
            services.AddTransient<INotificationsService, ServerNotificationsService>();
        }
    }
}
