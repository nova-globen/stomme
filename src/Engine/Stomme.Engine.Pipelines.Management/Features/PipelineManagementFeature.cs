using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Engine.Caching.Features;
using Stomme.Engine.Common.Codecs;
using Stomme.Engine.Common.Features;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.LogPersistence;
using Stomme.Engine.Pipelines.Management.Steps.HostMethod;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Handlers.Notifications;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Materializers;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Management.Providers;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Management.Stores;
using Stomme.Engine.Pipelines.Serialization.Serializers;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Engine.Pipelines.Management.Features;

/// <summary>
/// Installs and configures the pipeline management feature.
/// </summary>
[DependsOn(typeof(StringCompressionFeature))]
[DependsOn(typeof(MediatorFeature))]
[DependsOn(typeof(MemoryCacheFeature))]
[DependsOn(typeof(SystemClockFeature))]
[DependsOn(typeof(PipelinesFeature))]
[DependsOn(typeof(PipelineDefinitionsFeature))]
[DependsOn(typeof(PipelineInstancesFeature))]
[UsedImplicitly]
public class PipelineManagementFeature(IModule module) : FeatureBase(module)
{
    private const string PrimitivesCategory = "Primitives";
    private const string LookupsCategory = "Lookups";
    private const string DynamicCategory = "Dynamic";
    private const string DataCategory = "Data";
    private const string SystemCategory = "System";

    private Func<IServiceProvider, IPipelineDefinitionPublisher> _pipelineDefinitionPublisher = sp => ActivatorUtilities.CreateInstance<PipelineDefinitionPublisher>(sp);
    private Func<IServiceProvider, IPipelineReferenceQuery> _pipelineReferenceQuery = sp => ActivatorUtilities.CreateInstance<DefaultPipelineReferenceQuery>(sp);

    private string CompressionAlgorithm { get; set; } = nameof(None);
    private LogPersistenceMode LogPersistenceMode { get; set; } = LogPersistenceMode.Include;
    private bool IsReadOnlyMode { get; set; }

    /// <summary>
    /// A set of step types to make available to the system. 
    /// </summary>
    public HashSet<Type> StepTypes { get; } = [];

    /// <summary>
    /// A set of variable types to make available to the system. 
    /// </summary>
    public HashSet<VariableDescriptor> VariableDescriptors { get; } =
    [
        new(typeof(object), PrimitivesCategory, "The root class for all object in the CLR System."),
        new(typeof(string), PrimitivesCategory, "Represents a static string of characters."),
        new(typeof(bool), PrimitivesCategory, "Represents a true or false value."),
        new(typeof(int), PrimitivesCategory, "A 32 bit integer."),
        new(typeof(long), PrimitivesCategory, "A 64 bit integer."),
        new(typeof(float), PrimitivesCategory, "A 32 bit floating point number."),
        new(typeof(double), PrimitivesCategory, "A 64 bit floating point number."),
        new(typeof(decimal), PrimitivesCategory, "A decimal number."),
        new(typeof(Guid), PrimitivesCategory, "Represents a Globally Unique Identifier."),
        new(typeof(DateTime), PrimitivesCategory, "A value type that represents a date and time."),
        new(typeof(DateTimeOffset), PrimitivesCategory, "A value type that consists of a DateTime and a time zone offset."),
        new(typeof(TimeSpan), PrimitivesCategory, "Represents a duration of time."),
        new(typeof(IDictionary<string, string>), LookupsCategory, "A dictionary with string key and values."),
        new(typeof(IDictionary<string, object>), LookupsCategory, "A dictionary with string key and object values."),
        new(typeof(ExpandoObject), DynamicCategory, "A dictionary that can be typed as dynamic to access members using dot notation."),
        new(typeof(JsonElement), DynamicCategory, "A JSON element for reading a JSON structure."),
        new(typeof(JsonNode), DynamicCategory, "A JSON node for reading and writing a JSON structure."),
        new(typeof(JsonObject), DynamicCategory, "A JSON object for reading and writing a JSON structure."),
        new(typeof(byte[]), DataCategory, "A byte array."),
        new(typeof(Stream), DataCategory, "A stream."),
        new(typeof(LogPersistenceMode), SystemCategory, "A LogPersistenceMode enum value.")
    ];

    /// <summary>
    /// Adds the specified step type to the system.
    /// </summary>
    public PipelineManagementFeature AddStep<T>() where T : IStep => AddStep(typeof(T));

    /// <summary>
    /// Adds the specified step type to the system.
    /// </summary>
    public PipelineManagementFeature AddStep(Type stepType)
    {
        StepTypes.Add(stepType);
        return this;
    }

