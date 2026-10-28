namespace Stomme.Persistence.File;

/// <summary>
/// Where the file store keeps its JSON documents. One aggregate = one file under a PER-TENANT directory:
/// <c>{RootPath}/{tenantId}/{AggregateTypeName}/{id}.json</c>. The host computes <see cref="RootPath"/> at
/// composition time from <c>ModuleStore:FilePath</c> (default: <c>{DataDirectory}/module-store</c>), so a
/// deployment can point the store at any writable directory.
/// </summary>
public sealed class ModuleFileStoreOptions
{
    /// <summary>The directory under which every tenant's per-aggregate document tree lives.</summary>
    public required string RootPath { get; init; }
}
