using Stomme.Persistence.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// Opens EF Core sessions bound to a named store. The store map supplies a fresh <see cref="DbContext"/>
/// per session (the session owns and disposes it). The Pipelines feature binds a second
/// <see cref="StoreNames.Logs"/> store here for execution-log separation; until then only the main store
/// is bound.
/// </summary>
public sealed class EfDocumentSessionFactory(IReadOnlyDictionary<string, Func<DbContext>> stores) : IDocumentSessionFactory
{
    public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => new EfDocumentSession(Create(storeName));

    public IDocumentSession OpenSession(string storeName = StoreNames.Main) => new EfDocumentSession(Create(storeName));

    private DbContext Create(string storeName) =>
        stores.TryGetValue(storeName, out var factory)
            ? factory()
            : throw new InvalidOperationException($"No store is bound to the name '{storeName}'.");
}