    /// <summary>
    /// Adds all types implementing <see cref="IStep"/> to the system.
    /// </summary>
    public PipelineManagementFeature AddStepsFrom<TMarker>()
    {
        var stepTypes = typeof(TMarker).Assembly.GetExportedTypes()
            .Where(x => typeof(IStep).IsAssignableFrom(x) && x is { IsAbstract: false, IsInterface: false, IsGenericType: false })
            .ToList();
        return AddSteps(stepTypes);
    }

    /// <summary>
    /// Adds the specified step types to the system.
    /// </summary>
    public PipelineManagementFeature AddSteps(IEnumerable<Type> stepTypes)
    {
        StepTypes.AddRange(stepTypes);
        return this;
    }

    /// <summary>
    /// Removes the specified step type from the system.
    /// </summary>
    public PipelineManagementFeature RemoveStep<T>() where T : IStep => RemoveStep(typeof(T));

    /// <summary>
    /// Adds the specified step type to the system.
    /// </summary>
    public PipelineManagementFeature RemoveStep(Type stepType)
    {
        StepTypes.Remove(stepType);
        return this;
    }

    /// <summary>
    /// Configures the system to add a specific step host type to the pipeline management feature.
    /// </summary>
    /// <typeparam name="T">The type of the step host to be added.</typeparam>
    /// <param name="key">An optional unique key to associate with the step host type.</param>
    public PipelineManagementFeature AddStepHost<T>(string? key = null) where T : class
    {
        Module.Services.Configure<HostMethodStepsOptions>(options => options.AddType<T>(key));
        return this;
    }
    
    /// <summary>
    /// Configures the system to add a specific step host type to the pipeline management feature.
    /// </summary>
    /// <param name="hostType">The type of the step host to be added.</param>
    /// <param name="key">An optional unique key to associate with the step host type.</param>
    public PipelineManagementFeature AddStepHost(Type hostType, string? key = null)
    {
        Module.Services.Configure<HostMethodStepsOptions>(options => options.AddType(hostType, key));
        return this;
    }

    /// <summary>
    /// Adds the specified variable type to the system.
    /// </summary>
    public PipelineManagementFeature AddVariableType<T>(string category) => AddVariableType(typeof(T), category);

    /// <summary>
    /// Adds the specified variable type to the system.
    /// </summary>
    public PipelineManagementFeature AddVariableType(Type type, string category) => AddVariableTypes([type], category);

    /// <summary>
    /// Adds the specified variable types to the system.
    /// </summary>
    public PipelineManagementFeature AddVariableTypes(IEnumerable<Type> types, string category) =>
        AddVariableTypes(types.Select(x => new VariableDescriptor(x, category, x.GetCustomAttribute<DescriptionAttribute>()?.Description)));

    /// <summary>
    /// Adds the specified variable types to the system.
    /// </summary>
    public PipelineManagementFeature AddVariableTypes(IEnumerable<VariableDescriptor> descriptors)
    {
        VariableDescriptors.AddRange(descriptors);
        return this;
    }

    /// <summary>
    /// Sets the compression algorithm to use for compressing pipeline state.
    /// </summary>
    public PipelineManagementFeature SetCompressionAlgorithm(string algorithm)
    {
        CompressionAlgorithm = algorithm;
        return this;
    }

    /// <summary>
    /// Set the default Log Persistence mode to use for worflow state (default is Include)
    /// </summary>
    /// <param name="logPersistenceMode">The mode persistence value</param>
    public PipelineManagementFeature SetDefaultLogPersistenceMode(LogPersistenceMode logPersistenceMode)
    {
        LogPersistenceMode = logPersistenceMode;
        return this;
    }

    /// <summary>
    /// Enables or disables read-only mode for resources such as pipeline definitions.
    /// </summary>
    /// <returns></returns>
    public PipelineManagementFeature UseReadOnlyMode(bool enabled)
    {
        IsReadOnlyMode = enabled;
        return this;
    }

    public PipelineManagementFeature UsePipelineDefinitionPublisher(Func<IServiceProvider, IPipelineDefinitionPublisher> pipelineDefinitionPublisher)
    {
        _pipelineDefinitionPublisher = pipelineDefinitionPublisher;
        return this;
    }
    
    public PipelineManagementFeature UsePipelineReferenceFinder<T>() where T : class, IPipelineReferenceQuery
    {
        Services.TryAddScoped<T>();
        return UsePipelineReferenceFinder(sp => sp.GetRequiredService<T>());
    }
    
