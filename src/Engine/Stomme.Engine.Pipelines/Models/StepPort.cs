namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a binding between an owner step's port and a child step.
/// </summary>
public record StepPort(IStep? Step, ICollection<IStep>? Steps, string PortName)
{
    /// <summary>
    /// Creates a new StepPort from the specified step and port name.
    /// </summary>
    public static StepPort FromStep(IStep step, string portName) => new(step, null, portName);

    /// <summary>
    /// Creates a new StepPort from the specified steps and port name.
    /// </summary>
    public static StepPort FromSteps(IEnumerable<IStep> steps, string portName) => new(null, steps.ToList(), portName);

    /// <summary>
    /// Returns the steps bound to the port. Returns a list even if the Step is set and not Steps.
    /// </summary>
    public IEnumerable<IStep> GetSteps()
    {
        return Step != null ? [Step] : Steps!.ToList();
    }
}