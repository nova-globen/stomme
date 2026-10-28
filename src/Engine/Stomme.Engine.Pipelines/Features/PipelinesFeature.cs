using Stomme.Engine.Common;
using Stomme.Engine.Common.Features;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Expressions.Features;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.ActivationValidators;
using Stomme.Engine.Pipelines.Builders;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.IncidentStrategies;
using Stomme.Engine.Pipelines.LogPersistence;
using Stomme.Engine.Pipelines.LogPersistence.Strategies;
using Stomme.Engine.Pipelines.Middleware.Steps;
using Stomme.Engine.Pipelines.Middleware.Pipelines;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Stomme.Engine.Pipelines.PortResolvers;
using Stomme.Engine.Pipelines.Serialization.Configurators;
using Stomme.Engine.Pipelines.Serialization.Helpers;
using Stomme.Engine.Pipelines.Serialization.Serializers;
using Stomme.Engine.Pipelines.Services;
using Stomme.Engine.Pipelines.UIHints.CheckList;
using Stomme.Engine.Pipelines.UIHints.Dictionary;
using Stomme.Engine.Pipelines.UIHints.Dropdown;
using Stomme.Engine.Pipelines.UIHints.JsonEditor;
using Stomme.Engine.Pipelines.UIHints.RadioList;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Features;

/// <summary>
/// Adds pipeline services to the system.
/// </summary>
[DependsOn(typeof(SystemClockFeature))]
[DependsOn(typeof(ExpressionsFeature))]
[DependsOn(typeof(MediatorFeature))]
[DependsOn(typeof(DefaultFormattersFeature))]
[DependsOn(typeof(MultitenancyFeature))]
[DependsOn(typeof(CommitStrategiesFeature))]
public class PipelinesFeature : FeatureBase
{
    /// <inheritdoc />
    public PipelinesFeature(IModule module) : base(module)
    {
    }

    /// <summary>
    /// A factory that instantiates a concrete <see cref="IStandardInStreamProvider"/>.
    /// </summary>
    public Func<IServiceProvider, IStandardInStreamProvider> StandardInStreamProvider { get; set; } = _ => new StandardInStreamProvider(Console.In);

    /// <summary>
    /// A factory that instantiates a concrete <see cref="IStandardOutStreamProvider"/>.
    /// </summary>
    public Func<IServiceProvider, IStandardOutStreamProvider> StandardOutStreamProvider { get; set; } = _ => new StandardOutStreamProvider(Console.Out);

    /// <summary>
    /// A factory that instantiates a concrete <see cref="IIdentityGenerator"/>.
    /// </summary>
    public Func<IServiceProvider, IIdentityGenerator> IdentityGenerator { get; set; } = sp => new RandomLongIdentityGenerator();

    /// <summary>
    /// A handler for committing pipeline execution state.
    /// </summary>
    public Func<IServiceProvider, ICommitStateHandler> CommitStateHandler { get; set; } = sp => new NoopCommitStateHandler();

    /// <summary>
    /// A factory that instantiates a concrete <see cref="ILoggerStateGenerator{PipelineExecutionContext}"/>.
    /// </summary>
    public Func<IServiceProvider, ILoggerStateGenerator<PipelineExecutionContext>> PipelineLoggerStateGenerator { get; set; } = sp => new PipelineLoggerStateGenerator();

    /// <summary>
    /// A factory that instantiates a concrete <see cref="ILoggerStateGenerator{StepExecutionContext}"/>.
    /// </summary>
    public Func<IServiceProvider, ILoggerStateGenerator<StepExecutionContext>> StepLoggerStateGenerator { get; set; } = sp => new StepLoggerStateGenerator();

    /// <summary>
    /// A delegate to configure the <see cref="IPipelineExecutionPipeline"/>.
    /// </summary>
    public Action<IPipelineExecutionPipelineBuilder> PipelineExecutionPipeline { get; set; } = builder => builder
        .UseExceptionHandling()
        .UseDefaultStepScheduler();

    /// <summary>
    /// A delegate to configure the <see cref="IStepExecutionPipeline"/>.
    /// </summary>
    public Action<IStepExecutionPipelineBuilder> StepExecutionPipeline { get; set; } = builder => builder.UseDefaultStepInvoker();

