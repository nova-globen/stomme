using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Common.Features;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Runtime.ActivationValidators;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Handlers;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Providers;
using Stomme.Engine.Pipelines.Runtime.Stores;
using Stomme.Engine.Pipelines.Runtime.Tasks;
using Stomme.Engine.Pipelines.Runtime.UIHints;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime.Features;

/// <summary>
/// Installs and configures pipeline runtime features.
/// </summary>
[DependsOn(typeof(SystemClockFeature))]
public class PipelineRuntimeFeature(IModule module) : FeatureBase(module)
{
    private IDictionary<string, DispatcherChannel> PipelineDispatcherChannels { get; set; } = new Dictionary<string, DispatcherChannel>();

    /// <summary>
    /// A list of pipeline builders configured during application startup.
    /// </summary>
    public IDictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>> Pipelines { get; set; } = new Dictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>>();

    /// <summary>
    /// A factory that instantiates a concrete <see cref="IPipelineRuntime"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineRuntime> PipelineRuntime { get; set; } = sp => ActivatorUtilities.CreateInstance<LocalPipelineRuntime>(sp);

    /// <summary>
    /// A factory that instantiates an <see cref="IPipelineDispatcher"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineDispatcher> PipelineDispatcher { get; set; } = sp =>
    {
        var decoratedService = ActivatorUtilities.CreateInstance<BackgroundPipelineDispatcher>(sp);
        return ActivatorUtilities.CreateInstance<ValidatingPipelineDispatcher>(sp, decoratedService);
    };
    
    /// <summary>
    /// A factory that instantiates an <see cref="IStimulusDispatcher"/>.
    /// </summary>
    public Func<IServiceProvider, IStimulusDispatcher> StimulusDispatcher { get; set; } = sp => ActivatorUtilities.CreateInstance<BackgroundStimulusDispatcher>(sp);

    /// <summary>
    /// A factory that instantiates an <see cref="IPipelineCancellationDispatcher"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineCancellationDispatcher> PipelineCancellationDispatcher { get; set; } = sp => ActivatorUtilities.CreateInstance<BackgroundPipelineCancellationDispatcher>(sp);

    /// <summary>
    /// A factory that instantiates an <see cref="IBookmarkStore"/>.
    /// </summary>
    public Func<IServiceProvider, IBookmarkStore> BookmarkStore { get; set; } = sp => sp.GetRequiredService<MemoryBookmarkStore>();

    /// <summary>
    /// A factory that instantiates an <see cref="IBookmarkQueueStore"/>.
    /// </summary>
    public Func<IServiceProvider, IBookmarkQueueStore> BookmarkQueueStore { get; set; } = sp => sp.GetRequiredService<MemoryBookmarkQueueStore>();

    /// <summary>
    /// A factory that instantiates an <see cref="ITriggerStore"/>.
    /// </summary>
    public Func<IServiceProvider, ITriggerStore> TriggerStore { get; set; } = sp => sp.GetRequiredService<MemoryTriggerStore>();

    /// <summary>
    /// A factory that instantiates an <see cref="IPipelineExecutionLogStore"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineExecutionLogStore> PipelineExecutionLogStore { get; set; } = sp => sp.GetRequiredService<MemoryPipelineExecutionLogStore>();

    /// <summary>
    /// A factory that instantiates an <see cref="IStepExecutionStore"/>.
    /// </summary>
    public Func<IServiceProvider, IStepExecutionStore> StepExecutionLogStore { get; set; } = sp => sp.GetRequiredService<MemoryStepExecutionStore>();

