using System.ComponentModel;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;

/// <summary>
/// Loads and executes an <see cref="PipelineDefinition"/>.
/// </summary>
[Browsable(false)]
public class PipelineDefinitionStep : Composite, IInitializable
{
    private bool IsInitialized => Root.Id != null!;

    /// <summary>
    /// The definition ID of the pipeline to schedule for execution.
    /// </summary>
    public string PipelineDefinitionId { get; set; } = default!;

    /// <summary>
    /// The specific version ID of the pipeline to schedule for execution. If not set, the <see cref="Version"/> number will be considered.
    /// </summary>
    public string? PipelineDefinitionVersionId { get; set; }

    /// <summary>
    /// The latest published version number set by the provider. Tooling uses this to let the user know that a newer version is available.
    /// </summary>
    public int LatestAvailablePublishedVersion { get; set; }

    /// <summary>
    /// The latest published version ID set by the provider. Tooling uses this to let the user know that a newer version is available.
    /// </summary>
    public string? LatestAvailablePublishedVersionId { get; set; }

    async ValueTask IInitializable.InitializeAsync(InitializationContext context)
    {
        // This is not just for efficiency but also a necessity to avoid potential race conditions.
        // Such conditions can occur when multiple threads are simultaneously creating consuming pipelines,
        // especially when cached pipelines are being updated during the graph construction process.
        if (IsInitialized)
            return;

        var serviceProvider = context.ServiceProvider;
        var cancellationToken = context.CancellationToken;

        // Find the pipeline definition and not the graph; the graph must be computed at runtime, since NodeIds will vary across graphs.
        var pipelineDefinition = await GetPipelineDefinitionAsync(serviceProvider, cancellationToken).ConfigureAwait(false);

        if (pipelineDefinition == null)
            throw new Exception($"Could not find pipeline definition with ID {PipelineDefinitionId}.");

        var stepDescriptor = await FindStepDescriptorAsync(serviceProvider).ConfigureAwait(false);

        if (stepDescriptor == null)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<PipelineDefinitionStep>>();
            logger.LogWarning("Could not find step descriptor for step type {StepType}", Type);
        }
        else
        {
            // Declare input and output variables.
            DeclareInputAsVariables(stepDescriptor, (_, variable) => Variables.Declare(variable));
            DeclareOutputAsVariables(stepDescriptor, (_, variable) => Variables.Declare(variable));
        }

