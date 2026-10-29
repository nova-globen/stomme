#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Engine.Common.Features;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime.Features;
using Stomme.Engine.Tenants.Extensions;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Persistence;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Modules.Pipelines.Triggers;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Pipelines.Startup
{
    /// <summary>
    /// The Pipelines module's composition: the vendored engine, composed ONLY behind
    /// <c>Pipelines:Enabled</c> so a stock install is byte-for-byte unchanged, with its stores on Stomme
    /// persistence, its tenants bridged to Stomme's SITES (the Tenants feature over <see cref="SiteTenantsProvider"/>,
    /// so a run dispatched from a site keeps that site as its engine tenant on the worker), and its lock directory
    /// under this host's data directory rather than the process's current directory. Everything the engine registers -
    /// its scoped runner, its stores, its hosted services - exists in the container only when the switch is on; with
    /// it off, not one engine type is registered and no middleware is added.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public const string EnabledKey = "Pipelines:Enabled";

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // Runs after Stomme's tenant resolution (UseStomme orders module middleware after it), so the
            // request's site is known and its engine tenant can be entered for everything downstream.
            if (app.ApplicationServices.GetService<PipelinesComposed>() is not null)
            {
                app.UseMiddleware<EngineTenantMiddleware>();
            }
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            if (!IsEnabled(services))
            {
                return;
            }

            var kind = services.AddEnginePersistence();
            var dataDirectory = HostDataDirectory.Resolve(services);
            var dataRoot = string.IsNullOrEmpty(dataDirectory) ? AppContext.BaseDirectory : dataDirectory;
            var locks = Path.Combine(dataRoot, "engine-locks");
            services.AddEngine(module =>
            {
                module.UseStommeStores(kind);
                module.UseTenants();
                module.Configure<MultitenancyFeature>(multitenancy => multitenancy.UseTenantsProvider<SiteTenantsProvider>());
                module.Configure<PipelineRuntimeFeature>(runtime =>
                {
                    runtime.DistributedLockProvider = _ => new FileDistributedSynchronizationProvider(new DirectoryInfo(locks));
                    // Each trigger's call stamped with its arrival as it is handed over - the order a pipeline that runs in
                    // turn gives its waiting runs (the engine takes the calls on several workers, in no fixed order).
                    runtime.StimulusDispatcher = provider => new Reliability.ArrivalStampingStimulusDispatcher(
                        ActivatorUtilities.CreateInstance<Stomme.Engine.Pipelines.Runtime.BackgroundStimulusDispatcher>(provider),
                        provider.GetRequiredService<Stomme.Engine.Common.ISystemClock>());
                });
                // The step families (increment (d)): every public step in this assembly joins the engine's registry.
                module.AddStepsFrom<PipelineStepsMarker>();
                // BL-229, n8n's "save execution progress": the strategy a pipeline that asks names - its run stored after each step.
                PipelineRunPolicy.UseSaveProgress(module);
                // Which pipelines may call a pipeline - added before the retry, so it sits inside it and a refused call
                // takes the step's Failed path like any failure of the step.
                Stomme.Modules.Pipelines.Reliability.PipelineCallerPolicy.UseCallerPolicy(module);
                // A step's own retry and continue-on-error.
                Stomme.Modules.Pipelines.Reliability.StepReliability.UseStepReliability(module);
                // Stop and the pipeline's time limit, before each step - added after the retry, so it sits outside it and
                // a stopped run is not tried again.
                Stomme.Modules.Pipelines.Reliability.RunControlMiddleware.UseRunControl(module);
            });
            services.AddSingleton<Stomme.Modules.Pipelines.Reliability.PipelineRunStops>();
            // A step test stores nothing - no run in the site's list, no log.
            StepTestCommitGate.Wrap(services);
            // A pipeline's variables are kept with its run - through a pause, and for a retry from a failed step.
            PipelineVariableStorage.Wrap(services);

            // An input's template ({{ variable.path }}) is an expression kind of the engine's.
            services.AddExpressionDescriptorProvider<Stomme.Modules.Pipelines.Expressions.TemplateExpressionDescriptorProvider>();
            // A condition built from rules - what a Decision, an If, a While and a Switch case test.
            services.AddExpressionDescriptorProvider<Stomme.Modules.Pipelines.Expressions.ConditionExpressionDescriptorProvider>();
            // An input computed by JavaScript in the bounded sandbox - refused at run where scripts are off.
            services.AddExpressionDescriptorProvider<Stomme.Modules.Pipelines.Expressions.ScriptExpressionDescriptorProvider>();

            // The steps' settings, from THIS host's configuration root - the file steps default to a folder
            // under the host's data directory, so a fresh install needs no setting to confine them.
            services.Configure<PipelineStepOptions>((IConfiguration?)HostConfiguration(services)?.GetSection(PipelineStepOptions.Section) ?? new ConfigurationBuilder().Build());
            services.PostConfigure<PipelineStepOptions>(options => options.Files.Root ??= Path.Combine(dataRoot, "Pipelines", "Files"));

            // The triggers (increment (e)): the schedules task runs per site tenant under the engine's tenant task
            // manager; the webhook receiver's rate-limit policy joins the host's limiter; the event bridge's bus handler
            // is discovered by the messaging scan and resolves the module lazily (see PipelineEventRaisedHandler).
            var triggerSection = (IConfiguration?)HostConfiguration(services)?.GetSection(PipelineTriggerOptions.Section) ?? new ConfigurationBuilder().Build();
            services.Configure<PipelineTriggerOptions>(triggerSection);
            var triggerOptions = new PipelineTriggerOptions();
            triggerSection.Bind(triggerOptions);
            services.AddRecurringTask<ScheduledPipelinesTask>(TimeSpan.FromSeconds(Math.Max(5, triggerOptions.Schedules.IntervalSeconds)));
            // Finished runs older than Pipelines:Runs:KeepDays are deleted - off (0) unless the install says so.
            services.AddRecurringTask<PipelineRunRetentionTask>(TimeSpan.FromMinutes(Math.Max(1, triggerOptions.Runs.IntervalMinutes)));
            // A pipeline set not to keep the runs that finish well has them deleted shortly after they end.
            services.AddRecurringTask<PipelineRunSettingsTask>(TimeSpan.FromSeconds(Math.Max(10, triggerOptions.Runs.SettingsIntervalSeconds)));
            // A run paused by a Wait step is picked up again when its moment comes (n8n's Wait node).
            services.AddRecurringTask<DueWaitsTask>(TimeSpan.FromSeconds(Math.Max(5, triggerOptions.Waits.IntervalSeconds)));
            // One at a time, the rest in turn (n8n's concurrency control) - a trigger's start of such a pipeline is stored
            // first by the module's starter, which hands every other start to the engine's; the engine knows the strategy by its
            // type; the task starts each pipeline's next waiting run once the one before it has ended.
            services.AddScoped<Stomme.Engine.Pipelines.IPipelineActivationStrategy, Reliability.InTurnStrategy>();
            services.AddScoped<Stomme.Engine.Pipelines.Runtime.DefaultPipelineStarter>();
            services.AddScoped<Stomme.Engine.Pipelines.Runtime.IPipelineStarter, Reliability.InTurnPipelineStarter>();
            services.AddScoped<Reliability.PipelineTurns>();
            services.AddRecurringTask<Reliability.PipelineTurnsTask>(TimeSpan.FromSeconds(Math.Max(2, triggerOptions.Runs.TurnsIntervalSeconds)));
            // An evaluation's rows run a few at a time, the next started as one ends (n8n's Evaluations).
            services.AddRecurringTask<PipelineEvaluationTask>(TimeSpan.FromSeconds(Math.Max(2, triggerOptions.Evaluations.IntervalSeconds)));
            // "File added" (n8n's Local File Trigger) - a watched folder recorded as its pipeline is published, and looked at
            // per site tenant, each file put there since starting the pipelines watching it.
            services.AddScoped<WatchedFolders>();
            services.AddRecurringTask<WatchedFoldersTask>(TimeSpan.FromSeconds(Math.Max(2, triggerOptions.FileTriggers.IntervalSeconds)));
            services.AddNotificationHandler<StartWatchingOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            // "Email received" (n8n's Email Trigger (IMAP)) - a watched mailbox recorded as its pipeline is published, and
            // looked at per site tenant through its connection, each message since starting the pipelines watching it.
            services.AddScoped<WatchedMailboxes>();
            services.AddRecurringTask<WatchedMailboxesTask>(TimeSpan.FromSeconds(Math.Max(5, triggerOptions.EmailTriggers.IntervalSeconds)));
            services.AddNotificationHandler<StartWatchingMailboxOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            // n8n's Telegram Trigger: each site's watched bots asked for their messages.
            services.AddScoped<WatchedTelegramBots>();
            services.AddRecurringTask<WatchedTelegramBotsTask>(TimeSpan.FromSeconds(Math.Max(5, triggerOptions.TelegramTriggers.IntervalSeconds)));
            services.AddNotificationHandler<StartWatchingTelegramBotOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            PipelineWebhooks.AddPolicy(services, triggerOptions.Webhooks.RequestsPerMinute);
            // The Form trigger's pages - a limit of their own.
            PipelineWebhooks.AddPolicy(services, triggerOptions.Forms.RequestsPerMinute, PipelineFormPage.PolicyName);
            // The Chat trigger's pages - a limit of their own, each message being a run.
            PipelineWebhooks.AddPolicy(services, triggerOptions.Chats.RequestsPerMinute, PipelineChatPage.PolicyName);
            services.AddScoped<IEngineTenantBridge, EngineTenantBridge>();
            // A failed run starts the site's pipelines whose "Pipeline failed" trigger watches it (n8n's error workflow).
            services.AddNotificationHandler<StartPipelinesOnFailure, Stomme.Engine.Pipelines.Notifications.PipelineFinished>();
            // What a run was started with is kept on it - for its page and for a retry (the engine keeps only declared inputs).
            services.AddNotificationHandler<KeepRunInput, Stomme.Engine.Pipelines.Notifications.PipelineStarted>();
            // A run the engine has stored is the store's to answer for - "not yet" (204) is only for one never stored.
            services.AddNotificationHandler<Reliability.MarkRunStored, Stomme.Engine.Pipelines.Management.Notifications.PipelineInstanceSaved>();
            services.AddSingleton<PipelinesComposed>();
            // The site's pipeline activity - every command of the module recorded once it succeeds (n8n's audit log).
            Stomme.Modules.Pipelines.Audit.PipelineAuditRegistration.Register(services);
            // Comments on a pipeline's steps - a team's discussion where the pipeline is built.
            Stomme.Modules.Pipelines.Comments.PipelineStepCommentRegistration.Register(services);

            // The module's surface (increment (b)). Handlers are registered EXPLICITLY - never AddMediator() here: the
            // host composes the mediator once, and a bare AddMediator() now binds to the ENGINE's overload (measured).
            services.AddScoped<IRequestHandler<ListPipelinesQuery, IReadOnlyList<PipelineView>>, ListPipelinesQueryHandler>();
            services.AddScoped<IRequestHandler<GetPipelineQuery, PipelineDetail?>, GetPipelineQueryHandler>();
            services.AddScoped<IRequestHandler<ListRunsQuery, IReadOnlyList<PipelineRunView>>, ListRunsQueryHandler>();
            services.AddScoped<IRequestHandler<GetRunQuery, PipelineRunView?>, GetRunQueryHandler>();
            services.AddScoped<IRequestHandler<IsRunStartingQuery, bool>, IsRunStartingQueryHandler>(); // a run started, not stored yet
            services.AddScoped<IRequestHandler<ListCallersQuery, IReadOnlyList<PipelineCallerView>>, ListCallersQueryHandler>();
            services.AddScoped<IRequestHandler<GetRunLogQuery, IReadOnlyList<PipelineRunLogLine>>, GetRunLogQueryHandler>();
            services.AddScoped<IRequestHandler<ListRunStepsQuery, IReadOnlyList<PipelineStepRunView>>, ListRunStepsQueryHandler>();
            services.AddScoped<IRequestHandler<TestStepCommand, StepTestResult>, TestStepCommandHandler>();
            services.AddScoped<IRequestHandler<ListVersionsQuery, IReadOnlyList<PipelineVersionView>>, ListVersionsQueryHandler>();
            services.AddScoped<IRequestHandler<GetVersionGraphQuery, PipelineGraph?>, GetVersionGraphQueryHandler>();
            services.AddScoped<IRequestHandler<CreatePipelineCommand, PipelineView>, CreatePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<UpdatePipelineCommand, PipelineView?>, UpdatePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<PublishPipelineCommand, PublishResult?>, PublishPipelineCommandHandler>();
            services.AddScoped<IRequestHandler<RetractPipelineCommand, bool>, RetractPipelineCommandHandler>();
            services.AddScoped<IRequestHandler<DeletePipelineCommand, bool>, DeletePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<ArchivePipelineCommand, bool>, ArchivePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<RestorePipelineCommand, bool>, RestorePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<SetPipelineTagsCommand, IReadOnlyList<string>?>, SetPipelineTagsCommandHandler>();
            services.AddScoped<IRequestHandler<SetPipelineFolderCommand, string?>, SetPipelineFolderCommandHandler>();
            services.AddScoped<IRequestHandler<MovePipelineFolderCommand, PipelineFolderMoveResult?>, MovePipelineFolderCommandHandler>();
            services.AddScoped<IRequestHandler<FindInPipelinesQuery, IReadOnlyList<PipelineFoundView>>, FindInPipelinesQueryHandler>();
            services.AddScoped<IRequestHandler<GetPipelineMapQuery, PipelineMapView>, GetPipelineMapQueryHandler>();
            services.AddScoped<IRequestHandler<ListAgentToolsQuery, IReadOnlyList<AgentToolView>>, ListAgentToolsQueryHandler>();
            services.AddScoped<IRequestHandler<GetSiteVariablesQuery, Dictionary<string, string>>, GetSiteVariablesQueryHandler>();
            services.AddScoped<IRequestHandler<SetSiteVariablesCommand, Dictionary<string, string>>, SetSiteVariablesCommandHandler>();
            // n8n's Data tables: the site's tables and their rows, in the engine's key-value store.
            services.AddScoped<IRequestHandler<ListSiteTablesQuery, List<PipelineSiteTableView>>, ListSiteTablesQueryHandler>();
            services.AddScoped<IRequestHandler<CreateSiteTableCommand, PipelineSiteTableView>, CreateSiteTableCommandHandler>();
            services.AddScoped<IRequestHandler<UpdateSiteTableCommand, PipelineSiteTableView?>, UpdateSiteTableCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteSiteTableCommand, bool>, DeleteSiteTableCommandHandler>();
            // The site's pipeline folder, as the Files page sees it.
            services.AddScoped<IRequestHandler<ListPipelineFilesQuery, PipelineFolderView?>, ListPipelineFilesQueryHandler>();
            services.AddScoped<IRequestHandler<UploadPipelineFileCommand, PipelineFileEntryView>, UploadPipelineFileCommandHandler>();
            services.AddScoped<IRequestHandler<CreatePipelineFolderCommand, PipelineFileEntryView>, CreatePipelineFolderCommandHandler>();
            services.AddScoped<IRequestHandler<DeletePipelineFileCommand, bool>, DeletePipelineFileCommandHandler>();
            services.AddScoped<IRequestHandler<GetPipelineFileQuery, PipelineFileContent?>, GetPipelineFileQueryHandler>();
            services.AddScoped<IRequestHandler<GetTableRowsQuery, PipelineTableRowsView?>, GetTableRowsQueryHandler>();
            services.AddScoped<IRequestHandler<AddTableRowCommand, Dictionary<string, string>?>, AddTableRowCommandHandler>();
            services.AddScoped<IRequestHandler<UpdateTableRowCommand, Dictionary<string, string>?>, UpdateTableRowCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteTableRowCommand, bool>, DeleteTableRowCommandHandler>();
            services.AddScoped<IRequestHandler<SetRunReviewCommand, PipelineRunReview?>, SetRunReviewCommandHandler>(); // n8n's execution annotations
            // n8n's Evaluations - a pipeline run over a data table's rows, scored.
            services.AddScoped<IRequestHandler<StartEvaluationCommand, PipelineEvaluationView?>, StartEvaluationCommandHandler>();
            services.AddScoped<IRequestHandler<ListEvaluationsQuery, List<PipelineEvaluationView>>, ListEvaluationsQueryHandler>();
            services.AddScoped<IRequestHandler<GetEvaluationQuery, PipelineEvaluationView?>, GetEvaluationQueryHandler>();
            services.AddScoped<IRequestHandler<CancelEvaluationCommand, PipelineEvaluationView?>, CancelEvaluationCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteEvaluationCommand, bool>, DeleteEvaluationCommandHandler>();
            services.AddScoped<IRequestHandler<ExportTableCsvQuery, PipelineTableCsv?>, ExportTableCsvQueryHandler>();
            services.AddScoped<IRequestHandler<ImportTableCsvCommand, PipelineTableImportResult?>, ImportTableCsvCommandHandler>();
            // $vars, read once per scope however many expressions a run evaluates.
            services.AddScoped<PipelineSiteVariablesCache>();
            services.AddScoped<IRequestHandler<StartPipelineCommand, PipelineRunStarted?>, StartPipelineCommandHandler>();
            services.AddScoped<IRequestHandler<CancelRunCommand, bool>, CancelRunCommandHandler>();
            services.AddScoped<IRequestHandler<RetryRunCommand, PipelineRunStarted?>, RetryRunCommandHandler>();
            services.AddScoped<IRequestHandler<RetryFromFailedStepCommand, PipelineRunStarted?>, RetryFromFailedStepCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteRunCommand, bool>, DeleteRunCommandHandler>();
            // "Ask for approval" - the runs waiting for a decision, and a person's decision.
            services.AddScoped<IRequestHandler<ListApprovalsQuery, IReadOnlyList<PipelineApprovalView>>, ListApprovalsQueryHandler>();
            services.AddScoped<IRequestHandler<DecideApprovalCommand, PipelineApprovalDecided?>, DecideApprovalCommandHandler>();
            // A call to a run's call address (Wait for a call), dispatched through the engine's stimulus path.
            services.AddScoped<PipelineCallReceiver>();
            services.AddScoped<PipelineFormWaits>(); // A run waiting for a form - its page and its submission
            services.AddSingleton<Triggers.PipelineWebhookAnswers>(); // the calls waiting for their pipeline's answer, in this process
            // BL-229, n8n's multi-page forms: the submissions waiting to be taken on to their next page, in this process - and a burst
            // that ends without one says so, so the person is thanked at once.
            services.AddSingleton<Triggers.PipelineFormContinuations>();
            services.AddNotificationHandler<Triggers.FormContinuationOnCommit, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineStateCommitted>();
            services.AddScoped<Triggers.PipelineWebhookReceiver>();
            services.AddScoped<Triggers.PipelineFormReceiver>();
            services.AddScoped<Triggers.PipelineChatReceiver>();
            // n8n's Insights - how the site's pipelines have been running.
            services.AddScoped<IRequestHandler<GetInsightsQuery, PipelineInsightsView>, GetInsightsQueryHandler>();
            services.AddScoped<IRequestHandler<GetStepInsightsQuery, PipelineStepInsightsView>, GetStepInsightsQueryHandler>(); // and each step's
            services.AddScoped<IRequestHandler<ListUpcomingRunsQuery, PipelineUpcomingView>, ListUpcomingRunsQueryHandler>(); // and what runs next
            services.AddScoped<IRequestHandler<ListStepsQuery, IReadOnlyList<StepCatalogEntry>>, ListStepsQueryHandler>();
            services.AddScoped<IRequestHandler<GetGraphQuery, PipelineGraph?>, GetGraphQueryHandler>();
            services.AddScoped<IRequestHandler<SaveGraphCommand, PipelineView?>, SaveGraphCommandHandler>();

            // The collaborating save: the conflict resolver and the in-process gate are the collaboration
            // seam's; TryAdd, so the module needs no other module's startup to have run first.
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<Stomme.Collaboration.IConflictResolver, Stomme.Modules.Collaboration.Application.ConflictResolver>();
            services.TryAddSingleton<Stomme.Modules.Collaboration.Application.CollaborationGate>();
            services.TryAddScoped<Stomme.Modules.Collaboration.Realtime.ICollaborationNotifier, Stomme.Modules.Collaboration.Realtime.CollaborationNotifier>();
            services.AddScoped<IRequestHandler<SaveGraphMergedCommand, GraphSaveResult?>, SaveGraphMergedCommandHandler>();
            services.AddScoped<IRequestHandler<ValidateGraphQuery, IReadOnlyList<string>>, ValidateGraphQueryHandler>();
            services.AddScoped<IRequestHandler<ImportPipelineCommand, PipelineView>, ImportPipelineCommandHandler>();

            // The editor's trigger panel: a trigger's address or next runs, and listening for one webhook call -
            // the receiver hands each authentic call to the listeners of its site and name (in-process).
            services.AddSingleton<PipelineWebhookListeners>();
            services.AddScoped<IRequestHandler<GetTriggerInfoQuery, TriggerInfoView>, GetTriggerInfoQueryHandler>();
            services.AddScoped<IRequestHandler<FetchNewestEmailQuery, PipelineTestEventView>, FetchNewestEmailQueryHandler>();
            services.AddScoped<IRequestHandler<ListenForWebhookCommand, WebhookCallView>, ListenForWebhookCommandHandler>();

            // A webhook's own secret, in the site's secret store (scoped, as the store is) - read by the receiver and the
            // trigger panel, made and removed from the editor.
            services.AddScoped<PipelineWebhookSecrets>();
            services.AddScoped<IRequestHandler<RenewWebhookSecretCommand, PipelineWebhookSecretView>, RenewWebhookSecretCommandHandler>();
            services.AddScoped<IRequestHandler<RemoveWebhookSecretCommand, bool>, RemoveWebhookSecretCommandHandler>();

            // What the site lets the editor offer, and a JavaScript expression's preview.
            services.AddScoped<IRequestHandler<GetCapabilitiesQuery, PipelineCapabilitiesView>, GetCapabilitiesQueryHandler>();
            services.AddScoped<IRequestHandler<PreviewScriptCommand, ScriptPreviewView>, PreviewScriptCommandHandler>();

            // n8n's AI workflow builder: the canvas changed by the site's model. The AI assistant registers its designer
            // over this "none composed" default (a plain Add there, so it wins whichever startup runs first).
            services.TryAddScoped<IPipelineGraphDesigner, NoPipelineGraphDesigner>();
            services.AddScoped<IRequestHandler<DesignPipelineGraphCommand, PipelineGraphDesignView>, DesignPipelineGraphCommandHandler>();
            // And n8n's "Ask AI" in its Code node: a script written by the site's model, over the same kind of default.
            services.TryAddScoped<IPipelineScriptWriter, NoPipelineScriptWriter>();
            services.AddScoped<IRequestHandler<WriteScriptCommand, PipelineScriptView>, WriteScriptCommandHandler>();
            services.AddScoped<IRequestHandler<CheckScriptsQuery, PipelineScriptCheckView>, CheckScriptsQueryHandler>();
            services.AddScoped<IRequestHandler<RaisePipelineEventCommand, bool>, RaisePipelineEventCommandHandler>();
            services.AddTransient<Services.IPipelinesService, Services.ServerPipelinesService>();

            // The MCP surface (increment (f)): a contributor the MCP module publishes when IT is composed; inert otherwise.
            services.AddScoped<Stomme.Mcp.IMcpContributor, Mcp.PipelinesMcpContributor>();

            // A run's execution log as a D108 export source (re-booked): registered with the module, so it exists only
            // while Pipelines is on; the export job resolves it per site and it enters that site's engine tenant itself.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, PipelineRunLogExportSource>());
        }

        // Read at composition time from the host's own configuration root (the one that ends with the override
        // file), the way the storage-blind modules read Database:DefaultDBType.
        internal static bool IsEnabled(IServiceCollection services)
        {
            var configuration = HostConfiguration(services);
            return bool.TryParse(configuration?[EnabledKey], out var enabled) && enabled;
        }

        private static IConfigurationRoot? HostConfiguration(IServiceCollection services) =>
            services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?
                .ImplementationInstance as IConfigurationRoot;

        /// <summary>Present in the container exactly when the engine was composed; what Configure keys its middleware on.</summary>
        internal sealed class PipelinesComposed
        {
        }
    }
}
