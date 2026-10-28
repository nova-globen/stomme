using Stomme.Engine.Common.Services;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;

namespace Stomme.Engine.KeyValues.Stores;

/// <summary>
/// Stores key value records in memory.
/// </summary>
public class MemoryKeyValueStore : IKeyValueStore
{
    private readonly MemoryStore<SerializedKeyValuePair> _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryKeyValueStore"/> class.
    /// </summary>
    public MemoryKeyValueStore(MemoryStore<SerializedKeyValuePair> store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public Task SaveAsync(SerializedKeyValuePair keyValuePair, CancellationToken cancellationToken)
    {
        _store.Save(keyValuePair, kv => kv.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SerializedKeyValuePair?> FindAsync(KeyValueFilter filter, CancellationToken cancellationToken)
    {
        var result = _store.Query(filter.Apply).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<SerializedKeyValuePair>> FindManyAsync(KeyValueFilter filter, CancellationToken cancellationToken)
    {
        var result = _store.Query(filter.Apply);
        return Task.FromResult(result);
    }
    
    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _store.DeleteWhere(x => x.Key == key);
        return Task.CompletedTask;
    }
}