    /// <summary>
    /// Fluent method to set <see cref="StandardInStreamProvider"/>.
    /// </summary>
    public PipelinesFeature WithStandardInStreamProvider(Func<IServiceProvider, IStandardInStreamProvider> provider)
    {
        StandardInStreamProvider = provider;
        return this;
    }

    /// <summary>
    /// Fluent method to set <see cref="StandardOutStreamProvider"/>.
    /// </summary>
    public PipelinesFeature WithStandardOutStreamProvider(Func<IServiceProvider, IStandardOutStreamProvider> provider)
    {
        StandardOutStreamProvider = provider;
        return this;
    }

    /// <summary>
    /// Fluent method to set <see cref="IdentityGenerator"/>.
    /// </summary>
    public PipelinesFeature WithIdentityGenerator(Func<IServiceProvider, IIdentityGenerator> generator)
    {
        IdentityGenerator = generator;
        return this;
    }

    /// <summary>
    /// Fluent method to set <see cref="ILoggerStateGenerator{PipelineExecutionContext}"/>.
    /// </summary>
    public PipelinesFeature WithPipelineLoggerStateGenerator(Func<IServiceProvider, ILoggerStateGenerator<PipelineExecutionContext>> generator)
    {
        PipelineLoggerStateGenerator = generator;
        return this;
    }

    /// <summary>
    /// Fluent method to set <see cref="ILoggerStateGenerator{StepExecutionContext}"/>.
    /// </summary>
    public PipelinesFeature WithStepLoggerStateGenerator(Func<IServiceProvider, ILoggerStateGenerator<StepExecutionContext>> generator)
    {
        StepLoggerStateGenerator = generator;
        return this;
    }

    /// <summary>
    /// Fluent method to configure the <see cref="IPipelineExecutionPipeline"/>.
    /// </summary>
    public PipelinesFeature WithPipelineExecutionPipeline(Action<IPipelineExecutionPipelineBuilder> setup)
    {
        PipelineExecutionPipeline = setup;
        return this;
    }

    /// <summary>
    /// Fluent method to configure the <see cref="IStepExecutionPipeline"/>.
    /// </summary>
    public PipelinesFeature WithStepExecutionPipeline(Action<IStepExecutionPipelineBuilder> setup)
    {
        StepExecutionPipeline = setup;
        return this;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        AddEngineCore(Services);
    }

