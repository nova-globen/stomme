namespace Stomme.Persistence.Abstractions;

/// <summary>Well-known store names for log-store separation (see feature 03 — Log Store Separation).</summary>
public static class StoreNames
{
    /// <summary>The primary application database. The default for every aggregate.</summary>
    public const string Main = "main";

    /// <summary>An optional separate store for high-volume execution logs (bound by the Pipelines feature).</summary>
    public const string Logs = "logs";
}

/// <summary>
/// Binds an entity type to a named store other than <see cref="StoreNames.Main"/>. Apply
/// <c>[StoreBinding(StoreNames.Logs)]</c> to an entity whose rows should live in the logs database.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StoreBindingAttribute(string storeName) : Attribute
{
    public string StoreName { get; } = storeName;
}

/// <summary>Resolves the store an entity type is bound to. Defaults to <see cref="StoreNames.Main"/>.</summary>
public interface IStoreBindingResolver
{
    string Resolve(Type entityType);
}

/// <summary>Reads <see cref="StoreBindingAttribute"/> off the entity type; unmarked types map to the main store.</summary>
public sealed class StoreBindingResolver : IStoreBindingResolver
{
    public string Resolve(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return entityType.GetCustomAttributes(typeof(StoreBindingAttribute), inherit: false) is [StoreBindingAttribute binding, ..]
            ? binding.StoreName
            : StoreNames.Main;
    }
}