        var pipelineDefinitionService = serviceProvider.GetRequiredService<IPipelineDefinitionService>();
        var pipelineGraph = await pipelineDefinitionService.MaterializePipelineAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);

        // Set the root step.
        Root = pipelineGraph.Pipeline;
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        await CopyInputOutputToVariablesAsync(context).ConfigureAwait(false);
        await context.ScheduleStepAsync(Root, OnChildCompletedAsync).ConfigureAwait(false);
    }

    private async ValueTask OnChildCompletedAsync(StepCompletedContext context)
    {
        var stepExecutionContext = context.TargetContext;

        // Do we have a "complete composite" signal that triggered the completion?
        var completeCompositeSignal = context.PipelineExecutionContext.TransientProperties.TryGetValue(nameof(CompleteCompositeSignal), out var signal) ? (CompleteCompositeSignal)signal : default;

        // If we do, make sure to remove it from the transient properties.
        if (completeCompositeSignal != null)
        {
            var logger = context.GetRequiredService<ILogger<PipelineDefinitionStep>>();
            logger.LogDebug("Received a complete composite signal and removing it from the transient properties");
            context.PipelineExecutionContext.TransientProperties.Remove(nameof(CompleteCompositeSignal));
        }

        // Copy any collected outputs into the synthetic properties.
        foreach (var outputDescriptor in stepExecutionContext.StepDescriptor.Outputs)
        {
            var output = (Output?)outputDescriptor.ValueGetter(stepExecutionContext.Step);
            // If direct output mapping is used, we can read the output value directly from the memory.
            var value = stepExecutionContext.Get(output) ?? stepExecutionContext.Get(outputDescriptor.Name);

            // Make sure to select a parent scope to avoid naming collisions between outputs defined on the current scope and outputs defined on parent scopes.
            var parentStepExecutionContext = stepExecutionContext.ParentStepExecutionContext?.GetAncestors()
                .Any(x => x.StepDescriptor.Outputs.Any(y => y.Name == outputDescriptor.Name)) == true
                ? stepExecutionContext.ParentStepExecutionContext ?? stepExecutionContext
                : stepExecutionContext;

            parentStepExecutionContext.Set(output, value, outputDescriptor.Name);
        }

        // Complete this step with the signal value.
        await stepExecutionContext.CompleteStepAsync(completeCompositeSignal?.Value).ConfigureAwait(false);
    }

    private async Task CopyInputOutputToVariablesAsync(StepExecutionContext context)
    {
        var serviceProvider = context.GetRequiredService<IServiceProvider>();
        var stepDescriptor = await FindStepDescriptorAsync(serviceProvider).ConfigureAwait(false);

        if (stepDescriptor == null)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<PipelineDefinitionStep>>();
            logger.LogWarning("Could not find step descriptor for step type {StepType}", Type);
            return;
        }

        DeclareInputAsVariables(stepDescriptor, (descriptor, variable) =>
        {
            var inputName = descriptor.Name;
            var input = SyntheticProperties.TryGetValue(inputName, out var inputValue) ? (Input?)inputValue : default;
            var evaluatedExpression = input != null ? context.Get(input.MemoryBlockReference()) : default;

            context.ExpressionExecutionContext.Memory.Declare(variable);
            variable.Set(context, evaluatedExpression);
        });

        DeclareOutputAsVariables(stepDescriptor, (_, variable) => context.ExpressionExecutionContext.Memory.Declare(variable));
    }

    private void DeclareInputAsVariables(StepDescriptor stepDescriptor, Action<InputDescriptor, Variable> configureVariable)
    {
        foreach (var inputDescriptor in stepDescriptor.Inputs)
        {
            var inputName = inputDescriptor.Name;
            var unsafeInputName = PropertyNameHelper.GetUnsafePropertyName(typeof(PipelineDefinitionStep), inputName);
            var variableType = typeof(Variable<>).MakeGenericType(inputDescriptor.Type);
            var variable = (Variable)Activator.CreateInstance(variableType)!;

            variable.Id = unsafeInputName;
            variable.Name = unsafeInputName;
            variable.StorageDriverType = inputDescriptor.StorageDriverType;

            configureVariable(inputDescriptor, variable);
        }
    }

    private void DeclareOutputAsVariables(StepDescriptor stepDescriptor, Action<OutputDescriptor, Variable> configureVariable)
    {
        foreach (var outputDescriptor in stepDescriptor.Outputs)
        {
            var outputName = outputDescriptor.Name;
            var unsafeOutputName = PropertyNameHelper.GetUnsafePropertyName(typeof(PipelineDefinitionStep), outputName);

            var variable = new Variable
            {
                Id = unsafeOutputName,
                Name = unsafeOutputName
            };

            configureVariable(outputDescriptor, variable);
        }
    }

    private async Task<PipelineDefinition?> GetPipelineDefinitionAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var pipelineDefinitionService = serviceProvider.GetRequiredService<IPipelineDefinitionService>();
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = PipelineDefinitionId
        };

        if (!string.IsNullOrWhiteSpace(PipelineDefinitionVersionId))
            filter.Id = PipelineDefinitionVersionId;
        else
            filter.VersionOptions = VersionOptions.SpecificVersion(Version);

        var pipelineDefinition =
            await pipelineDefinitionService.FindPipelineDefinitionAsync(filter, cancellationToken).ConfigureAwait(false)
            ?? (await pipelineDefinitionService.FindPipelineDefinitionAsync(new PipelineDefinitionFilter
                {
                    DefinitionId = PipelineDefinitionId,
                    VersionOptions = VersionOptions.Published
                }, cancellationToken).ConfigureAwait(false)
                ?? await pipelineDefinitionService.FindPipelineDefinitionAsync(new PipelineDefinitionFilter
                {
                    DefinitionId = PipelineDefinitionId,
                    VersionOptions = VersionOptions.Latest
                }, cancellationToken).ConfigureAwait(false));

        return pipelineDefinition;
    }

    private async Task<StepDescriptor?> FindStepDescriptorAsync(IServiceProvider serviceProvider)
    {
        var stepRegistryLookup = serviceProvider.GetRequiredService<IStepRegistryLookupService>();
        return await stepRegistryLookup.FindAsync(Type, Version).ConfigureAwait(false) ?? await stepRegistryLookup.FindAsync(Type).ConfigureAwait(false);
    }
}