#nullable enable
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// The persistence seam's <see cref="ITenantContext"/> over Stomme's real tenancy: a request's host+path
    /// resolves to an <see cref="Models.Alias"/>, the alias names a Site and the Tenant that owns it, and the
    /// tenant is what the seam's tenant-owned aggregates are keyed by. This is the adapter four backlog items
    /// independently blocked on (01 phase 3, 02's identity cutover, 03's tenant propagation, 06's per-tenant
    /// flag scoping); it replaces <c>Stomme.FeatureManagement</c>'s single-tenant <c>DefaultTenantContext</c>
    /// fallback, which it out-registers (that one is a <c>TryAdd</c>).
    /// <para>
    /// Two sources, in priority order:
    /// </para>
    /// <list type="number">
    /// <item>An ambient scope opened by <see cref="TenantScopeFactory"/> — for code with no request to resolve
    /// an alias from (the flag seeder walking every tenant, the outbox dispatching under a captured tenant).
    /// It wins, because such a caller has stated the tenant explicitly.</item>
    /// <item>Otherwise <c>SiteState.Alias</c> — whatever the current scope has already resolved or been
    /// handed.</item>
    /// <item>Otherwise <see cref="IAliasAccessor.Alias"/> — the alias <c>TenantMiddleware</c> stashed on
    /// <c>HttpContext.Items</c> for the request.</item>
    /// </list>
    /// <para>
    /// <b>Reading <c>SiteState</c> first is not an optimisation — it is required for correctness, and getting
    /// it wrong is silent.</b> Background jobs run per tenant via <c>HostedServiceBase</c>, which calls
    /// <c>ITenantManager.SetTenant(id)</c>; that puts a <i>mock</i> alias on <c>SiteState</c>
    /// (<c>AliasId = -1</c>, carrying only the real TenantId), and a job has no <c>HttpContext</c> at all. An
    /// adapter that consulted only the request's alias would therefore report "no tenant" inside every job,
    /// fail closed, and every flag a job reads would evaluate <c>false</c>: <c>NotificationJob</c> would stop
    /// sending e-mail and <c>PurgeJob</c> would stop tracking visitors, on every install, logging only that the
    /// capability was "disabled by feature flag".
    /// </para>
    /// <para>
    /// <b>Both sources are synchronous and read no store.</b> That is what keeps
    /// <see cref="ITenantContext"/> — a hot, sync property read on every query the seam's tenant filter touches
    /// — sync <i>legitimately</i>. The fallback used to be <c>ITenantManager.GetAlias()</c>, which resolves the
    /// alias from the request's host+path via <c>IAliasRepository</c>; that is now
    /// <c>GetAliasAsync()</c> and cannot be awaited from here. It is not needed: <c>TenantMiddleware</c> awaits
    /// <c>GetAliasAsync()</c> at the head of every request and publishes the result to BOTH
    /// <c>SiteState.Alias</c> and <c>HttpContext.Items</c> before any seam code runs, so this adapter reads a
    /// value that has already been resolved rather than resolving one itself. (<c>IAliasAccessor</c> is safe
    /// outside a request despite the note this comment used to carry: <c>HttpContextExtensions.GetAlias</c> is
    /// a null-tolerant extension method, so a null <c>HttpContext</c> yields a null alias, not a throw.)
    /// </para>
    /// <para>
    /// <b>When neither yields a tenant, this reports "no tenant, not system scope" — it fails CLOSED.</b> The
    /// query filter the seam applies is <c>SystemScope || TenantId == CurrentTenant</c>, so that combination
    /// matches nothing rather than everything. Reporting system scope as the default would be the more
    /// convenient choice and a cross-tenant data leak: any code path that ran without an alias (a misrouted
    /// request, a job that forgot to set one) would silently read every tenant's rows. System scope is
    /// therefore only ever entered on purpose, via <c>BeginScope(null)</c>.
    /// </para>
    /// <para>
    /// <b>SiteState's alias can be SET later in a scope, and the tenant follows it.</b> A job, and the engine's
    /// worker running a dispatched pipeline, start a scope with no alias and set one when they need a site
    /// (<c>ITenantManager.SetAliasAsync</c>; the pipeline steps through <c>StommeSiteScope</c>). This context used to
    /// resolve once per scope, so a read BEFORE that - measured on RavenDB, where the run's first tenant read happens in the
    /// worker's scope before the step sets its site - pinned "no tenant" for the scope's life, and the step's later
    /// tenant-owned reads failed closed (the site's own connection "was not found"). The cache is now keyed by the SiteState
    /// alias it was computed from; the REQUEST's alias, which cannot change in a scope, is still read at most once.
    /// </para>
    /// </summary>
    public sealed class StommeTenantContext : ITenantContext
    {
        private readonly IAliasAccessor _aliasAccessor;
        private readonly SiteState _siteState;
        private TenantId? _fromAlias;
        private bool _aliasResolved;
        private Models.Alias? _resolvedForSiteAlias;
        private Models.Alias? _requestAlias;
        private bool _requestAliasRead;

        public StommeTenantContext(IAliasAccessor aliasAccessor, SiteState siteState)
        {
            _aliasAccessor = aliasAccessor;
            _siteState = siteState;
        }

        /// <inheritdoc />
        public TenantId? CurrentTenantId
        {
            get
            {
                // An explicit scope always wins — including BeginScope(null), which means system scope and so
                // correctly yields a null tenant WITHOUT falling through to the alias.
                var ambient = TenantScopeFactory.Current;
                return ambient is not null ? ambient.TenantId : ResolveFromAlias();
            }
        }

        /// <inheritdoc />
        public bool IsSystemScope
        {
            get
            {
                // Only an explicit BeginScope(null) is system scope. An unresolved alias is NOT (see the
                // fail-closed note on the class).
                var ambient = TenantScopeFactory.Current;
                return ambient is not null && ambient.TenantId is null;
            }
        }

        /// <summary>
        /// Resolves the tenant from the current alias. The answer is cached per SiteState alias - this property is read on
        /// every query the seam's tenant filter touches - and recomputed when that alias is set or replaced later in the
        /// scope (a job or the engine's worker entering a site). The request's alias cannot change within a scope, so it is
        /// read at most once.
        /// </summary>
        private TenantId? ResolveFromAlias()
        {
            var siteAlias = _siteState?.Alias;
            if (_aliasResolved && ReferenceEquals(siteAlias, _resolvedForSiteAlias))
            {
                return _fromAlias;
            }

            _aliasResolved = true;
            _resolvedForSiteAlias = siteAlias;

            // SiteState first: it carries the job path's mock alias, which the request's alias cannot supply
            // (see the class remarks). Then the request alias TenantMiddleware published to HttpContext.Items.
            // Both null outside either — the fail-closed case.
            var alias = siteAlias;
            if (alias is null)
            {
                if (!_requestAliasRead)
                {
                    _requestAlias = _aliasAccessor?.Alias;
                    _requestAliasRead = true;
                }

                alias = _requestAlias;
            }

            _fromAlias = alias is not null ? FrameworkTenantId.ToTenantIdOrNull(alias.TenantId) : null;

            return _fromAlias;
        }
    }
}
