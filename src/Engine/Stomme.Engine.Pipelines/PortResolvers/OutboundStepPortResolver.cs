using System.Reflection;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.PortResolvers;

/// <summary>
/// Returns a list of outbound steps for a given step by reflecting over its public properties matching <see cref="IStep"/> and <c>ICollection{IStep}</c>.
/// </summary>
public class PropertyBasedStepResolver : IStepResolver
{
    /// <inheritdoc />
    public int Priority => -1;

    /// <inheritdoc />
    public bool GetSupportsStep(IStep step) => true;

    /// <inheritdoc />
    public ValueTask<IEnumerable<StepPort>> GetStepPortsAsync(IStep step, CancellationToken cancellationToken = default)
    {
        return new(GetStepPortsInternal(step));
    }

    private static IEnumerable<StepPort> GetStepPortsInternal(IStep step)
    {
        var stepType = step.GetType();

        var ports =
            from prop in stepType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            where typeof(IStep).IsAssignableFrom(prop.PropertyType) || typeof(IEnumerable<IStep>).IsAssignableFrom(prop.PropertyType)
            let value = prop.GetValue(step)
            let isCollection = GetPropertyIsCollection(prop.PropertyType)
            let portName = prop.Name
            where value != null
            select isCollection ? StepPort.FromSteps((IEnumerable<IStep>)value, portName) : StepPort.FromStep((IStep)value, portName);

        return ports.ToList();
    }

    private static bool GetPropertyIsCollection(Type propertyType) => typeof(IEnumerable<IStep>).IsAssignableFrom(propertyType);
}