#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Models;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// The <see cref="Tenant"/> records that have ALREADY been resolved in this DI scope, <b>keyed by
    /// TenantId</b>, so that <c>DbContext.OnConfiguring</c> can pick a connection string without reading a
    /// store (D37).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>OnConfiguring</c> is a synchronous EF platform virtual that Stomme does not
    /// own, so it may stay sync — but only if it touches no store. It used to call
    /// <c>ITenantManager.GetTenant()</c>, which reads <c>ITenantRepository</c>. The obvious alternative —
    /// resolve the connection string before construction and pass it in — does not work here, and each blocker
    /// was measured rather than assumed:
    /// </para>
    /// <list type="number">
    /// <item>ASP.NET Identity's <c>UserStore</c> constructor-injects <see cref="Repository.TenantDBContext"/>
    /// via <c>AddIdentityCore&lt;IdentityUser&gt;().AddEntityFrameworkStores&lt;TenantDBContext&gt;()</c>, on
    /// the live authentication path. That is sync DI: there is no factory and nowhere to await.</item>
    /// <item><c>HtmlTextContext</c> (and the downstream <c>[Module]Context</c> template) reach the container
    /// through the <c>ITransientService</c> convention scan, which constructs them the same sync way.</item>
    /// <item><c>DatabaseManager</c> constructs a <c>TenantDBContext</c> directly with <c>new</c>.</item>
    /// <item>Five loops switch tenant MID-SCOPE and build a context after each switch — see below.</item>
    /// </list>
    /// <para>
    /// <b>Why keying by TenantId is the whole safety argument.</b> This type holds NO notion of "the current
    /// tenant". It is a pure lookup table. The current tenant is read fresh from <c>SiteState.Alias</c> on
    /// every single <c>OnConfiguring</c> call, and that alias is what the mid-scope loops already mutate
    /// (<see cref="ITenantManager.SetTenant(Tenant)"/> replaces it). So the key handed to
    /// <see cref="Require"/> IS the tenant being routed at, at the moment of routing.
    /// </para>
    /// <para>
    /// That makes the cross-tenant leak <b>structurally impossible</b> rather than merely avoided. A holder
    /// that cached "the tenant for this scope", warmed once, would route every tenant in
    /// <c>DatabaseManager</c>'s and <c>HostedServiceBase</c>'s per-tenant loops at the REQUEST's tenant —
    /// silently, and against a real connection string, so it would look like it worked. This project has
    /// already shipped one silent wrong-tenant bug (per-tenant flag seeding). Here the worst case is a MISS,
    /// and a miss throws.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> <see cref="Require"/> throws when the alias names a tenant nobody warmed. It must
    /// never fall back to "whatever tenant is in the table" — routing a query at the wrong tenant's database
    /// silently is strictly worse than a crash. The no-alias case is NOT a miss: it is the uninstalled/
    /// installer state, where the context is deliberately left unconfigured (see the callers).
    /// </para>
    /// <para>
    /// Registered SCOPED, alongside <c>SiteState</c>. <c>ITenantManager</c>, <c>IDBContextDependencies</c> and
    /// the contexts themselves are all TRANSIENT, so the scope is the only thing they share — which is exactly
    /// how a <c>SetTenant</c> on one <c>TenantManager</c> instance is already visible to a context built from
    /// another. This type holds pure state and no dependency of its own, so it captures nothing.
    /// </para>
    /// </remarks>
    public interface ITenantStoreBindings
    {
        /// <summary>
        /// Records a tenant the caller ALREADY HOLDS. Synchronous and store-free by construction: the object
        /// is the caller's, so there is nothing to read.
        /// </summary>
        void Add(Tenant tenant);

        /// <summary>Sync, store-free. False when the tenant has not been warmed in this scope.</summary>
        bool TryGet(int tenantId, out Tenant? tenant);

        /// <summary>
        /// Sync, store-free. The routing read. Throws when <paramref name="tenantId"/> was never warmed —
        /// see the fail-closed note on this interface.
        /// </summary>
        Tenant Require(int tenantId);
    }

    /// <inheritdoc cref="ITenantStoreBindings" />
    public sealed class TenantStoreBindings : ITenantStoreBindings
    {
        private readonly Dictionary<int, Tenant> _byTenantId = new();

        /// <inheritdoc />
        public void Add(Tenant tenant)
        {
            if (tenant == null)
            {
                throw new ArgumentNullException(nameof(tenant));
            }

            _byTenantId[tenant.TenantId] = tenant;
        }

        /// <inheritdoc />
        public bool TryGet(int tenantId, out Tenant? tenant)
            => _byTenantId.TryGetValue(tenantId, out tenant);

        /// <inheritdoc />
        public Tenant Require(int tenantId)
        {
            if (_byTenantId.TryGetValue(tenantId, out var tenant))
            {
                return tenant;
            }

            throw new InvalidOperationException(
                $"Tenant {tenantId} has not been resolved in this scope, so a DbContext cannot be routed to its " +
                "database without reading the tenant store — which OnConfiguring must not do (D37). The alias on " +
                "SiteState names this tenant, so something set a tenant without warming it. Set the tenant with " +
                "ITenantManager.SetTenant(Tenant) or ITenantManager.SetAliasAsync(...), both of which warm this " +
                "table. This throws rather than routing at whatever tenant happens to be at hand: a silent " +
                "cross-tenant read is worse than a crash. Warmed in this scope: " +
                (_byTenantId.Count == 0 ? "(none)" : string.Join(", ", _byTenantId.Keys)) + ".");
        }
    }
}
