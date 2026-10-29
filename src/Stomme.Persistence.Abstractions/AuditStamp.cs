using System.Collections.Concurrent;
using System.Reflection;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Supplies the identity and the clock the audit rule stamps with. A provider resolves this from the
/// ambient request (the relational path historically read <c>IHttpContextAccessor.HttpContext.User</c>);
/// an unauthenticated write stamps the empty string, matching the legacy <c>DbContextUtils</c> behaviour.
/// </summary>
public interface IAuditContext
{
    /// <summary>The username to stamp; empty when the write is not attributable to a user.</summary>
    string CurrentUsername { get; }

    /// <summary>The timestamp to stamp. UTC.</summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// True while a write must land WITHOUT a stamp (a restore replaying rows, an erasure replacing an author's
    /// name by a placeholder): the relational path asks the same question of its suppression scope. False by default.
    /// </summary>
    bool IsSuppressed => false;
}

/// <summary>An <see cref="IAuditContext"/> for writes with no ambient user (installer, jobs, tests).</summary>
public sealed class SystemAuditContext(string username = "") : IAuditContext
{
    public string CurrentUsername { get; } = username;

    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// The audit rule — <c>CreatedBy</c>/<c>CreatedOn</c> on insert, <c>ModifiedBy</c>/<c>ModifiedOn</c> on
/// insert and update — expressed ONCE so the relational and document providers cannot drift apart.
/// <para>
/// <b>Why duck-typed rather than an interface.</b> The auditable contract that the framework store's
/// aggregates actually implement is <c>Stomme.Models.IAuditable</c>, which lives in
/// <c>Stomme.Shared</c> — the CMS model project. Referencing it from here would invert the layering
/// (the persistence seam would depend on the CMS). Its four members are public, settable, and named
/// consistently, so matching them by shape binds both providers to the same rule without the reference.
/// </para>
/// <para>
/// <b>Why not <c>SharedKernel.IAuditableEntity</c>.</b> It has zero implementers and zero consumers, and
/// it is shape-incompatible with what is actually persisted: get-only members, <c>DateTimeOffset</c>, and
/// <c>CreatedAt</c>/<c>LastModifiedBy</c> against <c>IAuditable</c>'s settable <c>DateTime</c>
/// <c>CreatedOn</c>/<c>ModifiedBy</c>. An aggregate implementing it would not be stamped at all.
/// </para>
/// <para>
/// <b>Scope.</b> This covers <c>IAuditable</c> (<c>Created*/Modified*</c>) AND, since <b>D59</b>,
/// <c>IDeletable</c> (<c>DeletedBy</c>/<c>DeletedOn</c>) via <see cref="StampDeleted"/>. The legacy
/// <c>DbContextUtils</c> decides the delete transition by comparing the incoming <c>IsDeleted</c> against the
/// value currently in the database — a load a document store avoids. D59's port reproduces the SAME observable
/// outcome WITHOUT that load: the first transition to deleted is identified by
/// <c>IsDeleted == true &amp;&amp; DeletedOn == null</c> (the stamp is written exactly once, then
/// <c>DeletedOn</c> being set suppresses re-stamping on later saves), and a restore by
/// <c>IsDeleted == false &amp;&amp; DeletedOn != null</c> (clears the pair, as EF does). See
/// src/Stomme.Persistence.Abstractions/README.md.
/// </para>
/// </summary>
public static class AuditStamp
{
    private static readonly ConcurrentDictionary<Type, AuditProperties?> Cache = new();
    private static readonly ConcurrentDictionary<Type, DeleteProperties?> DeleteCache = new();

    /// <summary>True when <paramref name="type"/> carries the full auditable shape.</summary>
    public static bool IsAuditable(Type type) => Resolve(type) is not null;

    /// <summary>True when <paramref name="type"/> carries the soft-delete shape (<c>IDeletable</c>).</summary>
    public static bool IsDeletable(Type type) => ResolveDelete(type) is not null;

    /// <summary>
    /// Stamps an insert: creator and creation timestamp, plus the modified pair (an insert is also the
    /// first modification — this is what the relational path does for <c>EntityState.Added</c>).
    /// </summary>
    public static void StampCreated(object entity, IAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);
        if (Resolve(entity.GetType()) is not { } properties)
        {
            return;
        }

        var now = context.UtcNow;
        properties.CreatedBy.SetValue(entity, context.CurrentUsername);
        properties.CreatedOn.SetValue(entity, now);
        properties.ModifiedBy.SetValue(entity, context.CurrentUsername);
        properties.ModifiedOn.SetValue(entity, now);
    }

    /// <summary>Stamps an update: the modified pair only; the creation stamp is never rewritten.</summary>
    public static void StampModified(object entity, IAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);
        if (Resolve(entity.GetType()) is not { } properties)
        {
            return;
        }

