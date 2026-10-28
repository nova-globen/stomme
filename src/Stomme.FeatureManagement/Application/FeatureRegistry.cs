namespace Stomme.FeatureManagement.Application;

/// <summary>
/// The catalog of feature flags declared across modules — the discoverability surface. It unions every
/// <see cref="IFeatureContributor"/> so flags are known (with their description and defaults) before any
/// tenant has created the backing document; the seeder and admin UI consult it.
/// </summary>
public interface IFeatureRegistry
{
    /// <summary>All declared flags, deduplicated by key (first contributor wins), ordered by key.</summary>
    IReadOnlyList<FeatureDescriptor> Known { get; }

    /// <summary>The descriptor for a key, or null when no module has declared it.</summary>
    FeatureDescriptor? Find(string key);
}

internal sealed class FeatureRegistry : IFeatureRegistry
{
    private readonly Dictionary<string, FeatureDescriptor> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public FeatureRegistry(IEnumerable<IFeatureContributor> contributors)
    {
        foreach (var descriptor in contributors.SelectMany(c => c.Features))
        {
            // First contributor to declare a key wins; later duplicates are ignored so a module can't
            // silently redefine another's flag.
            _byKey.TryAdd(descriptor.Key, descriptor);
        }

        Known = _byKey.Values.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<FeatureDescriptor> Known { get; }

    public FeatureDescriptor? Find(string key) =>
        !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key.Trim(), out var descriptor) ? descriptor : null;
}
