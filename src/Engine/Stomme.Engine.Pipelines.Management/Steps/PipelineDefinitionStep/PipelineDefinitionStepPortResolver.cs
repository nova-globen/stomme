using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;

/// <summary>
/// Returns the root step for a given <see cref="PipelineDefinitionStep"/>.
/// </summary>
public class PipelineDefinitionStepResolver : IStepResolver
{
    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public bool GetSupportsStep(IStep step) => step is PipelineDefinitionStep;

    /// <inheritdoc />
    public ValueTask<IEnumerable<StepPort>> GetStepPortsAsync(IStep step, CancellationToken cancellationToken = default)
    {
        var definitionStep = (PipelineDefinitionStep)step;
        var root = definitionStep.Root;
        var port = StepPort.FromStep(root, nameof(PipelineDefinitionStep.Root));

        return new([port]);
    }
}