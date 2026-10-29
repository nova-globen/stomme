using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Stamps audit fields on every document RavenDB is about to store, via the store's
/// <see cref="IDocumentStore.OnBeforeStore"/> hook.
/// <para>
/// <b>Why this exists.</b> On the relational path the stamps are applied by <c>DbContextUtils</c>, which is
/// called from the <i>legacy</i> EF context overrides. The seam inherits them by accident of composition —
/// each store news up a legacy context, and <c>EfRepository.SaveAsync</c> calls <c>SaveChangesAsync</c>
/// virtually, landing on that override. Nothing in the seam itself stamps, so a provider that is not EF
/// stamps nothing: a RavenDB store silently wrote empty <c>created_by</c>/<c>modified_by</c>, a behavioural
/// divergence between two providers behind one contract.
/// </para>
/// <para>
/// <b>Drift.</b> The rule is now expressed twice — here and in <c>DbContextUtils</c> — which is a real cost,
/// accepted deliberately: the relational joint is shared with ~20 repositories that are not yet on the seam,
/// so moving stamping into the seam today would mean double-stamping or per-entity gating in the one
/// mechanism the migrated clusters rely on, for no behavioural gain. The mitigation is that both providers
/// call the same <see cref="AuditStamp"/> helper and a conformance test asserts they stamp identically.
/// Promote to a seam-wide port when the last EF repository leaves <c>DbContextUtils</c>.
/// </para>
/// </summary>
public static class RavenAuditListener
{
    /// <summary>
    /// Attaches the audit stamp to <paramref name="store"/>. Idempotent per <paramref name="context"/>:
    /// calling it twice with the same store would stamp twice, which is harmless but pointless.
    /// </summary>
    public static IDocumentStore StampAudit(this IDocumentStore store, IAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        store.OnBeforeStore += (_, args) => Stamp(args, context);
        return store;
    }

    private static void Stamp(BeforeStoreEventArgs args, IAuditContext context)
    {
        if (args.Entity is not { } entity || context.IsSuppressed)
        {
            return;
        }

        var type = entity.GetType();

        // IAuditable stamps. "Is this an insert?" is decided by an unset CreatedOn rather than by session
        // state. The repository stores DETACHED instances through per-operation sessions, so RavenDB's identity
        // map cannot answer it — a genuine update would look like an insert to any session-based check. An
        // unset CreatedOn is the durable signal, and it converges with the relational path's EntityState.Added
        // for every flow that inserts before it updates.
        if (AuditStamp.IsAuditable(type))
        {
            if (AuditStamp.IsUnstamped(entity))
            {
                AuditStamp.StampCreated(entity, context);
            }
            else
            {
                AuditStamp.StampModified(entity, context);
            }
        }

        // IDeletable soft-delete stamp — independent of IAuditable (Notification is IDeletable-only;
        // Page is both). AuditStamp.StampDeleted reproduces DbContextUtils' DeletedBy/DeletedOn outcome
        // without the DB load EF uses, keyed on the (IsDeleted, DeletedOn) pair. See that method's remarks.
        if (AuditStamp.IsDeletable(type))
        {
            AuditStamp.StampDeleted(entity, context);
        }
    }
}
