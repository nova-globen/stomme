using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Creates instances of <see cref="StepDescriptor" /> for a given step type.
/// </summary>
public interface IStepDescriber
{
    /// <summary>
    /// Describes the specified step type.
    /// </summary>
    /// <param name="stepType">The step type to describe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The step descriptor.</returns>
    Task<StepDescriptor> DescribeStepAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an instance of <see cref="OutputDescriptor"/> for the specified property. 
    /// </summary>
    /// <param name="propertyInfo">The property to describe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The output descriptor.</returns>
    Task<OutputDescriptor> DescribeOutputPropertyAsync(PropertyInfo propertyInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an instance of <see cref="InputDescriptor"/> for the specified property.
    /// </summary>
    /// <param name="propertyInfo">The property to describe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The input descriptor.</returns>
    Task<InputDescriptor> DescribeInputPropertyAsync(PropertyInfo propertyInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the input properties of the specified step type.
    /// </summary>
    /// <param name="stepType">The step type to describe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The input descriptors.</returns>
    Task<IEnumerable<InputDescriptor>> DescribeInputPropertiesAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the output properties of the specified step type.
    /// </summary>
    /// <param name="stepType">The step type to describe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The output descriptors.</returns>
    Task<IEnumerable<OutputDescriptor>> DescribeOutputPropertiesAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the input properties of the specified step type.
    /// </summary>
    /// <param name="stepType">The step type.</param>
    /// <returns>The input properties.</returns>
    IEnumerable<PropertyInfo> GetInputProperties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType);

    /// <summary>
    /// Gets the output properties of the specified step type.
    /// </summary>
    /// <param name="stepType">The step type.</param>
    /// <returns>The output properties.</returns>
    IEnumerable<PropertyInfo> GetOutputProperties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType);
}