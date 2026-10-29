using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// Reconciles the declarative <see cref="OpenIddictSeedOptions"/> (the <c>OpenIddict:Applications</c> /
    /// <c>OpenIddict:Scopes</c> config) into the CURRENT tenant's OpenIddict store — the operator path for
    /// registering relying parties (a+b+c work). Runs once per tenant per process, invoked by the bootstrap
    /// middleware AFTER the store is provisioned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent UPSERT, never delete-then-create.</b> The pre-existing <c>RegisterLoopbackClientAsync</c>
    /// deleted and recreated, which orphans the application's authorizations/tokens on every restart. This
    /// seeder resolves the existing application by client id and <c>UpdateAsync</c>es it in place, or
    /// <c>CreateAsync</c>es a new one — so restarts and re-seeds are stable.
    /// </para>
    /// <para>
    /// <b>Additive.</b> Applications/scopes NOT named in config are left untouched, so config-declared and
    /// runtime-registered (management controller / admin UI) clients coexist without fighting.
    /// </para>
    /// </remarks>
    public interface IOpenIddictConfigSeeder
    {
        /// <summary>Reconciles config into the current tenant's store. No-op when no tenant is resolved.</summary>
        Task SeedAsync();
    }

    /// <summary>Process-lifetime record of which tenants have had their config reconciled.</summary>
    public sealed class OpenIddictSeededTenantCache
    {
        private readonly ConcurrentDictionary<int, byte> _seeded = new();

        public bool IsSeeded(int tenantId) => _seeded.ContainsKey(tenantId);

        public void MarkSeeded(int tenantId) => _seeded.TryAdd(tenantId, 0);
    }

    internal sealed class OpenIddictConfigSeeder : IOpenIddictConfigSeeder
    {
        private readonly ITenantManager _tenantManager;
        private readonly IOpenIddictApplicationManager _applications;
        private readonly IOpenIddictScopeManager _scopes;
        private readonly OpenIddictSeedOptions _options;
        private readonly OpenIddictSeededTenantCache _cache;
        private readonly ILogger<OpenIddictConfigSeeder> _logger;

        public OpenIddictConfigSeeder(
            ITenantManager tenantManager,
            IOpenIddictApplicationManager applications,
            IOpenIddictScopeManager scopes,
            OpenIddictSeedOptions options,
            OpenIddictSeededTenantCache cache,
            ILogger<OpenIddictConfigSeeder> logger)
        {
            _tenantManager = tenantManager;
            _applications = applications;
            _scopes = scopes;
            _options = options;
            _cache = cache;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            Tenant tenant = _tenantManager.GetResolvedTenant();
            if (tenant == null || _cache.IsSeeded(tenant.TenantId))
            {
                return;
            }

            // Success-only caching: a partial failure (store briefly unreachable) retries next request rather
            // than wedging the tenant with a half-seeded store until restart.
            var scopesOk = await SeedScopesAsync(tenant);
            var appsOk = await SeedApplicationsAsync(tenant);
            if (scopesOk && appsOk)
            {
                _cache.MarkSeeded(tenant.TenantId);
            }
        }

        private async Task<bool> SeedScopesAsync(Tenant tenant)
        {
            var allOk = true;
            foreach (var seed in _options.Scopes ?? Enumerable.Empty<OpenIddictScopeSeed>())
            {
                if (string.IsNullOrWhiteSpace(seed.Name))
                {
                    _logger.LogWarning("OpenIddict seed: a scope with no Name was skipped (tenant {TenantId}).", tenant.TenantId);
                    continue;
                }

                try
                {
                    var descriptor = new OpenIddictScopeDescriptor
                    {
                        Name = seed.Name.Trim(),
                        DisplayName = seed.DisplayName,
                        Description = seed.Description
                    };
                    foreach (var resource in seed.Resources ?? Enumerable.Empty<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(resource))
                        {
                            descriptor.Resources.Add(resource.Trim());
                        }
                    }

                    var existing = await _scopes.FindByNameAsync(descriptor.Name, CancellationToken.None);
                    if (existing is null)
                    {
                        await _scopes.CreateAsync(descriptor, CancellationToken.None);
                        _logger.LogInformation("OpenIddict seed: created scope '{Scope}' (tenant {TenantId}).", descriptor.Name, tenant.TenantId);
                    }
                    else
                    {
                        await _scopes.UpdateAsync(existing, descriptor, CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    allOk = false;
                    _logger.LogError(exception, "OpenIddict seed: failed to reconcile scope '{Scope}' (tenant {TenantId}).", seed.Name, tenant.TenantId);
                }
            }

            return allOk;
        }

        private async Task<bool> SeedApplicationsAsync(Tenant tenant)
        {
            var allOk = true;
            foreach (var seed in _options.Applications ?? Enumerable.Empty<OpenIddictApplicationSeed>())
            {
                if (string.IsNullOrWhiteSpace(seed.ClientId))
                {
                    _logger.LogWarning("OpenIddict seed: an application with no ClientId was skipped (tenant {TenantId}).", tenant.TenantId);
                    continue;
                }

                try
                {
                    var descriptor = BuildDescriptor(seed);
                    var existing = await _applications.FindByClientIdAsync(seed.ClientId.Trim(), CancellationToken.None);
                    if (existing is null)
                    {
                        await _applications.CreateAsync(descriptor, CancellationToken.None);
                        _logger.LogInformation("OpenIddict seed: created client '{ClientId}' (tenant {TenantId}).", descriptor.ClientId, tenant.TenantId);
                    }
                    else
                    {
                        await _applications.UpdateAsync(existing, descriptor, CancellationToken.None);
                        _logger.LogInformation("OpenIddict seed: updated client '{ClientId}' (tenant {TenantId}).", descriptor.ClientId, tenant.TenantId);
                    }
                }
                catch (Exception exception)
                {
                    allOk = false;
                    _logger.LogError(exception, "OpenIddict seed: failed to reconcile client '{ClientId}' (tenant {TenantId}).", seed.ClientId, tenant.TenantId);
                }
            }

            return allOk;
        }

        /// <summary>
        /// Builds an <see cref="OpenIddictApplicationDescriptor"/> from a config seed. Shared by create and
        /// update so both paths produce an identical application. The permission set is derived from the
        /// declared grant types + scopes + redirect URIs — never a blanket allow.
        /// </summary>
        /// <param name="seed">The declarative/wire shape of the client.</param>
        /// <param name="requireSecret">
        /// When <c>true</c> (config seeding, and every CREATE) a confidential client with no resolvable secret
        /// is an error. The admin UI's EDIT path passes <c>false</c>: a secret is hashed at rest and cannot be
        /// read back, so an operator changing a redirect URI has no secret to re-supply — the caller then carries
        /// the STORED secret across, leaving it untouched (see <c>OpenIddictController.UpsertApplication</c>).
        /// </param>
        internal static OpenIddictApplicationDescriptor BuildDescriptor(OpenIddictApplicationSeed seed, bool requireSecret = true)
        {
            var clientId = seed.ClientId.Trim();
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? clientId : seed.DisplayName.Trim(),
                ClientType = seed.IsPublic ? ClientTypes.Public : ClientTypes.Confidential,
                ConsentType = seed.ConsentIsExplicit ? ConsentTypes.Explicit : ConsentTypes.Implicit
            };

            if (!seed.IsPublic)
            {
                var secret = seed.ResolveSecret();
                if (string.IsNullOrEmpty(secret) && requireSecret)
                {
                    throw new InvalidOperationException(
                        $"OpenIddict client '{clientId}' is confidential but no secret was resolved " +
                        $"(set ClientSecret or ClientSecretEnv). Public clients set Type=public.");
                }
                descriptor.ClientSecret = secret;
            }

            // Every authority client can reach the token endpoint.
            descriptor.Permissions.Add(Permissions.Endpoints.Token);

            var grants = (seed.GrantTypes ?? new List<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim().ToLowerInvariant())
                .ToHashSet();

            if (grants.Contains("authorization_code"))
            {
                descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
                descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
                descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            }
            if (grants.Contains("client_credentials"))
            {
                descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
            }
            if (grants.Contains("refresh_token"))
            {
                descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
            }

            foreach (var uri in seed.RedirectUris ?? Enumerable.Empty<string>())
            {
                if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(uri?.Trim(), out var redirect))
                {
                    descriptor.RedirectUris.Add(redirect);
                }
            }

            foreach (var uri in seed.PostLogoutRedirectUris ?? Enumerable.Empty<string>())
            {
                if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(uri?.Trim(), out var postLogout))
                {
                    descriptor.PostLogoutRedirectUris.Add(postLogout);
                }
            }
            if (descriptor.PostLogoutRedirectUris.Count > 0)
            {
                descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
            }

            // Scope permissions: each declared scope, uniformly (Prefixes.Scope + name covers
            // openid/email/profile/offline_access/custom). A client can only request what it is permitted.
            foreach (var scope in seed.Scopes ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope.Trim());
                }
            }

            // Public clients MUST use PKCE; confidential clients use it when RequirePkce is set (default). Only
            // meaningful on the authorization-code flow. (The server globally requires PKCE too, so this is the
            // per-client belt to that suspenders.)
            var requirePkce = grants.Contains("authorization_code") && (seed.IsPublic || seed.RequirePkce);
            if (requirePkce)
            {
                descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            }

            return descriptor;
        }
    }
}
