using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Management.Providers;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.PortResolvers;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Stomme.Engine.Testing.Shared;

/// <summary>
/// A test fixture for unit testing steps in isolation.
/// Provides a fluent API to configure services, variables, and execution context.
/// </summary>
public class StepTestFixture
{
    private Action<StepExecutionContext>? _configureContextAction;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepTestFixture"/> class.
    /// </summary>
    /// <param name="step">The step to test</param>
    public StepTestFixture(IStep step)
    {
        Step = step;
        Services = new ServiceCollection();
        AddCorePipelineServices(Services);
    }

    /// <summary>
    /// Represents the step being tested within the context of the step test fixture.
    /// Provides access to the step for configuration, execution, and validation purposes.
    /// </summary>
    public IStep Step { get; }

    /// <summary>
    /// Gets the service collection for registering additional services.
    /// Use this to add services required by the step under test.
    /// </summary>
    [UsedImplicitly]
    public IServiceCollection Services { get; private set; }

    /// <summary>
    /// Configures the service collection using a fluent action.
    /// </summary>
    /// <param name="configure">Action to configure the service collection</param>
    /// <returns>The fixture instance for method chaining</returns>
    public StepTestFixture ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(Services);
        return this;
    }

    /// <summary>
    /// Configures the step execution context before execution.
    /// Multiple calls to this method will chain the configuration actions together.
    /// </summary>
    /// <param name="configure">Action to configure the step execution context</param>
    /// <returns>The fixture instance for method chaining</returns>
    [UsedImplicitly]
    public StepTestFixture ConfigureContext(Action<StepExecutionContext> configure)
    {
        _configureContextAction += configure;
        return this;
    }

    /// <summary>
    /// Executes the step and returns the execution context.
    /// </summary>
    /// <returns>The StepExecutionContext after execution</returns>
    public async Task<StepExecutionContext> ExecuteAsync()
    {
        var context = await BuildAsync();
        return await ExecuteAsync(context);
    }
    
    /// <summary>
    /// Executes the step using a pre-built <see cref="StepExecutionContext"/>.
    /// Useful when you need to customize the context before execution, such as setting initial pipeline state or overriding correlation IDs.
    /// </summary>
    /// <param name="context">The pre-built context to execute</param>
    /// <returns>The <see cref="StepExecutionContext"/> after execution</returns>
    public async Task<StepExecutionContext> ExecuteAsync(StepExecutionContext context)
    {
        // Set up variables and inputs, then execute the step
        await SetupExistingVariablesAsync(Step, context);
        await context.EvaluateInputPropertiesAsync();
        context.TransitionTo(StepStatus.Running);
        await Step.ExecuteAsync(context);

        return context;
    }

    /// <summary>
    /// Builds the StepExecutionContext without executing the step.
    /// </summary>
    public async Task<StepExecutionContext> BuildAsync()
    {
        var serviceProvider = Services.BuildServiceProvider();
        var stepRegistry = serviceProvider.GetRequiredService<IStepRegistry>();
        var pipelineGraphBuilder = serviceProvider.GetRequiredService<IPipelineGraphBuilder>();

        await stepRegistry.RegisterAsync(Step.GetType());

        var pipeline = Pipeline.FromStep(Step);
        var pipelineGraph = await pipelineGraphBuilder.BuildAsync(pipeline);

        // Create pipeline execution context using the static factory method
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(
            serviceProvider,
            pipelineGraph,
            $"test-instance-{Guid.NewGuid()}",
            CancellationToken.None
        );

        // Create StepExecutionContext for the actual step we want to test
        var context = await pipelineExecutionContext.CreateStepExecutionContextAsync(Step);

        // Apply any context configuration action
        _configureContextAction?.Invoke(context);

        return context;
    }

    /// <summary>
    /// Sets up existing variables found on the step in the execution context.
    /// This is necessary because in unit tests, variables need to be initialized.
    /// </summary>
    private static Task SetupExistingVariablesAsync(IStep step, StepExecutionContext context)
    {
        var stepType = step.GetType();
        var variableProperties = stepType.GetProperties()
            .Where(p => typeof(Variable).IsAssignableFrom(p.PropertyType))
            .ToList();

        foreach (var variable in variableProperties.Select(property => (Variable?)property.GetValue(step)))
        {
            if(variable == null)
                continue;
            
            context.PipelineExecutionContext.MemoryRegister.Declare(variable);
            variable.Set(context.ExpressionExecutionContext, variable.Value);
        }

        return Task.CompletedTask;
    }

    private static void AddCorePipelineServices(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<ISystemClock>(_ => Substitute.For<ISystemClock>());
        services.AddSingleton<INotificationSender>(_ => Substitute.For<INotificationSender>());
        services.AddSingleton<IStepVisitor, StepVisitor>();
        services.AddScoped<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<IWellKnownTypeRegistry, WellKnownTypeRegistry>();
        services.AddSingleton<IStepDescriber, StepDescriber>();
        services.AddSingleton<IPropertyDefaultValueResolver, PropertyDefaultValueResolver>();
        services.AddSingleton<IPropertyUIHandlerResolver, PropertyUIHandlerResolver>();
        services.AddSingleton<IStepRegistry, StepRegistry>();
        services.AddScoped<IStepRegistryLookupService, StepRegistryLookupService>();
        services.AddScoped<IIdentityGraphService, IdentityGraphService>();
        services.AddScoped<IPipelineGraphBuilder, PipelineGraphBuilder>();
        services.AddScoped<IStepResolver, PropertyBasedStepResolver>();
        services.AddScoped<IStepResolver, SwitchStepResolver>();
        services.AddScoped<DefaultStepInputEvaluator>();
        services.AddSingleton<IExpressionDescriptorProvider, DefaultExpressionDescriptorProvider>();
        services.AddSingleton<IExpressionDescriptorRegistry, ExpressionDescriptorRegistry>();
        services.AddSingleton<IIdentityGenerator>(_ => Substitute.For<IIdentityGenerator>());
        services.AddSingleton<IHasher>(_ => Substitute.For<IHasher>());
        services.AddSingleton<IStimulusHasher, StimulusHasher>();
        services.AddSingleton<ICommitStateHandler>(_ => Substitute.For<ICommitStateHandler>());
        services.AddSingleton<IStepSchedulerFactory, StepSchedulerFactory>();
        services.AddSingleton<IPipelineExecutionContextSchedulerStrategy, FakePipelineExecutionContextSchedulerStrategy>();
        services.AddSingleton<IStepExecutionContextSchedulerStrategy, FakeStepExecutionContextSchedulerStrategy>();
        services.AddSingleton<ITenantAccessor, DefaultTenantAccessor>();
        services.AddScoped<IPipelineStateExtractor, PipelineStateExtractor>();
    }
}