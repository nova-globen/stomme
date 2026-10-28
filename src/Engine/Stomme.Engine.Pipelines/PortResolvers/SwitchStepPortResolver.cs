using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.PortResolvers;

/// <summary>
/// Returns a list of outbound steps for a given <see cref="Switch"/> step's branches.
/// </summary>
public class SwitchStepResolver : IStepResolver
{
    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public bool GetSupportsStep(IStep step) => step is Switch;

    /// <inheritdoc />
    public ValueTask<IEnumerable<StepPort>> GetStepPortsAsync(IStep step, CancellationToken cancellationToken = default)
    {
        var ports = GetPortsInternal(step).ToList();
        return new(ports);
    }

    private static IEnumerable<StepPort> GetPortsInternal(IStep step)
    {
        var @switch = (Switch)step;
        var cases = @switch.Cases.Where(x => x.Step != null);

        foreach (var @case in cases)
            yield return StepPort.FromStep(@case.Step!, @case.Label);

        if (@switch.Default != null)
            yield return StepPort.FromStep(@switch.Default, nameof(Switch.Default));
    }
}