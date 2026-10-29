using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Resolves the current request's <see cref="Alias"/> and <see cref="Tenant"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolving members are async because they READ A STORE on a miss —
    /// <see cref="IAliasRepository"/> and <see cref="ITenantRepository"/>. They are reached from
    /// <c>TenantMiddleware</c> and from Blazor component lifecycle, i.e. from under the
    /// <c>RendererSynchronizationContext</c>, which is exactly where a blocking wait deadlocks.
    /// </para>
    /// <para>
    /// <b>Every member that establishes a tenant also WARMS <see cref="ITenantStoreBindings"/></b>, which
    /// is what lets <c>DbContext.OnConfiguring</c> — a sync platform virtual — pick a connection string
    /// without reading a store. Two shapes, and the split is deliberate:
    /// </para>
    /// <list type="bullet">
    /// <item>The <b>async</b> members (<see cref="GetAliasAsync"/>, the <c>SetAliasAsync</c> overloads) have an
    /// alias but not a Tenant, so they read the (cached) tenant store where awaiting is legal.</item>
    /// <item><see cref="SetTenant(Tenant)"/> is <b>sync</b> and reads nothing: it takes the Tenant OBJECT.
    /// It replaced <c>SetTenant(int)</c> — measured, all six call sites already held the object and were
    /// passing <c>tenant.TenantId</c>, so taking the object costs nothing and buys the invariant that
    /// <b>a tenant cannot be set without warming it</b>. An id-only overload would compile everywhere and
    /// throw at OnConfiguring, which is a trap, so it is gone rather than kept for convenience.</item>
    /// </list>
    /// <para>
    /// <see cref="GetResolvedTenant"/> is the sync, store-free counterpart the contexts call. There is
    /// deliberately no sync <c>SetAlias(Alias)</c>: it could not warm the bindings, so every caller of it
    /// would have compiled and then thrown. All five of its call sites were already in async methods.
    /// </para>
    /// </remarks>
    public interface ITenantManager
    {
        Task<Alias> GetAliasAsync();
        Task<Tenant> GetTenantAsync();

        /// <summary>
        /// The tenant the current <c>SiteState.Alias</c> names, from the bindings warmed in this scope.
        /// Synchronous and reads NO store — this is what <c>DbContext.OnConfiguring</c> calls.
        /// Returns null when there is no alias at all (the uninstalled/installer state). Throws when an alias
        /// names a tenant that was never warmed: see <see cref="ITenantStoreBindings.Require"/>.
        /// </summary>
        Tenant GetResolvedTenant();

        Task SetAliasAsync(Alias alias);
        Task SetAliasAsync(int tenantId, int siteId);

        /// <summary>
        /// Routes this scope at <paramref name="tenant"/> and warms it into <see cref="ITenantStoreBindings"/>.
        /// Sync and store-free: the caller supplies the object.
        /// </summary>
        void SetTenant(Tenant tenant);
    }

    public class TenantManager : ITenantManager
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAliasRepository _aliasRepository;
        private readonly ITenantRepository _tenantRepository;
        private readonly SiteState _siteState;
        private readonly ITenantStoreBindings _bindings;

        public TenantManager(IHttpContextAccessor httpContextAccessor, IAliasRepository aliasRepository, ITenantRepository tenantRepository, SiteState siteState, ITenantStoreBindings bindings)
        {
            _httpContextAccessor = httpContextAccessor;
            _aliasRepository = aliasRepository;
            _tenantRepository = tenantRepository;
            _siteState = siteState;
            _bindings = bindings;
        }

        public async Task<Alias> GetAliasAsync()
        {
            Alias alias = null;

            // does not support mock Alias objects (GetTenantAsync should be used to retrieve a TenantId)
            if (_siteState?.Alias != null && _siteState.Alias.AliasId != -1)
            {
                alias = _siteState.Alias;
            }
            else
            {
                // if there is HttpContext
                var httpcontext = _httpContextAccessor.HttpContext;
                if (httpcontext != null)
                {
                    // legacy support for client api requests which would include the alias as a path prefix ( ie. {alias}/api/[controller] )
                    int aliasId;
                    string[] segments = httpcontext.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length > 1 && Shared.Constants.ReservedRoutes.Contains(segments[1]) && int.TryParse(segments[0], out aliasId))
                    {
                        alias = (await _aliasRepository.GetAliasesAsync()).ToList().FirstOrDefault(item => item.AliasId == aliasId);
                    }

                    // resolve alias based on host name and path
                    if (alias == null)
                    {
                        string name = httpcontext.Request.Host.Value + httpcontext.Request.Path;
                        alias = await _aliasRepository.GetAliasAsync(name);
                    }

                    // if there is a match save it
                    if (alias != null)
                    {
                        alias.Protocol = (httpcontext.Request.IsHttps) ? "https://" : "http://";
                        alias.BaseUrl = "";
                        if (httpcontext.Request.Headers.ContainsKey("User-Agent") && httpcontext.Request.Headers["User-Agent"] == Shared.Constants.MauiUserAgent)
                        {
                            alias.BaseUrl = alias.Protocol + alias.Name.Replace("/" + alias.Path, "");
                        }
                        _siteState.Alias = alias;
                    }
                }
            }

            // warm the routing table for whatever alias we ended up on, so a DbContext built later in this
            // scope can route without a store read. This is the request path's warm point: TenantMiddleware
            // awaits this at the head of every request, before any context exists.
            await WarmAsync(alias);

            return alias;
        }

        public async Task<Tenant> GetTenantAsync()
        {
            var alias = _siteState?.Alias;
            if (alias != null)
            {
                return await _tenantRepository.GetTenantAsync(alias.TenantId);
            }
            return null;
        }

        /// <inheritdoc />
        public Tenant GetResolvedTenant()
        {
            var alias = _siteState?.Alias;

            // No alias at all is NOT a miss — it is the uninstalled/installer state, and the caller leaves the
            // context unconfigured. Only an alias naming an unwarmed tenant is a miss, and that throws.
            return alias == null ? null : _bindings.Require(alias.TenantId);
        }

        // background processes can set the alias using the SiteState service
        public async Task SetAliasAsync(Alias alias)
        {
            _siteState.Alias = alias;
            await WarmAsync(alias);
        }

        public async Task SetAliasAsync(int tenantId, int siteId)
        {
            var alias = (await _aliasRepository.GetAliasesAsync()).ToList().FirstOrDefault(item => item.TenantId == tenantId && item.SiteId == siteId);
            _siteState.Alias = alias;
            await WarmAsync(alias);
        }

        /// <inheritdoc />
        public void SetTenant(Tenant tenant)
        {
            _siteState.Alias = new Alias { TenantId = tenant.TenantId, AliasId = -1, SiteId = -1 };
            _bindings.Add(tenant);
        }

        /// <summary>
        /// Resolves <paramref name="alias"/>'s tenant into the bindings, once per scope per tenant.
        /// <see cref="ITenantRepository.GetTenantAsync"/> is served from the "Tenants" cache entry, so the
        /// common case costs no query; the point of this warm is that the READ happens HERE, where awaiting is
        /// legal, rather than inside OnConfiguring.
        /// </summary>
        private async Task WarmAsync(Alias alias)
        {
            if (alias == null || _bindings.TryGet(alias.TenantId, out _))
            {
                return;
            }

            var tenant = await _tenantRepository.GetTenantAsync(alias.TenantId);
            if (tenant != null)
            {
                _bindings.Add(tenant);
            }

            // A null tenant is left UNwarmed on purpose: the alias points at a tenant the master store does not
            // have. Adding a placeholder would route somewhere; leaving it makes Require throw and say so.
        }
    }
}
