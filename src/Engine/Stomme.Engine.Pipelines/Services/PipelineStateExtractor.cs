using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Services;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class PipelineStateExtractor(ILogger<PipelineStateExtractor> logger) : IPipelineStateExtractor
{
    /// <inheritdoc />
    public PipelineState Extract(PipelineExecutionContext pipelineExecutionContext)
    {
        var state = new PipelineState
        {
            Id = pipelineExecutionContext.Id,
            DefinitionId = pipelineExecutionContext.Pipeline.Identity.DefinitionId,
            DefinitionVersionId = pipelineExecutionContext.Pipeline.Identity.Id,
            DefinitionVersion = pipelineExecutionContext.Pipeline.Identity.Version,
            CorrelationId = pipelineExecutionContext.CorrelationId,
            Name = pipelineExecutionContext.Name,
            ParentPipelineInstanceId = pipelineExecutionContext.ParentPipelineInstanceId,
            Status = pipelineExecutionContext.Status,
            SubStatus = pipelineExecutionContext.SubStatus,
            IsExecuting = pipelineExecutionContext.IsExecuting,
            Bookmarks = pipelineExecutionContext.Bookmarks,
            ExecutionLogSequence = pipelineExecutionContext.ExecutionLogSequence,
            Input = GetPersistableInput(pipelineExecutionContext),
            Output = pipelineExecutionContext.Output,
            Incidents = pipelineExecutionContext.Incidents,
            IsSystem = pipelineExecutionContext.Pipeline.IsSystem,
            CreatedAt = pipelineExecutionContext.CreatedAt,
            UpdatedAt = pipelineExecutionContext.UpdatedAt,
            FinishedAt = pipelineExecutionContext.FinishedAt,
        };

        ExtractProperties(state, pipelineExecutionContext);
        ExtractActiveStepExecutionContexts(state, pipelineExecutionContext);
        ExtractCompletionCallbacks(state, pipelineExecutionContext);
        ExtractScheduledSteps(state, pipelineExecutionContext);

        return state;
    }

    /// <inheritdoc />
    public async Task<PipelineExecutionContext> ApplyAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState state)
    {
        pipelineExecutionContext.Id = state.Id;
        pipelineExecutionContext.CorrelationId = state.CorrelationId;
        pipelineExecutionContext.Name = state.Name;
        pipelineExecutionContext.ParentPipelineInstanceId = state.ParentPipelineInstanceId;
        pipelineExecutionContext.SubStatus = state.SubStatus;
        pipelineExecutionContext.IsExecuting = state.IsExecuting;
        pipelineExecutionContext.Bookmarks = state.Bookmarks;
        pipelineExecutionContext.Output = state.Output;
        pipelineExecutionContext.ExecutionLogSequence = state.ExecutionLogSequence;
        pipelineExecutionContext.CreatedAt = state.CreatedAt;
        pipelineExecutionContext.UpdatedAt = state.UpdatedAt;
        pipelineExecutionContext.FinishedAt = state.FinishedAt;
        ApplyInput(state, pipelineExecutionContext);
        ApplyProperties(state, pipelineExecutionContext);
        await ApplyStepExecutionContextsAsync(state, pipelineExecutionContext).ConfigureAwait(false);
        ApplyCompletionCallbacks(state, pipelineExecutionContext);
        ApplyScheduledSteps(state, pipelineExecutionContext);
        return pipelineExecutionContext;
    }

    private void ApplyInput(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        // Only add input from state if the input doesn't already exist on the pipeline execution context.
        foreach (var inputItem in state.Input)
            if (!pipelineExecutionContext.Input.ContainsKey(inputItem.Key))
                pipelineExecutionContext.Input.Add(inputItem.Key, inputItem.Value);
    }

    private IDictionary<string, object> GetPersistableInput(PipelineExecutionContext pipelineExecutionContext)
    {
        // TODO: This is a temporary solution. We need to find a better way to handle this.
        var persistableInput = pipelineExecutionContext.Pipeline.Inputs.Where(x => x.StorageDriverType == typeof(PipelineStorageDriver) || x.StorageDriverType == typeof(PipelineInstanceStorageDriver)).ToList();
        var input = pipelineExecutionContext.Input;
        var filteredInput = new Dictionary<string, object>();

        foreach (var inputDefinition in persistableInput)
        {
            if (input.TryGetValue(inputDefinition.Name, out var value))
                filteredInput.Add(inputDefinition.Name, value);
        }

        return filteredInput;
    }

    private void ExtractProperties(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        state.Properties = pipelineExecutionContext.Properties;
    }

    private void ApplyProperties(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        // Merge properties.
        foreach (var property in state.Properties)
            pipelineExecutionContext.Properties[property.Key] = property.Value;
    }

    private async Task ApplyStepExecutionContextsAsync(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        var stepExecutionContexts = (await Task.WhenAll(state.StepExecutionContexts.Select(async item => await CreateStepExecutionContextAsync(item).ConfigureAwait(false))).ConfigureAwait(false)).Where(x => x != null).Select(x => x!).ToList();

        var lookup = stepExecutionContexts.ToDictionary(x => x.Id);

        // Reconstruct hierarchy.
        foreach (var contextState in state.StepExecutionContexts.Where(x => !string.IsNullOrWhiteSpace(x.ParentContextId)))
        {
            var parentContextId = contextState.ParentContextId;
            if (parentContextId == null || !lookup.TryGetValue(parentContextId, out var parentContext))
            {
                logger.LogWarning("Parent context with ID '{ParentContextId}' not found for context with ID '{ContextId}'.", parentContextId, contextState.Id);
                continue; // Skip if parent context is not found.
            }
            
            var contextId = contextState.Id;

            if (lookup.TryGetValue(contextId, out var context))
            {
                context.ExpressionExecutionContext.ParentContext = parentContext.ExpressionExecutionContext;
                context.ParentStepExecutionContext = parentContext;
            }
        }

        // Assign root expression execution context.
        var rootStepExecutionContexts = stepExecutionContexts.Where(x => x.ExpressionExecutionContext.ParentContext == null);

        foreach (var rootStepExecutionContext in rootStepExecutionContexts)
            rootStepExecutionContext.ExpressionExecutionContext.ParentContext = pipelineExecutionContext.ExpressionExecutionContext;

        pipelineExecutionContext.StepExecutionContexts = stepExecutionContexts;
        return;

        async Task<StepExecutionContext?> CreateStepExecutionContextAsync(StepExecutionContextState stepExecutionContextState)
        {
            var step = pipelineExecutionContext.FindStepByNodeId(stepExecutionContextState.ScheduledStepNodeId);

            // Step can be null in case the pipeline instance was migrated to a newer version that no longer contains this step.
            if (step == null)
                return null;

            var properties = stepExecutionContextState.Properties;
            var metadata = stepExecutionContextState.Metadata;
            var stepExecutionContext = await pipelineExecutionContext.CreateStepExecutionContextAsync(step).ConfigureAwait(false);
            stepExecutionContext.Id = stepExecutionContextState.Id;
            stepExecutionContext.CallStackDepth = stepExecutionContextState.CallStackDepth;
            stepExecutionContext.Properties.Merge(properties);
            stepExecutionContext.Metadata.Merge(metadata);
            
            if(stepExecutionContextState.StepState != null)
                stepExecutionContext.StepState.Merge(stepExecutionContextState.StepState);

            stepExecutionContext.TransitionTo(stepExecutionContextState.Status);
            stepExecutionContext.IsExecuting = stepExecutionContextState.IsExecuting;
            stepExecutionContext.AggregateFaultCount = stepExecutionContextState.FaultCount;
            stepExecutionContext.StartedAt = stepExecutionContextState.StartedAt;
            stepExecutionContext.CompletedAt = stepExecutionContextState.CompletedAt;
            stepExecutionContext.Tag = stepExecutionContextState.Tag;
            stepExecutionContext.DynamicVariables = stepExecutionContextState.DynamicVariables;

            return stepExecutionContext;
        }
    }

    private static void ApplyCompletionCallbacks(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        foreach (var completionCallbackEntry in state.CompletionCallbacks)
        {
            var ownerStepExecutionContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == completionCallbackEntry.OwnerInstanceId);
            if (ownerStepExecutionContext != null)
            {
                var childNode = pipelineExecutionContext.FindNodeById(completionCallbackEntry.ChildNodeId);

                if (childNode == null)
                    continue;

                var callbackName = completionCallbackEntry.MethodName;
                var callbackDelegate = !string.IsNullOrEmpty(callbackName) ? ownerStepExecutionContext.Step.GetStepCompletionCallback(callbackName) : default;
                var tag = completionCallbackEntry.Tag;
                pipelineExecutionContext.AddCompletionCallback(ownerStepExecutionContext, childNode, callbackDelegate, tag);
            }
        }
    }

    private void ApplyScheduledSteps(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        foreach (var stepWorkItemState in state.ScheduledSteps)
        {
            var step = pipelineExecutionContext.FindStepByNodeId(stepWorkItemState.StepNodeId);

            if (step == null)
                continue;

            var ownerContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == stepWorkItemState.OwnerContextId);
            var existingStepExecutionContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == stepWorkItemState.ExistingStepExecutionContextId);
            var variables = stepWorkItemState.Variables;
            var input = stepWorkItemState.Input;
            var tag = stepWorkItemState.Tag;
            var workItem = new StepWorkItem(step, ownerContext, tag, variables, existingStepExecutionContext, input);
            pipelineExecutionContext.Scheduler.Schedule(workItem);
        }
    }

    private static void ExtractCompletionCallbacks(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        // Assert that all referenced owner contexts exist.
        var activeContexts = pipelineExecutionContext.GetActiveStepExecutionContexts().ToList();
        foreach (var completionCallback in pipelineExecutionContext.CompletionCallbacks)
        {
            var ownerContext = activeContexts.FirstOrDefault(x => x == completionCallback.Owner);

            if (ownerContext == null)
                throw new("Lost an owner context");
        }

        var completionCallbacks = pipelineExecutionContext.CompletionCallbacks.Select(x => new CompletionCallbackState(x.Owner.Id, x.Child.NodeId, x.CompletionCallback?.Method.Name, x.Tag));

        state.CompletionCallbacks = completionCallbacks.ToList();
    }

    private static void ExtractActiveStepExecutionContexts(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        StepExecutionContextState CreateStepExecutionContextState(StepExecutionContext stepExecutionContext)
        {
            var parentId = stepExecutionContext.ParentStepExecutionContext?.Id;

            if (parentId != null)
            {
                var parentContext = stepExecutionContext.PipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == parentId);

                if (parentContext == null)
                    throw new("We lost a context. This could indicate a bug in a parent step that completed before (some of) its child steps.");
            }

            var stepExecutionContextState = new StepExecutionContextState
            {
                Id = stepExecutionContext.Id,
                CallStackDepth = stepExecutionContext.CallStackDepth,
                ParentContextId = stepExecutionContext.ParentStepExecutionContext?.Id,
                ScheduledStepNodeId = stepExecutionContext.NodeId,
                OwnerStepNodeId = stepExecutionContext.ParentStepExecutionContext?.NodeId,
                Properties = stepExecutionContext.Properties,
                Metadata = stepExecutionContext.Metadata,
                StepState = stepExecutionContext.StepState,
                Status = stepExecutionContext.Status,
                IsExecuting = stepExecutionContext.IsExecuting,
                FaultCount = stepExecutionContext.AggregateFaultCount,
                StartedAt = stepExecutionContext.StartedAt,
                CompletedAt = stepExecutionContext.CompletedAt,
                Tag = stepExecutionContext.Tag,
                DynamicVariables = stepExecutionContext.DynamicVariables,
            };
            return stepExecutionContextState;
        }

        // Only persist non-completed contexts.
        state.StepExecutionContexts = pipelineExecutionContext.GetActiveStepExecutionContexts().Reverse().Select(CreateStepExecutionContextState).ToList();
    }

    private void ExtractScheduledSteps(PipelineState state, PipelineExecutionContext pipelineExecutionContext)
    {
        var scheduledSteps = pipelineExecutionContext
            .Scheduler.List()
            .Select(x => new StepWorkItemState
            {
                StepNodeId = x.Step.NodeId,
                OwnerContextId = x.Owner?.Id,
                Tag = x.Tag,
                Variables = x.Variables?.ToList(),
                ExistingStepExecutionContextId = x.ExistingStepExecutionContext?.Id,
                Input = x.Input,
            });

        state.ScheduledSteps = scheduledSteps.ToList();
    }
}
