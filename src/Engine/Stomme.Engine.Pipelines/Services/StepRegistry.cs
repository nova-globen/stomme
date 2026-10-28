using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepRegistry(IStepDescriber stepDescriber, IEnumerable<IStepDescriptorModifier> modifiers, ITenantAccessor tenantAccessor, ILogger<StepRegistry> logger) : IStepRegistry
{
    // Legacy support for manually registered steps
    private readonly ISet<StepDescriptor> _manualStepDescriptors = new HashSet<StepDescriptor>();

    // Per-tenant step descriptors (pipeline-as-steps, tenant-specific providers, etc.)
    private readonly ConcurrentDictionary<string, TenantRegistryData> _tenantRegistries = new();

    // Tenant-agnostic step descriptors (built-in steps, manually registered, etc.)
    private readonly TenantRegistryData _agnosticRegistry = new();

    /// <inheritdoc />
    public void Add(Type providerType, StepDescriptor descriptor)
    {
        var registry = GetOrCreateRegistry(descriptor.TenantId);
        var providerDescriptors = GetOrCreateProviderDescriptors(registry, providerType);
        Add(descriptor, registry.StepDescriptors, providerDescriptors);
    }

    /// <inheritdoc />
    public void Remove(Type providerType, StepDescriptor descriptor)
    {
        var registry = GetOrCreateRegistry(descriptor.TenantId);
        if (registry.ProvidedStepDescriptors.TryGetValue(providerType, out var providerDescriptors))
        {
            providerDescriptors.Remove(descriptor);
            registry.StepDescriptors.TryRemove((descriptor.TypeName, descriptor.Version), out _);
        }
    }

    /// <inheritdoc />
    public IEnumerable<StepDescriptor> ListAll()
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Get descriptors from current tenant's registry
        var tenantDescriptors = _tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry)
            ? tenantRegistry.StepDescriptors.Values
            : Enumerable.Empty<StepDescriptor>();

        // Get descriptors from agnostic registry
        var agnosticDescriptors = _agnosticRegistry.StepDescriptors.Values;

        return tenantDescriptors.Concat(agnosticDescriptors);
    }

    /// <inheritdoc />
    public IEnumerable<StepDescriptor> ListByProvider(Type providerType)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Get descriptors from current tenant's registry
        var tenantDescriptors = _tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry) &&
                                tenantRegistry.ProvidedStepDescriptors.TryGetValue(providerType, out var tenantProviderDescriptors)
            ? tenantProviderDescriptors
            : Enumerable.Empty<StepDescriptor>();

        // Get descriptors from agnostic registry
        var agnosticDescriptors = _agnosticRegistry.ProvidedStepDescriptors.TryGetValue(providerType, out var agnosticProviderDescriptors)
            ? agnosticProviderDescriptors
            : Enumerable.Empty<StepDescriptor>();

        return tenantDescriptors.Concat(agnosticDescriptors);
    }

    /// <inheritdoc />
    public StepDescriptor? Find(string type)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Always prefer tenant-specific descriptors over tenant-agnostic ones
        // Get highest version from current tenant's registry
        if (_tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry))
        {
            var tenantDescriptor = tenantRegistry.StepDescriptors.Values
                .Where(x => x.TypeName == type)
                .MaxBy(x => x.Version);

            if (tenantDescriptor != null)
                return tenantDescriptor;
        }

        // Fall back to agnostic registry only if no tenant-specific descriptor exists
        return _agnosticRegistry.StepDescriptors.Values
            .Where(x => x.TypeName == type)
            .MaxBy(x => x.Version);
    }

    /// <inheritdoc />
    public StepDescriptor? Find(string type, int version)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Check current tenant's registry first
        if (_tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry) &&
            tenantRegistry.StepDescriptors.TryGetValue((type, version), out var tenantDescriptor))
        {
            return tenantDescriptor;
        }

        // Fall back to agnostic registry
        return _agnosticRegistry.StepDescriptors.TryGetValue((type, version), out var agnosticDescriptor)
            ? agnosticDescriptor
            : null;
    }

    /// <inheritdoc />
    public StepDescriptor? Find(Func<StepDescriptor, bool> predicate)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Check current tenant's registry first
        if (_tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry))
        {
            var tenantMatch = tenantRegistry.StepDescriptors.Values.FirstOrDefault(predicate);
            if (tenantMatch != null) return tenantMatch;
        }

        // Fall back to agnostic registry
        return _agnosticRegistry.StepDescriptors.Values.FirstOrDefault(predicate);
    }

    /// <inheritdoc />
    public IEnumerable<StepDescriptor> FindMany(Func<StepDescriptor, bool> predicate)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Get descriptors from current tenant's registry
        var tenantDescriptors = _tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry)
            ? tenantRegistry.StepDescriptors.Values.Where(predicate)
            : Enumerable.Empty<StepDescriptor>();

        // Get descriptors from agnostic registry
        var agnosticDescriptors = _agnosticRegistry.StepDescriptors.Values.Where(predicate);

        return tenantDescriptors.Concat(agnosticDescriptors);
    }

    /// <inheritdoc />
    public void Register(StepDescriptor descriptor)
    {
        var registry = GetOrCreateRegistry(descriptor.TenantId);
        var providerDescriptors = GetOrCreateProviderDescriptors(registry, GetType());
        Add(descriptor, registry.StepDescriptors, providerDescriptors);
    }

    /// <inheritdoc />
    public async Task RegisterAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken)
    {
        var stepTypeName = StepTypeNameHelper.GenerateTypeName(stepType);

        // Check if already registered in any registry
        if (ListAll().Any(x => x.TypeName == stepTypeName))
            return;

        var stepDescriptor = await stepDescriber.DescribeStepAsync(stepType, cancellationToken).ConfigureAwait(false);

        var registry = GetOrCreateRegistry(stepDescriptor.TenantId);
        Add(stepDescriptor, registry.StepDescriptors, _manualStepDescriptors);
        _manualStepDescriptors.Add(stepDescriptor);
    }

    /// <inheritdoc />
    public async Task RegisterAsync(IEnumerable<Type> stepTypes, CancellationToken cancellationToken = default)
    {
        foreach (var stepType in stepTypes)
            await RegisterAsync(stepType, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) => new(_manualStepDescriptors);

    /// <inheritdoc />
    public async Task RefreshDescriptorsAsync(IEnumerable<IStepProvider> stepProviders, CancellationToken cancellationToken = default)
    {
        foreach (var stepProvider in stepProviders)
            await RefreshDescriptorsAsync(stepProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshDescriptorsAsync(IStepProvider stepProvider, CancellationToken cancellationToken = default)
    {
        var providerType = stepProvider.GetType();

        // Get new descriptors from provider
        var descriptors = (await stepProvider.GetDescriptorsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        // Group descriptors by normalized tenant ID
        // Normalize null to "*" so both map to the same agnostic group, avoiding redundant processing
        var descriptorsByTenant = descriptors.GroupBy(d => NormalizeTenantIdForGrouping(d.TenantId));

        foreach (var group in descriptorsByTenant)
        {
            var tenantId = group.Key;
            var registry = GetOrCreateRegistry(tenantId);

            // Remove old descriptors for this provider from this tenant's registry
            if (registry.ProvidedStepDescriptors.TryGetValue(providerType, out var oldDescriptors))
            {
                foreach (var oldDescriptor in oldDescriptors.ToList())
                {
                    registry.StepDescriptors.TryRemove((oldDescriptor.TypeName, oldDescriptor.Version), out _);
                }
            }

            // Add new descriptors for this tenant
            var providerDescriptors = new List<StepDescriptor>();
            foreach (var descriptor in group)
            {
                Add(descriptor, registry.StepDescriptors, providerDescriptors);
            }

            // Update the provider's descriptor list in this registry
            registry.ProvidedStepDescriptors[providerType] = providerDescriptors;
        }
    }

    private void Add(StepDescriptor? descriptor, ConcurrentDictionary<(string Type, int Version), StepDescriptor> stepDescriptors, ICollection<StepDescriptor> providerDescriptors)
    {
        if (descriptor is null)
        {
            logger.LogError("Unable to add a null descriptor");
            return;
        }

        foreach (var modifier in modifiers)
            modifier.Modify(descriptor);

        // If the descriptor already exists, replace it. But log a warning.
        if (stepDescriptors.TryGetValue((descriptor.TypeName, descriptor.Version), out var existingDescriptor))
        {
            // Remove the existing descriptor from the providerDescriptors collection.
            providerDescriptors.Remove(existingDescriptor);

            // Log a warning.
            logger.LogWarning("Step descriptor {StepType} v{StepVersion} was already registered for tenant {TenantId}. Replacing with new descriptor", descriptor.TypeName, descriptor.Version, descriptor.TenantId);
        }

        stepDescriptors[(descriptor.TypeName, descriptor.Version)] = descriptor;
        providerDescriptors.Add(descriptor);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _tenantRegistries.Clear();
        _agnosticRegistry.StepDescriptors.Clear();
        _agnosticRegistry.ProvidedStepDescriptors.Clear();
    }

    /// <inheritdoc />
    public void ClearProvider(Type providerType)
    {
        var currentTenantId = tenantAccessor.TenantId;

        // Clear from current tenant's registry
        if (_tenantRegistries.TryGetValue(currentTenantId, out var tenantRegistry)
            && tenantRegistry.ProvidedStepDescriptors.TryGetValue(providerType, out var descriptors))
        {
            foreach (var descriptor in descriptors.ToList()) 
                tenantRegistry.StepDescriptors.TryRemove((descriptor.TypeName, descriptor.Version), out _);

            tenantRegistry.ProvidedStepDescriptors.TryRemove(providerType, out _);
        }

        // Clear from agnostic registry
        if (_agnosticRegistry.ProvidedStepDescriptors.TryGetValue(providerType, out var agnosticDescriptors))
        {
            foreach (var descriptor in agnosticDescriptors.ToList()) 
                _agnosticRegistry.StepDescriptors.TryRemove((descriptor.TypeName, descriptor.Version), out _);

            _agnosticRegistry.ProvidedStepDescriptors.TryRemove(providerType, out _);
        }
    }

    /// <summary>
    /// Clears all step descriptors for a specific tenant. Useful when a tenant is deactivated.
    /// </summary>
    internal void ClearTenant(string tenantId)
    {
        _tenantRegistries.TryRemove(tenantId, out _);
    }

    private TenantRegistryData GetOrCreateRegistry(string? tenantId)
    {
        // Null or agnostic tenant ID goes to agnostic registry
        if (tenantId is null or Tenant.AgnosticTenantId)
            return _agnosticRegistry;

        // Get or create tenant-specific registry
        return _tenantRegistries.GetOrAdd(tenantId, _ => new());
    }

    private ICollection<StepDescriptor> GetOrCreateProviderDescriptors(TenantRegistryData registry, Type providerType)
    {
        return registry.ProvidedStepDescriptors.GetOrAdd(providerType, _ => new List<StepDescriptor>());
    }

    /// <summary>
    /// Normalizes tenant ID for grouping purposes.
    /// Converts null to "*" so that both null and "*" descriptors are grouped together,
    /// avoiding redundant processing of the agnostic registry.
    /// </summary>
    private static string? NormalizeTenantIdForGrouping(string? tenantId)
    {
        // Normalize null to "*" so both map to the same group
        return tenantId ?? Tenant.AgnosticTenantId;
    }
}