    private void AddEngineCore(IServiceCollection services)
    {
        services

            // Core.
            .AddScoped<IStepInvoker, StepInvoker>()
            .AddScoped<IPipelineRunner, PipelineRunner>()
            .AddScoped<IStepTestRunner, StepTestRunner>()
            .AddScoped<IStepVisitor, StepVisitor>()
            .AddScoped<IIdentityGraphService, IdentityGraphService>()
            .AddScoped<IPipelineGraphBuilder, PipelineGraphBuilder>()
            .AddScoped<IPipelineStateExtractor, PipelineStateExtractor>()
            .AddScoped<IStepSchedulerFactory, StepSchedulerFactory>()
            .AddSingleton<IPipelineExecutionContextSchedulerStrategy, PipelineExecutionContextSchedulerStrategy>()
            .AddSingleton<IStepExecutionContextSchedulerStrategy, StepExecutionContextSchedulerStrategy>()
            .AddScoped(CommitStateHandler)
            .AddSingleton<IHasher, Hasher>()
            .AddSingleton<IStimulusHasher, StimulusHasher>()
            .AddSingleton(IdentityGenerator)
            .AddSingleton<IBookmarkPayloadSerializer>(sp => ActivatorUtilities.CreateInstance<BookmarkPayloadSerializer>(sp))
            .AddSingleton<IStepDescriber, StepDescriber>()
            .AddSingleton<IStepRegistry, StepRegistry>()
            .AddScoped<IStepRegistryLookupService, StepRegistryLookupService>()
            .AddSingleton<IPropertyDefaultValueResolver, PropertyDefaultValueResolver>()
            .AddSingleton<IPropertyUIHandlerResolver, PropertyUIHandlerResolver>()
            .AddSingleton<IStepFactory, StepFactory>()
            .AddTransient<PipelineBuilder>()
            .AddScoped(typeof(Func<IPipelineBuilder>), sp => () => sp.GetRequiredService<PipelineBuilder>())
            .AddScoped<IPipelineBuilderFactory, PipelineBuilderFactory>()
            .AddScoped<IVariablePersistenceManager, VariablePersistenceManager>()
            .AddScoped<IIncidentStrategyResolver, DefaultIncidentStrategyResolver>()
            .AddScoped<IStepStateFilterManager, DefaultStepStateFilterManager>()
            .AddScoped<IPipelineInstanceVariableReader, DefaultPipelineInstanceVariableReader>()
            .AddScoped<IPipelineInstanceVariableWriter, DefaultPipelineInstanceVariableWriter>()
            .AddScoped<DefaultStepInputEvaluator>()

            // Incident Strategies.
            .AddTransient<IIncidentStrategy, FaultStrategy>()
            .AddTransient<IIncidentStrategy, ContinueWithIncidentsStrategy>()

            // Pipelines.
            .AddScoped<IStepExecutionPipeline>(sp => new StepExecutionPipeline(sp, StepExecutionPipeline))
            .AddScoped<IPipelineExecutionPipeline>(sp => new PipelineExecutionPipeline(sp, PipelineExecutionPipeline))

            // Built-in step services.
            .AddScoped<IStepResolver, PropertyBasedStepResolver>()
            .AddScoped<IStepResolver, SwitchStepResolver>()
            .AddSerializationOptionsConfigurator<AdditionalConvertersConfigurator>()
            .AddSerializationOptionsConfigurator<CustomConstructorConfigurator>()

            // Domain event handlers.
            .AddHandlersFrom<PipelinesFeature>()

            // Stream providers.
            .AddScoped(StandardInStreamProvider)
            .AddScoped(StandardOutStreamProvider)

            // Storage drivers.
            .AddScoped<IStorageDriverManager, StorageDriverManager>()
            .AddStorageDriver<PipelineStorageDriver>()
            .AddStorageDriver<PipelineInstanceStorageDriver>()
            .AddStorageDriver<MemoryStorageDriver>()

            // Serialization.
            .AddSingleton<IPipelineStateSerializer, JsonPipelineStateSerializer>()
            .AddSingleton<IPayloadSerializer, JsonPayloadSerializer>()
            .AddSingleton<IStepSerializer, JsonStepSerializer>()
            .AddSingleton<IApiSerializer, ApiSerializer>()
            .AddSingleton<ISafeSerializer, SafeSerializer>()
            .AddSingleton<IJsonSerializer, StandardJsonSerializer>()
            .AddSingleton<SyntheticPropertiesWriter>()
            .AddSingleton<StepWriter>()

            // Instantiation strategies.
            .AddScoped<IPipelineActivationStrategy, AllowAlwaysStrategy>()

            // UI.
            .AddScoped<IUIHintHandler, DropDownUIHintHandler>()
            .AddScoped<IUIHintHandler, CheckListUIHintHandler>()
            .AddScoped<IUIHintHandler, RadioListUIHintHandler>()
            .AddScoped<IUIHintHandler, JsonEditorUIHintHandler>()
            .AddScoped<IPropertyUIHandler, StaticCheckListOptionsProvider>()
            .AddScoped<IPropertyUIHandler, StaticRadioListOptionsProvider>()
            .AddScoped<IPropertyUIHandler, StaticDropDownOptionsProvider>()
            .AddScoped<IPropertyUIHandler, JsonCodeOptionsProvider>()
            .AddScoped<DictionaryValueEvaluator>()
            .AddSingleton<IStepDescriptorModifier, DictionaryUIHintInputModifier>()

            // Logger state generators.
            .AddSingleton(PipelineLoggerStateGenerator)
            .AddSingleton(StepLoggerStateGenerator)

            // Log Persistence Strategies.
            .AddScoped<ILogPersistenceStrategyService, DefaultLogPersistenceStrategyService>()
            .AddScoped<ILogPersistenceStrategy, Include>()
            .AddScoped<ILogPersistenceStrategy, Exclude>()
            .AddScoped<ILogPersistenceStrategy, Inherit>()
            .AddScoped<ILogPersistenceStrategy, Configuration>()

            // Logging
            .AddLogging();
    }
}