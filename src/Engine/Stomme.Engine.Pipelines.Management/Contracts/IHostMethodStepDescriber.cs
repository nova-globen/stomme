using System.Reflection;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Provides descriptions of pipeline steps that are backed by methods on a host type.
/// </summary>
/// <remarks>
/// Implement this interface to translate methods on a host type into <see cref="StepDescriptor"/> instances
/// that can be used by the pipeline runtime and design-time tooling. Implementations are responsible for
/// inspecting the specified host type and methods, and returning descriptors that describe how they should be
/// represented and configured as pipeline steps.
/// </remarks>
public interface IHostMethodStepDescriber
{
    /// <summary>
    /// Describes all pipeline steps exposed by the specified host type for the given key.
    /// </summary>
    /// <param name="key">A classifier used to select or group host methods (for example, a category or provider key).</param>
    /// <param name="hostType">The host type whose methods should be described as pipeline steps.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that, when completed, contains the collection of <see cref="StepDescriptor"/> instances
    /// describing the applicable host methods.
    /// </returns>
    Task<IEnumerable<StepDescriptor>> DescribeAsync(string key, Type hostType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes a single pipeline step backed by the specified host method.
    /// </summary>
    /// <param name="key">A classifier used to select or group host methods (for example, a category or provider key).</param>
    /// <param name="hostType">The host type that declares the method to describe.</param>
    /// <param name="method">The method that should be described as a pipeline step.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that, when completed, contains the <see cref="StepDescriptor"/> describing the specified method.
    /// </returns>
    Task<StepDescriptor> DescribeMethodAsync(string key, Type hostType, MethodInfo method, CancellationToken cancellationToken = default);
}