    public PipelineManagementFeature UsePipelineReferenceFinder(Func<IServiceProvider, IPipelineReferenceQuery> pipelineReferenceFinder)
    {
        _pipelineReferenceQuery = pipelineReferenceFinder;
        return this;
    }

    /// <summary>
    /// Configures the pipeline reference graph builder options.
    /// </summary>
    /// <param name="configure">A delegate to configure the options.</param>
    public PipelineManagementFeature ConfigurePipelineReferenceGraph(Action<PipelineReferenceGraphOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The assembly containing the specified marker type will be scanned for step types.")]
    public override void Configure()
    {
        AddStepsFrom<PipelineManagementFeature>();
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services
             .AddMemoryStore<PipelineDefinition, MemoryPipelineDefinitionStore>()
             .AddMemoryStore<PipelineInstance, MemoryPipelineInstanceStore>()
             .AddStepProvider<TypedStepProvider>()
             .AddStepProvider<PipelineDefinitionStepProvider>()
             .AddStepProvider<HostMethodStepProvider>()
             .AddScoped<IHostMethodStepDescriber, HostMethodStepDescriber>()
             .AddScoped<IHostMethodParameterValueProvider, DefaultHostMethodParameterValueProvider>()
             .AddScoped<PipelineDefinitionStepDescriptorFactory>()
             .AddScoped<PipelineDefinitionStepProvider>()
             .AddScoped<IPipelineDefinitionStepRegistryUpdater, PipelineDefinitionStepRegistryUpdater>()
             .AddScoped<IMaterializerRegistry, MaterializerRegistry>()
             .AddScoped<IPipelineDefinitionService, PipelineDefinitionService>()
             .AddScoped<IPipelineSerializer, PipelineSerializer>()
             .AddScoped<IPipelineValidator, PipelineValidator>()
             .AddScoped(_pipelineReferenceQuery)
             .AddScoped<IPipelineReferenceGraphBuilder, PipelineReferenceGraphBuilder>()
             .AddScoped(_pipelineDefinitionPublisher)
             .AddScoped<IPipelineDefinitionImporter, PipelineDefinitionImporter>()
             .AddScoped<IPipelineDefinitionExporter, PipelineDefinitionExporter>()
             .AddSingleton<IFileNameSanitizer, DefaultFileNameSanitizer>()
             .AddScoped<IPipelineDefinitionManager, PipelineDefinitionManager>()
             .AddScoped<IPipelineInstanceManager, PipelineInstanceManager>()
             .AddScoped<IPipelineReferenceUpdater, PipelineReferenceUpdater>()
             .AddScoped<IStepRegistryPopulator, StepRegistryPopulator>()
             .AddSingleton<IExpressionDescriptorRegistry, ExpressionDescriptorRegistry>()
             .AddSingleton<IExpressionDescriptorProvider, DefaultExpressionDescriptorProvider>()
             .AddSerializationOptionsConfigurator<SerializationOptionsConfigurator>()
             .AddScoped<IPipelineMaterializer, TypedPipelineMaterializer>()
             .AddScoped<IPipelineMaterializer, ClrPipelineMaterializer>()
             .AddScoped<IPipelineMaterializer, JsonPipelineMaterializer>()
             .AddScoped<IStepResolver, PipelineDefinitionStepResolver>()
             .AddScoped<IPipelineInstanceVariableManager, PipelineInstanceVariableManager>()
             .AddScoped<PipelineDefinitionMapper>()
             .AddSingleton<VariableDefinitionMapper>()
             .AddSingleton<PipelineStateMapper>()
             ;

        Services
            .AddNotificationHandler<DeletePipelineInstances>()
            .AddNotificationHandler<RefreshStepRegistry>()
            .AddNotificationHandler<UpdateConsumingPipelines>()
            .AddNotificationHandler<ValidatePipeline>()
            ;

        Services.Configure<ManagementOptions>(options =>
        {
            foreach (var stepType in StepTypes.Distinct())
                options.StepTypes.Add(stepType);

            foreach (var descriptor in VariableDescriptors.DistinctBy(x => x.Type))
                options.VariableDescriptors.Add(descriptor);

            options.CompressionAlgorithm = CompressionAlgorithm;
            options.LogPersistenceMode = LogPersistenceMode;
            options.IsReadOnlyMode = IsReadOnlyMode;
        });

        Services.Configure<HostMethodStepsOptions>(_ => { });
        Services.Configure<PipelineReferenceGraphOptions>(_ => { });
    }
}