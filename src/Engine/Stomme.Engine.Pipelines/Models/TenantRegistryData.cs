using System.Collections.Concurrent;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Holds the per-tenant step descriptor dictionaries that back the step registry.
/// </summary>
/// <remarks>
/// <para>
/// This model represents the value stored in the tenant-level registry dictionary described in ADR-0009's
/// three-dictionary architecture. The outermost dictionary typically maps a tenant identifier (or a value
/// representing tenant-agnostic scope) to an instance of <see cref="TenantRegistryData"/>. Within this class,
/// the <see cref="StepDescriptors"/> and <see cref="ProvidedStepDescriptors"/> properties form the
/// inner dictionaries that index step descriptors for that specific tenant or for tenant-agnostic steps.
/// </para>
/// <para>
/// By encapsulating these dictionaries, the registry can manage step descriptors per tenant while maintaining
/// a consistent lookup and invalidation strategy across the entire system.
/// </para>
/// </remarks>
public class TenantRegistryData
{
    /// <summary>
    /// Primary index of step descriptors for this tenant (or for tenant-agnostic scope).
    /// </summary>
    /// <remarks>
    /// The key is a composite of the step <c>Type</c> (a logical step type identifier) and its
    /// <c>Version</c>. This allows efficient lookup of a specific step descriptor by type and version,
    /// which is the most common access pattern when compiling or executing pipelines.
    /// </remarks>
    public ConcurrentDictionary<(string Type, int Version), StepDescriptor> StepDescriptors { get; } = new();

    /// <summary>
    /// Secondary index of step descriptors grouped by their provider type.
    /// </summary>
    /// <remarks>
    /// This dictionary maps a provider <see cref="Type"/> (for example, a step provider implementation)
    /// to the collection of <see cref="StepDescriptor"/> instances contributed by that provider for this
    /// tenant. It complements <see cref="StepDescriptors"/> by enabling provider-centric operations such as
    /// refreshing, removing, or re-registering all descriptors originating from a given provider.
    /// </remarks>
    public ConcurrentDictionary<Type, ICollection<StepDescriptor>> ProvidedStepDescriptors { get; } = new();
}
