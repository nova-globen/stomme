using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Stomme.Extensions;
using Stomme.Identity.OpenIddict.Raven;
using Stomme.Models;
using Stomme.Repository.Stores;
using Stomme.Shared;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// Wires a PER-TENANT OpenIddict authority into Stomme's existing Blazor SSR host.
    /// </summary>
    /// <remarks>
    /// Everything here is gated on <c>OpenIddict:Enabled = true</c> so a stock install is entirely
    /// unaffected — the authority co-hosts inside the SAME pipeline (no sibling process) only when asked.
    /// Per-tenant isolation is proven by:
    /// <list type="bullet">
    /// <item><b>Issuer</b> — <c>SetIssuer</c> is deliberately NOT called, so OpenIddict infers the issuer
    /// from the request host; each tenant's distinct alias/host yields a distinct <c>iss</c> for free.</item>
    /// <item><b>Stores</b> — the applications/authorizations/scopes/tokens live in
    /// <see cref="TenantOpenIddictDbContext"/>, a dedicated context routed per-alias, so a client registered
    /// for tenant A is not found when a token is requested against tenant B's host.</item>
    /// </list>
    /// Signing/encryption keys are a single SHARED ephemeral key for B1 (per-tenant keys are B1b) — isolation
    /// is carried by issuer + store, driven, not by the key.
    /// </remarks>
    public static class StommeOpenIddictExtensions
    {
        public const string EnabledConfigKey = "OpenIddict:Enabled";

        /// <summary>The audience/resource stamped on every access token this authority issues (spike).</summary>
        public const string ApiResource = "stomme-api";

        /// <summary>
        /// The claim naming the site that issued a token: its alias (<c>example.com/shop</c>), in the access token and the
        /// id_token, so a module can assert the site as well as relying on the issuer check.
        /// </summary>
        public const string SiteClaim = "site";

        public static bool IsOpenIddictEnabled(this IConfiguration configuration)
            => configuration.GetValue<bool>(EnabledConfigKey);

        /// <summary>
        /// The configured database provider type, read from config the same way
        /// <c>ConfigManager.GetDatabaseType()</c> reads it (empty resolves to the SqlServer default; the pre-6.1.5
        /// <c>, Stomme.Database.*</c> namespace is rewritten to <c>, Stomme.Server</c>). Read at composition
        /// time so the OpenIddict store provider can match the database provider.
        /// </summary>
        private static string ResolveConfiguredDatabaseType(IConfiguration configuration)
        {
            var databaseType = configuration.GetSection(SettingKeys.DatabaseSection)[SettingKeys.DatabaseTypeKey];
            if (string.IsNullOrEmpty(databaseType))
            {
                return Constants.DefaultDBType;
            }

            if (databaseType.Contains(", Stomme.Database."))
            {
                return databaseType.Substring(0, databaseType.IndexOf(", ", StringComparison.Ordinal)) + ", Stomme.Server";
            }

            return databaseType;
        }

        public static IServiceCollection AddStommeOpenIddict(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            if (!configuration.IsOpenIddictEnabled())
            {
                return services;
            }

            // Pick the OpenIddict store provider to MATCH the configured database provider — the EF
            // TenantOpenIddictDbContext on a relational install, the RavenDB stores on a document install. This
            // is a COMPOSITION-time decision (read once here) rather than the seam's resolve-time selection,
            // because OpenIddict binds its manager to ONE default entity type per store (EF's
            // OpenIddictEntityFrameworkCoreApplication vs the Raven OpenIddictRavenApplication), which cannot be
            // swapped per-resolve the way a single closed store interface can. Consequence (honest limit): the
            // provider must be persisted in config by the time the host that ENABLES OpenIddict boots — true for
            // every post-install boot, which is when the opt-in authority is switched on.
            var databaseType = ResolveConfiguredDatabaseType(configuration);
            var relational = FrameworkStoreProviderSelection.IsRelationalProvider(databaseType);

            if (relational)
            {
                // The dedicated per-tenant OpenIddict store. Scoped (EF default) so it resolves inside the request
                // scope where TenantMiddleware has already established SiteState.Alias -> the resolved tenant.
                services.AddDbContext<TenantOpenIddictDbContext>();

                // The schema was previously created ONLY by the test suite's own EnsureCreated, so a
                // deployed install could not issue a token. The provisioner mirrors the Raven side's
                // ensure-on-first-use, once per tenant per process, invoked by the provisioning middleware.
                services.AddSingleton<OpenIddictProvisionedStoreCache>();
                services.AddScoped<IOpenIddictStoreProvisioner, RelationalOpenIddictStoreProvisioner>();
            }
            else
            {
                // The RavenDB stores' per-tenant plumbing: a host-wide store cache (singleton) and the scoped
                // session provider that routes each session to the current tenant's dedicated OpenIddict database.
                services.AddSingleton<RavenOpenIddictDocumentStoreCache>();
                services.AddScoped<IRavenOpenIddictSessionProvider, TenantRavenOpenIddictSessionProvider>();

                // Storage-blind seam — Raven callers get a no-op because the session provider above
                // already creates the per-tenant OpenIddict database idempotently at session open.
                services.AddScoped<IOpenIddictStoreProvisioner, RavenOpenIddictStoreProvisioner>();
            }

            // The authority's clients and scopes in the installation snapshot; a restore revokes every token.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services,
                ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant, Stomme.Infrastructure.Backup.Participants.OpenIddictBackupParticipant>());

            // Client/scope work (a): the declarative config seeder. Provider-agnostic — it drives the OpenIddict
            // managers, which OpenIddict has already bound to the tenant's chosen store (EF or Raven). Bound from
            // the OpenIddict:Applications / OpenIddict:Scopes config; reconciled once per tenant per process by
            // the bootstrap middleware after the store is provisioned.
            var seedOptions = new OpenIddictSeedOptions();
            configuration.GetSection(OpenIddictSeedOptions.SectionName).Bind(seedOptions);
            services.AddSingleton(seedOptions);
            services.AddSingleton<OpenIddictSeededTenantCache>();
            services.AddScoped<IOpenIddictConfigSeeder, OpenIddictConfigSeeder>();

            services.AddOpenIddict()
                .AddCore(options =>
                {
                    if (relational)
                    {
                        options.UseEntityFrameworkCore()
                            .UseDbContext<TenantOpenIddictDbContext>();
                    }
                    else
                    {
                        // The four IOpenIddict*Store twins over RavenDB, per-tenant. Registered OUTSIDE the
                        // reflective seam gates, which is exactly why the mandatory driven parity test
                        // exists: a missing/broken Raven token store would silently down the token endpoint.
                        options.UseStommeRavenOpenIddictStores();
                    }

                    // A native app's loopback redirect on any port (RFC 8252 7.3).
                    options.ReplaceApplicationManager(typeof(LoopbackRedirectApplicationManager<>));
                })
                .AddServer(options =>
                {
                    // The INTERACTIVE browser flow is added alongside B1's client-credentials endpoint.
                    // /connect/authorize (interactive), /connect/userinfo (OIDC claims), plus the discovery
                    // (/.well-known/openid-configuration) and JWKS (/.well-known/jwks) endpoints, which
                    // OpenIddict serves itself by default (no passthrough needed) so the consumer can fetch
                    // metadata and validate the id_token's signature.
                    // The URIs are RELATIVE (no leading slash): OpenIddict resolves them against the request's base URI,
                    // PathBase included, so a folder site's discovery document names its own /<folder>/connect/* endpoints.
                    // With a leading slash they resolve against the host root.
                    options.SetAuthorizationEndpointUris("connect/authorize");
                    options.SetTokenEndpointUris("connect/token");
                    options.SetUserInfoEndpointUris("connect/userinfo");
                    // Client/scope work (c): RP-initiated logout. Most OIDC client libraries call the
                    // end_session_endpoint on sign-out; without it the RP "logs out" while the Stomme cookie
                    // stays live and immediately re-authenticates. OpenIddict validates post_logout_redirect_uri
                    // against the client's registered set.
                    options.SetEndSessionEndpointUris("connect/endsession");

                    // RFC 7009 revocation: a native app revokes its refresh token on sign-out. OpenIddict answers it itself.
                    options.SetRevocationEndpointUris("connect/revoke");

                    // A client registered for named sites (OpenIddict:Applications:n:Sites) is refused at any other site,
                    // even one sharing its tenant's store.
                    SiteScopedClientHandlers.Register(options);

                    options.AllowClientCredentialsFlow();

                    // Authorization_code + PKCE — the interactive browser login grant.
                    options.AllowAuthorizationCodeFlow()
                        .RequireProofKeyForCodeExchange();

                    // Client/scope work (b): refresh tokens, so an interactive session survives access-token
                    // expiry without a full re-authorization. Opt-in PER CLIENT — a client gets refresh tokens
                    // only if it has the refresh_token grant permission AND requests offline_access, so a stock
                    // client-credentials client is unaffected.
                    options.AllowRefreshTokenFlow();

                    options.RegisterScopes(Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, "api");

                    // Signing/encryption keys. Signed-only JWT access tokens so a resource server / the test can
                    // inspect and validate them; the signing key is exposed via the JWKS endpoint above so the
                    // OIDC consumer can validate the id_token, and the encryption key protects the authorization
                    // code.
                    //
                    // PERSIST the keys across restarts when a certificate is configured, so the JWKS `kid`
                    // and the tokens stay STABLE (an ephemeral key regenerates per process and breaks a real
                    // deployment — every restart invalidates outstanding tokens and rotates the JWKS). When
                    // NOTHING is configured, fall back to B1's shared ephemeral keys — so a stock install and
                    // every existing test are byte-for-byte unchanged. Isolation is carried by host-inferred
                    // issuer + per-tenant store (B1 Q4), NOT by the key, so a SINGLE signing cert shared across
                    // tenants is correct here; per-tenant keys remain the separate deferred B1b.
                    var signingCertificate = OpenIddictCertificateLoader.TryLoadSigningCertificate(configuration);
                    if (signingCertificate != null)
                    {
                        options.AddSigningCertificate(signingCertificate);
                    }
                    else
                    {
                        options.AddEphemeralSigningKey();
                    }

                    var encryptionCertificate = OpenIddictCertificateLoader.TryLoadEncryptionCertificate(configuration);
                    if (encryptionCertificate != null)
                    {
                        options.AddEncryptionCertificate(encryptionCertificate);
                    }
                    else
                    {
                        options.AddEphemeralEncryptionKey();
                    }

                    options.DisableAccessTokenEncryption();

                    // NOTE: SetIssuer is deliberately NOT called — OpenIddict infers the issuer from the
                    // request host and PathBase (a folder site's connect/* and .well-known/* requests carry the folder as their
                    // PathBase, TenantMiddleware), which is what makes the issuer per-site. The
                    // discovery document's issuer and the id_token's iss are therefore the tenant host, and
                    // match the consumer's Authority.

                    var aspnet = options.UseAspNetCore()
                        .EnableTokenEndpointPassthrough()
                        .EnableAuthorizationEndpointPassthrough()
                        .EnableUserInfoEndpointPassthrough()
                        .EnableEndSessionEndpointPassthrough();

                    // Local dev/integration tests run over plain HTTP.
                    if (environment.IsDevelopment())
                    {
                        aspnet.DisableTransportSecurityRequirement();
                    }
                })
                .AddValidation(options =>
                {
                    // Validate tokens this same host issued (shares the server's ephemeral key). Because no
                    // issuer is pinned, validation infers the expected issuer from the CURRENT request host —
                    // the mechanism that rejects a token minted at another host.
                    options.UseLocalServer();
                    options.UseAspNetCore();

                    // A token is also checked against its stored entry, so a revoked token (or one whose
                    // authorization was revoked) stops working at once instead of at its expiry - api/* trusts these.
                    options.EnableTokenEntryValidation();
                    options.EnableAuthorizationEntryValidation();
                });

            return services;
        }

        /// <summary>
        /// Provisions the CURRENT tenant's dedicated OpenIddict store on the request path. Placed
        /// after tenant resolution and BEFORE authentication, because OpenIddict validates a token request
        /// against its store inside the authentication stage. No-op unless OpenIddict is enabled; the
        /// provider split is behind <see cref="IOpenIddictStoreProvisioner"/> (relational EnsureCreated once
        /// per tenant per process, RavenDB a no-op — its session provider ensures at session open).
        /// </summary>
        public static IApplicationBuilder UseOpenIddictStoreProvisioning(this IApplicationBuilder app, IConfiguration configuration)
        {
            if (!configuration.IsOpenIddictEnabled())
            {
                return app;
            }

            return app.UseMiddleware<OpenIddictStoreProvisioningMiddleware>();
        }

        /// <summary>
        /// <c>api/*</c> accepts this host's access tokens (<see cref="OpenIddictApiBearerMiddleware"/>). Placed after
        /// <c>UseAuthentication</c> - OpenIddict's validation context is created there - and before <c>UseAuthorization</c>.
        /// No-op unless OpenIddict is enabled.
        /// </summary>
        public static IApplicationBuilder UseOpenIddictApiBearer(this IApplicationBuilder app, IConfiguration configuration)
        {
            if (!configuration.IsOpenIddictEnabled())
            {
                return app;
            }

            return app.UseMiddleware<OpenIddictApiBearerMiddleware>();
        }

        /// <summary>
        /// Maps the authority endpoints into Stomme's endpoint pipeline. No-op unless OpenIddict is enabled.
        /// </summary>
        public static IEndpointRouteBuilder MapStommeOpenIddict(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
        {
            if (!configuration.IsOpenIddictEnabled())
            {
                return endpoints;
            }

            // Authorization endpoint — the INTERACTIVE hop. OpenIddict has already validated the
            // client_id/redirect_uri against the CURRENT tenant's store (a foreign/unknown client is rejected
            // before this handler). If the end user is authenticated at the authority (the existing Stomme
            // Identity cookie), an authorization code is issued for them; otherwise the Identity cookie scheme
            // is challenged (its configured login path). The user is never a default/forced challenge — only
            // this endpoint challenges, so rendering is unaffected.
            endpoints.MapMethods("/connect/authorize", new[] { "GET", "POST" }, async (HttpContext context) =>
            {
                var request = context.GetOpenIddictServerRequest()
                    ?? throw new InvalidOperationException("The OpenID Connect authorization request cannot be retrieved.");

                var result = await context.AuthenticateAsync(Constants.AuthenticationScheme);
                if (result?.Succeeded != true || result.Principal?.Identity?.IsAuthenticated != true)
                {
                    // Not signed in at the authority: challenge the Identity cookie and come back here.
                    return Results.Challenge(
                        new AuthenticationProperties
                        {
                            RedirectUri = context.Request.PathBase + context.Request.Path +
                                (context.Request.HasFormContentType
                                    ? QueryString.Create(context.Request.Form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)))
                                    : context.Request.QueryString)
                        },
                        new[] { Constants.AuthenticationScheme });
                }

                // Stomme's Identity cookie carries the SITE user id in NameIdentifier (not the IdentityUser
                // GUID) — see UserSecurity.CreateClaimsIdentity — so UserManager.GetUserAsync(principal) would
                // miss. Resolve the IdentityUser by the cookie's user name, then subject = IdentityUser.Id.
                var userManager = context.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
                var userName = result.Principal.Identity?.Name;
                var user = string.IsNullOrEmpty(userName) ? null : await userManager.FindByNameAsync(userName);
                if (user == null)
                {
                    return Results.Forbid(
                        authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
                        properties: new AuthenticationProperties(new Dictionary<string, string>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.ServerError,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                                "The authenticated user could not be resolved."
                        }));
                }

                var userId = await userManager.GetUserIdAsync(user);
                var email = await userManager.GetEmailAsync(user);

                var identity = new ClaimsIdentity(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
                identity.SetClaim(Claims.Subject, userId);
                identity.SetClaim(Claims.Name, userName);
                identity.SetClaim(Claims.PreferredUsername, userName);
                if (!string.IsNullOrEmpty(email))
                {
                    identity.SetClaim(Claims.Email, email);
                }
                identity.SetClaim(SiteClaim, context.GetAlias()?.Name);

                var principal = new ClaimsPrincipal(identity);
                principal.SetScopes(request.GetScopes());
                principal.SetResources(ApiResource);
                principal.SetDestinations(GetDestinations);

                return Results.SignIn(principal, new AuthenticationProperties(),
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            });

            // Token endpoint — client-credentials (B1) AND authorization_code. The client_id is looked
            // up in the CURRENT tenant's OpenIddict store by OpenIddict before this handler runs; an unknown/
            // foreign client is rejected with invalid_client and never reaches here.
            endpoints.MapPost("/connect/token", async (HttpContext context) =>
            {
                var request = context.GetOpenIddictServerRequest();
                if (request == null)
                {
                    return UnsupportedGrant();
                }

                if (request.IsClientCredentialsGrantType())
                {
                    var identity = new ClaimsIdentity(
                        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
                    identity.SetClaim(Claims.Subject, request.ClientId);
                    identity.SetClaim(Claims.Name, request.ClientId);
                    identity.SetClaim(SiteClaim, context.GetAlias()?.Name);

                    var principal = new ClaimsPrincipal(identity);
                    principal.SetScopes(request.GetScopes());
                    principal.SetResources(ApiResource);
                    principal.SetDestinations(_ => new[] { Destinations.AccessToken });

                    return Results.SignIn(principal, new AuthenticationProperties(),
                        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                }

                // authorization_code AND refresh_token (client/scope work b) share one path: OpenIddict has
                // already validated the grant (the code + PKCE verifier, or the refresh token) and restored the
                // principal it originally stored. Re-issue by signing that principal back in — destinations are
                // preserved, so the id_token keeps sub/name/email and a new refresh token is rotated in when the
                // client holds offline_access.
                if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
                {
                    var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                    if (result?.Succeeded != true || result.Principal == null)
                    {
                        return UnsupportedGrant();
                    }

                    return Results.SignIn(result.Principal, new AuthenticationProperties(),
                        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                }

                return UnsupportedGrant();
            });

            // UserInfo endpoint — returns the standard OIDC claims for the access token's subject. The
            // consumer calls this because ExternalLogin sets GetClaimsFromUserInfoEndpoint = true, so a broken
            // userinfo FAILS the login (it is genuinely exercised, not stubbed).
            endpoints.MapMethods("/connect/userinfo", new[] { "GET", "POST" }, async (HttpContext context) =>
            {
                var userManager = context.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
                var subject = context.User.GetClaim(Claims.Subject);
                var user = string.IsNullOrEmpty(subject) ? null : await userManager.FindByIdAsync(subject);
                if (user == null)
                {
                    return Results.Challenge(
                        authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
                        properties: new AuthenticationProperties(new Dictionary<string, string>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                                "The access token is no longer valid."
                        }));
                }

                var claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [Claims.Subject] = subject
                };
                if (context.User.HasScope(Scopes.Profile))
                {
                    var userName = await userManager.GetUserNameAsync(user);
                    claims[Claims.Name] = userName;
                    claims[Claims.PreferredUsername] = userName;

                    // The user's role names ON THE SITE the issuer belongs to - effective, unexpired,
                    // Host implying Administrators and Registered Users - so a relying party can entitle by
                    // role. Always an array, empty for a user without roles there.
                    var alias = await context.RequestServices.GetRequiredService<Stomme.Infrastructure.ITenantManager>().GetAliasAsync();
                    var siteUser = alias is null ? null
                        : await context.RequestServices.GetRequiredService<Stomme.Managers.IUserManager>().GetUserAsync(userName, alias.SiteId);
                    claims[Claims.Role] = (siteUser?.Roles ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                }
                if (context.User.HasScope(Scopes.Email))
                {
                    claims[Claims.Email] = await userManager.GetEmailAsync(user);
                }

                return Results.Json(claims);
            }).RequireAuthorization(new AuthorizationPolicyBuilder(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());

            // End-session endpoint (client/scope work c) — RP-initiated logout. OpenIddict has already
            // validated the (optional) id_token_hint + post_logout_redirect_uri against the client's registered
            // set before this handler runs. Clear Stomme's OWN Identity cookie (so the user is genuinely
            // signed out at the authority, not silently re-authenticated on the next authorize), then hand back
            // to OpenIddict's sign-out handler, which redirects to the validated post_logout_redirect_uri (or
            // renders a completed-logout response when none was supplied).
            endpoints.MapMethods("/connect/endsession", new[] { "GET", "POST" }, async (HttpContext context) =>
            {
                await context.SignOutAsync(Constants.AuthenticationScheme);

                return Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/" },
                    new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
            });

            // Protected resource — proves an issued access token is ACCEPTED by the validation handler on the
            // issuing host, and whether a token minted at another host is REJECTED here.
            endpoints.MapGet("/connect/whoami", (HttpContext context) => Results.Json(new
            {
                subject = context.User.GetClaim(Claims.Subject),
                scopes = context.User.GetScopes()
            })).RequireAuthorization(new AuthorizationPolicyBuilder(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());

            return endpoints;
        }

        /// <summary>Rejects a token request whose grant type this authority does not support.</summary>
        private static IResult UnsupportedGrant() => Results.Forbid(
            authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
            properties: new AuthenticationProperties(new Dictionary<string, string>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.UnsupportedGrantType
            }));

        /// <summary>
        /// Routes each claim to the tokens it belongs in. sub/name/preferred_username/email are exposed in BOTH
        /// the access token and the id_token so the OIDC consumer can read the identity from the id_token
        /// (which is validated before userinfo is called); everything else stays in the access token only.
        /// </summary>
        private static IEnumerable<string> GetDestinations(Claim claim)
        {
            switch (claim.Type)
            {
                case Claims.Subject:
                case Claims.Name:
                case Claims.PreferredUsername:
                case Claims.Email:
                case SiteClaim:
                    return new[] { Destinations.AccessToken, Destinations.IdentityToken };
                default:
                    return new[] { Destinations.AccessToken };
            }
        }

        /// <summary>
        /// Registers (idempotently) a per-tenant loopback OIDC client — authorization_code + PKCE — in the
        /// CURRENT scope's tenant OpenIddict store (EF or Raven). Call inside a request scope
        /// whose tenant has been established (e.g. <c>ITenantManager.SetAliasAsync</c>), exactly like the token
        /// stores resolve their tenant. <paramref name="redirectUri"/> is the site's <c>signin-oidc</c> callback.
        /// </summary>
        public static async Task RegisterLoopbackClientAsync(
            IServiceProvider scopedServices, string clientId, string clientSecret, string redirectUri, params string[] scopes)
        {
            var seed = new OpenIddictApplicationSeed
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                DisplayName = clientId,
                Type = "confidential",
                ConsentType = "implicit", // loopback: no interactive consent screen
                GrantTypes = { "authorization_code" },
                RedirectUris = { redirectUri },
                RequirePkce = true,
                Scopes = new List<string> { Scopes.Email, Scopes.Profile }
            };
            foreach (var scope in scopes ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(scope) && !seed.Scopes.Contains(scope))
                {
                    seed.Scopes.Add(scope);
                }
            }

            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(seed);

            // Idempotent UPSERT (not the old delete-then-create, which orphaned the client's authorizations and
            // tokens on every restart): update the existing client in place, or create it.
            var applications = scopedServices.GetRequiredService<IOpenIddictApplicationManager>();
            var existing = await applications.FindByClientIdAsync(clientId);
            if (existing is null)
            {
                await applications.CreateAsync(descriptor);
            }
            else
            {
                await applications.UpdateAsync(existing, descriptor);
            }
        }
    }
}
