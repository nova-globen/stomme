using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Memory;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class VariablePersistenceManager(IStorageDriverManager storageDriverManager, ILogger<VariablePersistenceManager> logger) : IVariablePersistenceManager
{
    /// <inheritdoc />
    public async Task LoadVariablesAsync(PipelineExecutionContext pipelineExecutionContext, IEnumerable<string>? excludeTags = null)
    {
        var cancellationToken = pipelineExecutionContext.CancellationToken;
        var contexts = pipelineExecutionContext.StepExecutionContexts.ToList();
        var excludeTagsList = excludeTags?.ToList();

        foreach (var context in contexts)
        {
            var variables = GetLocalVariables(context).ToList();

            foreach (var variable in variables)
            {
                context.ExpressionExecutionContext.Memory.Declare(variable);
                var storageDriverContext = new StorageDriverContext(context, variable, cancellationToken);
                var register = context.ExpressionExecutionContext.Memory;
                var block = EnsureBlock(register, variable);
                var metadata = (VariableBlockMetadata)block.Metadata!;
                var driver = storageDriverManager.Get(metadata.StorageDriverType!);

                block.Metadata = metadata with
                {
                    IsInitialized = true
                };

                if (driver == null)
                    continue;

                if (excludeTagsList != null && driver.Tags.Any(excludeTagsList.Contains))
                    continue;

                var id = GetStateId(variable);

                try
                {
                    var value = await driver.ReadAsync(id, storageDriverContext).ConfigureAwait(false);
                    if (value == null) continue;

                    register.Declare(variable);

                    if (!variable.TryParseValue(value, out var parsedValue))
                    {
                        logger.LogWarning("Failed to parse value for variable {VariableId} of type {VariableType} with value {Value}", variable.Id, variable.GetVariableType().FullName, value);

                        if (!ObjectConverter.StrictMode)
                            variable.Set(register, value);
                        continue;
                    }

                    variable.Set(register, parsedValue);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to read variable {VariableId} from storage driver {StorageDriverType}", variable.Id, driver.GetType().FullName);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task SaveVariablesAsync(PipelineExecutionContext pipelineExecutionContext)
    {
        var cancellationToken = pipelineExecutionContext.CancellationToken;
        var contexts = pipelineExecutionContext.StepExecutionContexts.ToList();

        foreach (var context in contexts)
        {
            var variables = GetLocalVariables(context).ToList();

            foreach (var variable in variables)
            {
                var block = variable.GetBlock(context.ExpressionExecutionContext);
                var metadata = (VariableBlockMetadata)block.Metadata!;
                var driver = storageDriverManager.Get(metadata.StorageDriverType!);

                if (driver == null)
                    continue;

                var id = GetStateId(variable);
                var value = block.Value;
                var storageDriverContext = new StorageDriverContext(context, variable, cancellationToken);

                if (value == null)
                    await driver.DeleteAsync(id, storageDriverContext).ConfigureAwait(false);
                else
                    await driver.WriteAsync(id, value, storageDriverContext).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteVariablesAsync(StepExecutionContext context, IEnumerable<string>? includeTags = default)
    {
        var register = context.ExpressionExecutionContext.Memory;
        var variableList = GetLocalVariables(context).ToList();
        var cancellationToken = context.CancellationToken;
        var includeTagsList = includeTags?.ToList();

        foreach (var variable in variableList)
        {
            if (!register.TryGetBlock(variable.Id, out var block))
                continue;

            var metadata = (VariableBlockMetadata)block.Metadata!;
            var driver = storageDriverManager.Get(metadata.StorageDriverType!);

            if (driver == null)
                continue;
            
            if (includeTagsList != null && !driver.Tags.Any(includeTagsList.Contains))
                continue;

            var id = GetStateId(variable);
            var storageDriverContext = new StorageDriverContext(context, variable, cancellationToken);
            await driver.DeleteAsync(id, storageDriverContext).ConfigureAwait(false);
            register.Blocks.Remove(variable.Id);
        }
    }

    /// <inheritdoc />
    public async Task DeleteVariablesAsync(PipelineExecutionContext context, IEnumerable<string>? includeTags = default)
    {
        var stepContexts = context.StepExecutionContexts.ToList();
        var includeTagsList = includeTags?.ToList();
        
        foreach (var stepContext in stepContexts)
        {
            await DeleteVariablesAsync(stepContext, includeTagsList).ConfigureAwait(false);
        }
    }

    private IEnumerable<Variable> GetLocalVariables(IExecutionContext context) => context.Variables;

    private MemoryBlock EnsureBlock(MemoryRegister register, Variable variable)
    {
        if (!register.TryGetBlock(variable.Id, out var block))
            block = register.Declare(variable);
        return block;
    }

    private string GetStateId(Variable variable) => variable.Id;
}