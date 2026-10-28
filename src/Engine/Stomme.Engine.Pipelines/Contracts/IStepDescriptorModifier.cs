using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides a way to modify step descriptors as they are registered.
/// </summary>
public interface IStepDescriptorModifier
{
    /// <summary>
    /// Modifies the specified step descriptor.
    /// </summary>
    /// <param name="descriptor">The step descriptor to modify.</param>
    void Modify(StepDescriptor descriptor);
}