    /// <summary>
    /// A factory that instantiates an <see cref="IDistributedLockProvider"/>.
    /// </summary>
    public Func<IServiceProvider, IDistributedLockProvider> DistributedLockProvider { get; set; } = _ => new FileDistributedSynchronizationProvider(new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "App_Data/locks")));

    /// <summary>
    /// A factory that instantiates an <see cref="ITaskDispatcher"/>.
    /// </summary>
    public Func<IServiceProvider, ITaskDispatcher> RunTaskDispatcher { get; set; } = sp => sp.GetRequiredService<BackgroundTaskDispatcher>();

    /// <summary>
    /// A factory that instantiates an <see cref="IBackgroundStepScheduler"/>.
    /// </summary>
    public Func<IServiceProvider, IBackgroundStepScheduler> BackgroundStepScheduler { get; set; } = sp => ActivatorUtilities.CreateInstance<LocalBackgroundStepScheduler>(sp);

    /// <summary>
    /// A factory that instantiates a log record sink for an <see cref="StepExecutionRecord"/>.
    /// </summary>
    public Func<IServiceProvider, ILogRecordSink<StepExecutionRecord>> StepExecutionLogSink { get; set; } = sp => sp.GetRequiredService<StoreStepExecutionLogSink>();

    /// <summary>
    /// A factory that instantiates a log record sink for an <see cref="PipelineExecutionLogRecord"/>.
    /// </summary>
    public Func<IServiceProvider, ILogRecordSink<PipelineExecutionLogRecord>> PipelineExecutionLogSink { get; set; } = sp => sp.GetRequiredService<StorePipelineExecutionLogSink>();

    /// <summary>
    /// A factory that instantiates an <see cref="ICommandHandler"/>.
    /// </summary>
    public Func<IServiceProvider, ICommandHandler> DispatchPipelineCommandHandler { get; set; } = sp => sp.GetRequiredService<DispatchPipelineCommandHandler>();

    /// <summary>
    /// A factory that instantiates an <see cref="IPipelineResumer"/>.
    /// </summary>
    public Func<IServiceProvider, IPipelineResumer> PipelineResumer { get; set; } = sp => sp.GetRequiredService<PipelineResumer>();

    /// <summary>
    /// A factory that instantiates an <see cref="IBookmarkQueueWorker"/>.
    /// </summary>
    public Func<IServiceProvider, IBookmarkQueueWorker> BookmarkQueueWorker { get; set; } = sp => sp.GetRequiredService<BookmarkQueueWorker>();

    /// <summary>
    /// A delegate to configure the <see cref="DistributedLockingOptions"/>.
    /// </summary>
    public Action<DistributedLockingOptions> DistributedLockingOptions { get; set; } = _ => { };

    /// <summary>
    /// A delegate to configure the <see cref="PipelineInboxCleanupOptions"/>.
    /// </summary>
    [Obsolete("Will be removed in a future version")]
    public Action<PipelineInboxCleanupOptions> PipelineInboxCleanupOptions { get; set; } = _ => { };

    /// <summary>
    /// A delegate to configure the <see cref="PipelineDispatcherOptions"/>.
    /// </summary>
    public Action<PipelineDispatcherOptions> PipelineDispatcherOptions { get; set; } = _ => { };
    
    /// <summary>
    /// A delegate to configure the <see cref="BookmarkQueuePurgeOptions"/>.
    /// </summary>
    public Action<BookmarkQueuePurgeOptions> BookmarkQueuePurgeOptions { get; set; } = _ => { };

    /// <summary>
    /// Enables the pipeline inbox cleanup job. 
    /// </summary>
    public PipelineRuntimeFeature EnablePipelineInboxCleanupJob()
    {
        Services.Configure<PipelineInboxCleanupOptions>(options => { options.IsEnabled = true; });
        return this;
    }

    /// <summary>
    /// Disables the pipeline inbox cleanup job.
    /// </summary>
    public PipelineRuntimeFeature DisablePipelineInboxCleanupJob()
    {
        Services.Configure<PipelineInboxCleanupOptions>(options => { options.IsEnabled = false; });
        return this;
    }

    /// <summary>
    /// Register the specified pipeline type.
    /// </summary>
    public PipelineRuntimeFeature AddPipeline<T>() where T : IPipeline
    {
        Pipelines.Add<T>();
        return this;
    }

    /// <summary>
    /// Register all pipelines in the specified assembly.
    /// </summary>
    [RequiresUnreferencedCode("The assembly is required to be referenced.")]
    public PipelineRuntimeFeature AddPipelinesFrom(Assembly assembly)
    {
        var pipelineTypes = assembly.GetExportedTypes()
            .Where(x => typeof(IPipeline).IsAssignableFrom(x) && x is { IsAbstract: false, IsInterface: false, IsGenericType: false })
            .ToList();

        foreach (var pipelineType in pipelineTypes)
            Pipelines.Add(pipelineType);

        return this;
    }

    /// <summary>
    /// Adds a dispatcher channel.
    /// </summary>
    public PipelineRuntimeFeature AddDispatcherChannel(string channel)
    {
        return AddDispatcherChannel(new DispatcherChannel
        {
            Name = channel
        });
    }

    /// <summary>
    /// Adds a dispatcher channel.
    /// </summary>
    public PipelineRuntimeFeature AddDispatcherChannel(DispatcherChannel channel)
    {
        PipelineDispatcherChannels[channel.Name] = channel;
        return this;
    }

    /// <inheritdoc />
    public override void Configure()
    {
        Module.AddStepsFrom<PipelineRuntimeFeature>();
        Module.Configure<PipelinesFeature>(pipelines =>
        {
            pipelines.CommitStateHandler = sp => sp.GetRequiredService<DefaultCommitStateHandler>();
        });

        Services.Configure<RecurringTaskOptions>(options =>
        {
            options.Schedule.ConfigureTask<TriggerBookmarkQueueRecurringTask>(TimeSpan.FromSeconds(10));
        });
    }

    /// <inheritdoc />
    public override void Apply()
    {
        // Options.
        Services.Configure(DistributedLockingOptions);
        Services.Configure(PipelineInboxCleanupOptions);
        Services.Configure(PipelineDispatcherOptions);
        Services.Configure(BookmarkQueuePurgeOptions);
        Services.Configure<RuntimeOptions>(options => { options.Pipelines = Pipelines; });
        Services.Configure<PipelineDispatcherOptions>(options =>
        {
            options.Channels.AddRange(PipelineDispatcherChannels.Values);
        });

        Services
            // Core.
            .AddScoped<ITriggerIndexer, TriggerIndexer>()
            .AddScoped<IPipelineInstanceFactory, PipelineInstanceFactory>()
            .AddScoped<IPipelineHostFactory, PipelineHostFactory>()
            .AddScoped<IBackgroundStepInvoker, BackgroundStepInvoker>()
            .AddScoped(PipelineRuntime)
            .AddScoped(PipelineDispatcher)
            .AddScoped(StimulusDispatcher)
            .AddScoped(PipelineCancellationDispatcher)
            .AddScoped(RunTaskDispatcher)
            .AddScoped(StepExecutionLogSink)
            .AddScoped(PipelineExecutionLogSink)
            .AddSingleton(BackgroundStepScheduler)
            .AddSingleton<RandomLongIdentityGenerator>()
            .AddSingleton<IBookmarkQueueSignaler, BookmarkQueueSignaler>()
            .AddScoped(BookmarkQueueWorker)
            .AddScoped<IBookmarkManager, DefaultBookmarkManager>()
            .AddScoped<IStepExecutionManager, DefaultStepExecutionManager>()
            .AddScoped<IStepExecutionStatsService, StepExecutionStatsService>()
            .AddScoped<IStepExecutionMapper, DefaultStepExecutionMapper>()
            .AddScoped<IPipelineDefinitionStorePopulator, DefaultPipelineDefinitionStorePopulator>()
            .AddScoped<IRegistriesPopulator, DefaultRegistriesPopulator>()
            .AddScoped<IPipelineDefinitionsRefresher, PipelineDefinitionsRefresher>()
            .AddScoped<IPipelineDefinitionsReloader, PipelineDefinitionsReloader>()
            .AddScoped<IPipelineRegistry, DefaultPipelineRegistry>()
            .AddScoped<IPipelineMatcher, PipelineMatcher>()
            .AddScoped<IPipelineInvoker, PipelineInvoker>()
            .AddScoped<IStimulusSender, StimulusSender>()
            .AddScoped<ITriggerBoundPipelineService, TriggerBoundPipelineService>()
            .AddScoped<IBookmarkBoundPipelineService, BookmarkBoundPipelineService>()
            .AddScoped<ITaskReporter, TaskReporter>()
            .AddScoped<SynchronousTaskDispatcher>()
            .AddScoped<BackgroundTaskDispatcher>()
            .AddScoped<StoreStepExecutionLogSink>()
            .AddScoped<StorePipelineExecutionLogSink>()
            .AddScoped<DispatchPipelineCommandHandler>()
            .AddScoped<IEventPublisher, EventPublisher>()
            .AddScoped<IBookmarkUpdater, BookmarkUpdater>()
            .AddScoped<IBookmarksPersister, BookmarksPersister>()
            .AddScoped<IBookmarkResumer, BookmarkResumer>()
            .AddScoped<IBookmarkQueue, StoreBookmarkQueue>()
            .AddScoped(PipelineResumer)
            .AddScoped<PipelineResumer>()
            .AddScoped(BookmarkQueueWorker)
            .AddScoped<BookmarkQueueWorker>()
            .AddScoped<ITriggerInvoker, TriggerInvoker>()
            .AddScoped<IPipelineCanceler, PipelineCanceler>()
            .AddScoped<IPipelineCancellationService, PipelineCancellationService>()
            .AddScoped<IPipelineActivationStrategyEvaluator, DefaultPipelineActivationStrategyEvaluator>()
            .AddScoped<IPipelineStarter, DefaultPipelineStarter>()
            .AddScoped<IPipelineRestarter, DefaultPipelineRestarter>()
            .AddScoped<IBookmarkQueuePurger, DefaultBookmarkQueuePurger>()
            .AddScoped<ILogRecordExtractor<PipelineExecutionLogRecord>, PipelineExecutionLogRecordExtractor>()
            .AddScoped<IStepPropertyLogPersistenceEvaluator, StepPropertyLogPersistenceEvaluator>()
            .AddScoped<IBookmarkQueueProcessor, BookmarkQueueProcessor>()
            .AddScoped<DefaultCommitStateHandler>()
            .AddScoped<PipelineHeartbeatGeneratorFactory>()

            // Deprecated services.
            .AddScoped<IPipelineInbox, StimulusProxyPipelineInbox>()

            // Stores.
            .AddScoped(BookmarkStore)
            .AddScoped(BookmarkQueueStore)
            .AddScoped(TriggerStore)
            .AddScoped(PipelineExecutionLogStore)
            .AddScoped(StepExecutionLogStore)

            // Lazy services.
            .AddScoped<Func<IEnumerable<IPipelinesProvider>>>(sp => sp.GetServices<IPipelinesProvider>)
            .AddScoped<Func<IEnumerable<IPipelineMaterializer>>>(sp => sp.GetServices<IPipelineMaterializer>)

            // Noop stores.
            .AddScoped<MemoryPipelineExecutionLogStore>()
            .AddScoped<MemoryStepExecutionStore>()

            // Memory stores.
            .AddMemoryStore<StoredBookmark, MemoryBookmarkStore>()
            .AddMemoryStore<StoredTrigger, MemoryTriggerStore>()
            .AddMemoryStore<BookmarkQueueItem, MemoryBookmarkQueueStore>()
            .AddMemoryStore<PipelineExecutionLogRecord, MemoryPipelineExecutionLogStore>()
            .AddMemoryStore<StepExecutionRecord, MemoryStepExecutionStore>()
            
            // Startup tasks, background tasks, and recurring tasks.
            .AddStartupTask<PopulateRegistriesStartupTask>()
            .AddRecurringTask<TriggerBookmarkQueueRecurringTask>(TimeSpan.FromMinutes(1))
            .AddRecurringTask<PurgeBookmarkQueueRecurringTask>(TimeSpan.FromSeconds(10))
            .AddRecurringTask<RestartInterruptedPipelinesTask>(TimeSpan.FromMinutes(5)) // Same default as the pipeline liveness threshold.
            
            // Distributed locking.
            .AddSingleton(DistributedLockProvider)

            // Pipeline providers.
            .AddPipelinesProvider<ClrPipelinesProvider>()
            
            // UI property handlers.
            .AddScoped<IPropertyUIHandler, DispatcherChannelOptionsProvider>()

            // Domain handlers.
            .AddCommandHandler<DispatchPipelineCommandHandler>()
            .AddCommandHandler<DispatchStimulusCommandHandler>()
            .AddCommandHandler<CancelPipelinesCommandHandler>()
            .AddNotificationHandler<ResumeDispatchPipelineStep>()
            .AddNotificationHandler<ResumeBulkDispatchPipelineStep>()
            .AddNotificationHandler<ResumeExecutePipelineStep>()
            .AddNotificationHandler<IndexTriggers>()
            .AddNotificationHandler<CancelBackgroundSteps>()
            .AddNotificationHandler<DeleteBookmarks>()
            .AddNotificationHandler<DeleteTriggers>()
            .AddNotificationHandler<DeleteStepExecutionLogRecords>()
            .AddNotificationHandler<DeletePipelineExecutionLogRecords>()
            .AddNotificationHandler<RefreshStepRegistry>()
            .AddNotificationHandler<SignalBookmarkQueueWorker>()
            .AddNotificationHandler<EvaluateParentLogPersistenceModes>()
            .AddNotificationHandler<CaptureStepExecutionState>()
            .AddNotificationHandler<ValidatePipelineRequestHandler>()

            // Pipeline activation strategies.
            .AddScoped<IPipelineActivationStrategy, SingletonStrategy>()
            .AddScoped<IPipelineActivationStrategy, CorrelatedSingletonStrategy>()
            .AddScoped<IPipelineActivationStrategy, CorrelationStrategy>()
            ;
    }
}