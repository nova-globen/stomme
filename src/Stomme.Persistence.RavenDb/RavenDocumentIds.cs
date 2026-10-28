using System.Collections.Concurrent;
using System.Reflection;
using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// The single expression of Stomme's RavenDB document-addressing convention: <c>{TypeName}/{id}</c>.
/// <para>
/// This MUST stay shared. <see cref="RavenRepository{TAggregate, TId}"/> writes documents at these ids and
/// <see cref="RavenDocumentSession"/> reads them back through the seam's
/// <see cref="Persistence.Abstractions.IQuerySession.LoadAsync{TEntity}"/>, whose contract takes the
/// entity's key (an <c>int</c>, as the relational provider's <c>FindAsync</c> does) rather than a document
/// id. If the two ever formatted ids differently, every write would still succeed and every load would
/// silently return null.
/// </para>
/// </summary>
public static class RavenDocumentIds
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> IdProperties = new();

    /// <summary>The document id addressing <paramref name="id"/> within <paramref name="entityType"/>'s collection.</summary>
    public static string For(Type entityType, object id)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(id);
        return $"{entityType.Name}/{id}";
    }

    /// <summary>The document id for an entity, derived from its <c>Id</c> property.</summary>
    public static string For(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var type = entity.GetType();
        return For(type, IdValue(entity) ?? throw new InvalidOperationException(
            $"'{type.Name}' has no readable 'Id' property, so it cannot be addressed as a RavenDB document."));
    }

    /// <summary>The value of the entity's <c>Id</c> property, or null when it has none.</summary>
    public static object? IdValue(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return IdProperty(entity.GetType())?.GetValue(entity);
    }

    /// <summary>
    /// Assigns the entity's <c>Id</c>. <see cref="Entity{TId}.Id"/> has a protected setter — the domain
    /// deliberately does not let callers reassign identity — so a database-assigned id is written the same
    /// way EF Core's materializer writes one: through the property, reflectively.
    /// </summary>
    public static void SetId(object entity, object id)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(id);
        var property = IdProperty(entity.GetType())
            ?? throw new InvalidOperationException($"'{entity.GetType().Name}' has no 'Id' property to assign.");
        var setter = property.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"'{entity.GetType().Name}.Id' has no setter to assign.");
        setter.Invoke(entity, [id]);
    }

    private static PropertyInfo? IdProperty(Type type) => IdProperties.GetOrAdd(type, static t =>
        t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance));
}