        properties.ModifiedBy.SetValue(entity, context.CurrentUsername);
        properties.ModifiedOn.SetValue(entity, context.UtcNow);
    }

    /// <summary>
    /// Applies the soft-delete stamp, reproducing <c>DbContextUtils</c>' observable outcome WITHOUT the
    /// database load EF uses to detect the <c>IsDeleted</c> transition.
    /// <list type="bullet">
    /// <item><c>IsDeleted == true &amp;&amp; DeletedOn == null</c> — the first transition to deleted: stamp
    /// <c>DeletedBy</c> (the current username, empty when unattributed, exactly as EF writes it) and
    /// <c>DeletedOn</c>. Once <c>DeletedOn</c> is set, a later save of the still-deleted entity no longer meets
    /// this condition, so the stamp is written exactly once — matching EF, which stamps only when the DB value
    /// was still <c>false</c>.</item>
    /// <item><c>IsDeleted == false &amp;&amp; DeletedOn != null</c> — a restore: clear both, as EF's second
    /// branch does when the incoming <c>IsDeleted</c> is false and the DB value was true.</item>
    /// </list>
    /// <b>The one unreachable edge, named not papered over:</b> EF SKIPS the <c>IDeletable</c> block on INSERT
    /// (<c>EntityState.Added</c>), whereas this rule cannot tell a document insert from an update and so WOULD
    /// stamp an entity inserted already-soft-deleted. That flow does not occur in the product — an aggregate is
    /// always inserted with <c>IsDeleted == false</c> and soft-deleted by a later update — so the reachable
    /// behaviour is identical on both providers.
    /// </summary>
    public static void StampDeleted(object entity, IAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);
        if (ResolveDelete(entity.GetType()) is not { } properties)
        {
            return;
        }

        var isDeleted = properties.IsDeleted.GetValue(entity) is true;
        var hasDeletedOn = properties.DeletedOn.GetValue(entity) is not null;

        if (isDeleted && !hasDeletedOn)
        {
            properties.DeletedBy.SetValue(entity, context.CurrentUsername);
            properties.DeletedOn.SetValue(entity, context.UtcNow);
        }
        else if (!isDeleted && hasDeletedOn)
        {
            properties.DeletedBy.SetValue(entity, null);
            properties.DeletedOn.SetValue(entity, null);
        }
    }

    /// <summary>
    /// True when the entity has never been stamped. Document providers store detached instances through
    /// per-operation sessions and so have no change tracker to ask "is this an insert?"; an unset
    /// <c>CreatedOn</c> is the durable stand-in for that signal.
    /// </summary>
    public static bool IsUnstamped(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Resolve(entity.GetType()) is { } properties
            && properties.CreatedOn.GetValue(entity) is DateTime created
            && created == default;
    }

    private static AuditProperties? Resolve(Type type) => Cache.GetOrAdd(type, static t =>
    {
        var createdBy = Settable(t, nameof(AuditProperties.CreatedBy), typeof(string));
        var createdOn = Settable(t, nameof(AuditProperties.CreatedOn), typeof(DateTime));
        var modifiedBy = Settable(t, nameof(AuditProperties.ModifiedBy), typeof(string));
        var modifiedOn = Settable(t, nameof(AuditProperties.ModifiedOn), typeof(DateTime));

        return createdBy is null || createdOn is null || modifiedBy is null || modifiedOn is null
            ? null
            : new AuditProperties(createdBy, createdOn, modifiedBy, modifiedOn);
    });

    private static DeleteProperties? ResolveDelete(Type type) => DeleteCache.GetOrAdd(type, static t =>
    {
        var deletedBy = Settable(t, nameof(DeleteProperties.DeletedBy), typeof(string));
        var deletedOn = Settable(t, nameof(DeleteProperties.DeletedOn), typeof(DateTime?));
        var isDeleted = Settable(t, nameof(DeleteProperties.IsDeleted), typeof(bool));

        return deletedBy is null || deletedOn is null || isDeleted is null
            ? null
            : new DeleteProperties(deletedBy, deletedOn, isDeleted);
    });

    private static PropertyInfo? Settable(Type type, string name, Type propertyType)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property is not null && property.PropertyType == propertyType && property.CanWrite
            ? property
            : null;
    }

    private sealed record AuditProperties(
        PropertyInfo CreatedBy,
        PropertyInfo CreatedOn,
        PropertyInfo ModifiedBy,
        PropertyInfo ModifiedOn);

    private sealed record DeleteProperties(
        PropertyInfo DeletedBy,
        PropertyInfo DeletedOn,
        PropertyInfo IsDeleted);
}
