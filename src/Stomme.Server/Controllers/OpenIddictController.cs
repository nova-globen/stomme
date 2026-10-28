using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Controllers
{
    /// <summary>
    /// Runtime management of the per-tenant OpenIddict authority's client applications and scopes — the
    /// operator counterpart to the declarative <c>OpenIddict:Applications</c>/<c>OpenIddict:Scopes</c> config
    /// seeder (client/scope work a). Everything here is <b>Host-only</b>: creating an OAuth client mints
    /// credentials against the tenant's authority, which is a host-level act (never Admin — the same posture as
    /// the D88 module-settings gate). All actions no-op with <c>404</c> when OpenIddict is disabled, so a stock
    /// install exposes nothing.
    /// </summary>
    /// <remarks>
    /// The managers resolve the CURRENT tenant's OpenIddict store (EF or Raven), which the provisioning
    /// middleware has already ensured exists (BL-55). Client secrets are hashed at rest and unrecoverable —
    /// the summaries never return a secret; a secret is supplied once on create/rotate and shown to the caller
    /// only in that response.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public class OpenIddictController : Controller
    {
        // IConfigurationRoot, NOT IConfiguration: AddStomme registers the fully-merged configuration (the one
        // that includes appsettings.override.json used by the test harness, and every layered source in a real
        // deployment) as IConfigurationRoot. A plain IConfiguration resolves to the host's default config, which
        // is missing the override — so the OpenIddict:Enabled gate would read false under a driven install.
        // SiteRepository and AdminSiteTemplate inject IConfigurationRoot for the same reason.
        private readonly IConfigurationRoot _configuration;
        private readonly IServiceProvider _services;
        private readonly ILogManager _logger;

        public OpenIddictController(IConfigurationRoot configuration, IServiceProvider services, ILogManager logger)
        {
            _configuration = configuration;
            _services = services;
            _logger = logger;
        }

        private bool Enabled => _configuration.IsOpenIddictEnabled();

        private IOpenIddictApplicationManager Applications => _services.GetRequiredService<IOpenIddictApplicationManager>();

        private IOpenIddictScopeManager Scopes => _services.GetRequiredService<IOpenIddictScopeManager>();

        // GET: api/OpenIddict/status
        /// <summary>
        /// Whether the authority is switched on for this install, plus this TENANT's endpoint addresses. The
        /// only action that answers 200 while disabled — the admin UI needs to distinguish "OpenIddict is off"
        /// from "route missing", which the deliberate 404 everywhere else cannot express.
        /// </summary>
        [HttpGet("status")]
        [Authorize(Roles = RoleNames.Host)]
        public IActionResult GetStatus()
        {
            // Issuer is INFERRED from the request host (SetIssuer is deliberately never called, D61), so the
            // answer is per-alias and must be read off THIS request rather than from configuration.
            var issuer = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

            var persistentSigningKey = false;
            if (Enabled)
            {
                using var certificate = OpenIddictCertificateLoader.TryLoadSigningCertificate(_configuration);
                persistentSigningKey = certificate != null;
            }

            return Ok(new OpenIddictStatus
            {
                Enabled = Enabled,
                Issuer = issuer,
                DiscoveryEndpoint = issuer + "/.well-known/openid-configuration",
                AuthorizationEndpoint = issuer + "/connect/authorize",
                TokenEndpoint = issuer + "/connect/token",
                UserInfoEndpoint = issuer + "/connect/userinfo",
                EndSessionEndpoint = issuer + "/connect/endsession",
                JsonWebKeySetEndpoint = issuer + "/.well-known/jwks",
                HasPersistentSigningKey = persistentSigningKey,
                ServiceAccountsEnabled = ServiceAccounts.IsEnabled(_configuration)
            });
        }

        // GET: api/OpenIddict/applications
        [HttpGet("applications")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> GetApplications()
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var summaries = new List<OpenIddictApplicationSummary>();
            await foreach (var application in Applications.ListAsync(cancellationToken: HttpContext.RequestAborted))
            {
                summaries.Add(await Summarize(application, HttpContext.RequestAborted));
            }
            return Ok(summaries);
        }

        // GET: api/OpenIddict/applications/{clientId}
        [HttpGet("applications/{clientId}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> GetApplication(string clientId)
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var application = await Applications.FindByClientIdAsync(clientId, HttpContext.RequestAborted);
            if (application is null)
            {
                return NotFound();
            }
            return Ok(await Summarize(application, HttpContext.RequestAborted));
        }

        // POST: api/OpenIddict/applications  — idempotent upsert from a seed body.
        [HttpPost("applications")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> UpsertApplication([FromBody] OpenIddictApplicationSeed seed)
        {
            if (!Enabled)
            {
                return NotFound();
            }
            if (seed is null || string.IsNullOrWhiteSpace(seed.ClientId))
            {
                return BadRequest("ClientId is required.");
            }

            var existing = await Applications.FindByClientIdAsync(seed.ClientId.Trim(), HttpContext.RequestAborted);

            // A CREATE must carry a secret for a confidential client; an UPDATE need not, because the stored
            // secret is hashed and unrecoverable — an operator editing a redirect URI has nothing to re-type.
            // In that case the STORED value is carried onto the descriptor unchanged, so OpenIddict's update
            // sees an identical secret and leaves it alone (it re-hashes only when the value differs). Supplying
            // a secret on an update therefore ROTATES it, which is the admin UI's documented behaviour.
            OpenIddictApplicationDescriptor descriptor;
            try
            {
                descriptor = OpenIddictConfigSeeder.BuildDescriptor(seed, requireSecret: existing is null);
            }
            catch (System.Exception exception)
            {
                return BadRequest(exception.Message);
            }

            object application;
            if (existing is null)
            {
                application = await Applications.CreateAsync(descriptor, HttpContext.RequestAborted);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "OpenIddict client {ClientId} created", seed.ClientId);
            }
            else
            {
                if (string.IsNullOrEmpty(descriptor.ClientSecret) && !seed.IsPublic)
                {
                    var stored = new OpenIddictApplicationDescriptor();
                    await Applications.PopulateAsync(stored, existing, HttpContext.RequestAborted);
                    descriptor.ClientSecret = stored.ClientSecret;
                }

                await Applications.UpdateAsync(existing, descriptor, HttpContext.RequestAborted);
                application = existing;
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "OpenIddict client {ClientId} updated", seed.ClientId);
            }
            return Ok(await Summarize(application, HttpContext.RequestAborted));
        }

        // DELETE: api/OpenIddict/applications/{clientId}
        [HttpDelete("applications/{clientId}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> DeleteApplication(string clientId)
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var application = await Applications.FindByClientIdAsync(clientId, HttpContext.RequestAborted);
            if (application is null)
            {
                return NotFound();
            }
            await Applications.DeleteAsync(application, HttpContext.RequestAborted);
            _logger.Log(LogLevel.Information, this, LogFunction.Delete, "OpenIddict client {ClientId} deleted", clientId);
            return NoContent();
        }

        // GET: api/OpenIddict/scopes
        [HttpGet("scopes")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> GetScopes()
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var scopes = new List<OpenIddictScopeSeed>();
            await foreach (var scope in Scopes.ListAsync(cancellationToken: HttpContext.RequestAborted))
            {
                scopes.Add(new OpenIddictScopeSeed
                {
                    Name = await Scopes.GetNameAsync(scope, HttpContext.RequestAborted),
                    DisplayName = await Scopes.GetDisplayNameAsync(scope, HttpContext.RequestAborted),
                    Description = await Scopes.GetDescriptionAsync(scope, HttpContext.RequestAborted),
                    Resources = (await Scopes.GetResourcesAsync(scope, HttpContext.RequestAborted)).ToList()
                });
            }
            return Ok(scopes);
        }

        // POST: api/OpenIddict/scopes  — idempotent upsert.
        [HttpPost("scopes")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> UpsertScope([FromBody] OpenIddictScopeSeed seed)
        {
            if (!Enabled)
            {
                return NotFound();
            }
            if (seed is null || string.IsNullOrWhiteSpace(seed.Name))
            {
                return BadRequest("Name is required.");
            }

            var descriptor = new OpenIddictScopeDescriptor
            {
                Name = seed.Name.Trim(),
                DisplayName = seed.DisplayName,
                Description = seed.Description
            };
            foreach (var resource in seed.Resources ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    descriptor.Resources.Add(resource.Trim());
                }
            }

            var existing = await Scopes.FindByNameAsync(descriptor.Name, HttpContext.RequestAborted);
            if (existing is null)
            {
                await Scopes.CreateAsync(descriptor, HttpContext.RequestAborted);
            }
            else
            {
                await Scopes.UpdateAsync(existing, descriptor, HttpContext.RequestAborted);
            }
            return Ok(seed);
        }

        // DELETE: api/OpenIddict/scopes/{name}
        [HttpDelete("scopes/{name}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> DeleteScope(string name)
        {
            if (!Enabled)
            {
                return NotFound();
            }

            var scope = await Scopes.FindByNameAsync(name, HttpContext.RequestAborted);
            if (scope is null)
            {
                return NotFound();
            }
            await Scopes.DeleteAsync(scope, HttpContext.RequestAborted);
            return NoContent();
        }

        // BL-224: an application's SERVICE ACCOUNT on this site. 404 unless OpenIddict:ServiceAccounts:Enabled (and the
        // authority) is on. Host-only, like every client act here.
        // GET: api/OpenIddict/applications/{clientId}/service-account
        [HttpGet("applications/{clientId}/service-account")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> GetServiceAccount(string clientId)
        {
            if (!ServiceAccounts.IsEnabled(_configuration))
            {
                return NotFound();
            }

            var application = await Applications.FindByClientIdAsync(clientId ?? "", HttpContext.RequestAborted);
            return application is null ? NotFound() : Ok(await DescribeServiceAccountAsync(clientId));
        }

        // POST: api/OpenIddict/applications/{clientId}/service-account
        // Creates the account the server alone makes: the derived name, a password nobody is told, an .invalid address, no
        // notification, and a lockout that never ends - then an administrator grants it roles and permission rows.
        [HttpPost("applications/{clientId}/service-account")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> CreateServiceAccount(string clientId)
        {
            if (!ServiceAccounts.IsEnabled(_configuration))
            {
                return NotFound();
            }

            var cancellationToken = HttpContext.RequestAborted;
            var application = await Applications.FindByClientIdAsync(clientId ?? "", cancellationToken);
            if (application is null)
            {
                return NotFound();
            }

            var permissions = await Applications.GetPermissionsAsync(application, cancellationToken);
            if (!string.Equals(await Applications.GetClientTypeAsync(application, cancellationToken), ClientTypes.Confidential, StringComparison.Ordinal)
                || !permissions.Contains(Permissions.GrantTypes.ClientCredentials)
                || !permissions.Contains(Permissions.Prefixes.Scope + OpenIddictApiBearerMiddleware.ApiScope))
            {
                return BadRequest(new { error = "A service account is for a confidential application that holds the client-credentials grant and the api scope." });
            }

            var described = await DescribeServiceAccountAsync(clientId);
            if (described.Exists || described.NameTaken)
            {
                return Conflict(new { error = described.Exists
                    ? "This application already has a service account."
                    : $"An account named '{described.Username}' already exists and was not made by the server; it is never used for an application." });
            }

            var alias = await _services.GetRequiredService<ITenantManager>().GetAliasAsync();
            var displayName = await Applications.GetDisplayNameAsync(application, cancellationToken);
            var users = _services.GetRequiredService<Stomme.Managers.IUserManager>();
            var created = await users.AddUser(new User
            {
                SiteId = alias.SiteId,
                Username = described.Username,
                Password = ServiceAccounts.NewUndisclosedPassword(),
                Email = ServiceAccounts.EmailFor(described.Username),
                DisplayName = "Service account: " + (string.IsNullOrWhiteSpace(displayName) ? clientId : displayName),
                EmailConfirmed = true,
                SuppressNotification = true,
                IsAuthenticated = false
            });
            if (created is null)
            {
                return BadRequest(new { error = "The service account was refused - see the event log." });
            }

            var identityUsers = _services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
            var identityUser = await identityUsers.FindByNameAsync(described.Username);
            if (identityUser is null || !await ServiceAccounts.LockOutForGoodAsync(identityUsers, identityUser))
            {
                // never leave an account that can sign in behind
                await users.DeleteUser(created.UserId, alias.SiteId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "The service account could not be locked out of sign-in, so it was removed." });
            }

            _logger.Log(LogLevel.Information, this, LogFunction.Create, "Service Account {Username} Created For OpenIddict Client {ClientId}", described.Username, clientId);
            return Ok(await DescribeServiceAccountAsync(clientId));
        }

        // DELETE: api/OpenIddict/applications/{clientId}/service-account
        [HttpDelete("applications/{clientId}/service-account")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> DeleteServiceAccount(string clientId)
        {
            if (!ServiceAccounts.IsEnabled(_configuration) || string.IsNullOrWhiteSpace(clientId))
            {
                return NotFound();
            }

            var described = await DescribeServiceAccountAsync(clientId);
            if (!described.Exists || described.UserId is null)
            {
                return NotFound();
            }

            var alias = await _services.GetRequiredService<ITenantManager>().GetAliasAsync();
            await _services.GetRequiredService<Stomme.Managers.IUserManager>().DeleteUser(described.UserId.Value, alias.SiteId);
            _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Service Account {Username} Of OpenIddict Client {ClientId} Deleted", described.Username, clientId);
            return NoContent();
        }

        private async Task<OpenIddictServiceAccount> DescribeServiceAccountAsync(string clientId)
        {
            var username = ServiceAccounts.UsernameFor(clientId);
            var identityUsers = _services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
            var identityUser = await identityUsers.FindByNameAsync(username);
            var madeByServer = identityUser != null && await ServiceAccounts.IsLockedOutForGoodAsync(identityUsers, identityUser);
            User user = null;
            if (madeByServer)
            {
                var alias = await _services.GetRequiredService<ITenantManager>().GetAliasAsync();
                user = await _services.GetRequiredService<Stomme.Managers.IUserManager>().GetUserAsync(username, alias.SiteId);
            }

            return new OpenIddictServiceAccount
            {
                ClientId = clientId,
                Username = username,
                Exists = user is { IsDeleted: false },
                UserId = user?.UserId,
                NameTaken = identityUser != null && !madeByServer
            };
        }

        private async Task<OpenIddictApplicationSummary> Summarize(object application, CancellationToken cancellationToken)
        {
            var permissions = (await Applications.GetPermissionsAsync(application, cancellationToken)).ToList();
            var requirements = await Applications.GetRequirementsAsync(application, cancellationToken);

            // Unprefix the grants/scopes HERE, where OpenIddict's constants live, so the admin UI can round-trip
            // a summary back into an editable seed without re-implementing the gt:/scp: prefixes client-side.
            return new OpenIddictApplicationSummary
            {
                ClientId = await Applications.GetClientIdAsync(application, cancellationToken),
                DisplayName = await Applications.GetDisplayNameAsync(application, cancellationToken),
                ClientType = await Applications.GetClientTypeAsync(application, cancellationToken),
                ConsentType = await Applications.GetConsentTypeAsync(application, cancellationToken),
                Permissions = permissions,
                GrantTypes = Unprefix(permissions, Permissions.Prefixes.GrantType),
                Scopes = Unprefix(permissions, Permissions.Prefixes.Scope),
                RedirectUris = (await Applications.GetRedirectUrisAsync(application, cancellationToken)).ToList(),
                PostLogoutRedirectUris = (await Applications.GetPostLogoutRedirectUrisAsync(application, cancellationToken)).ToList(),
                RequirePkce = requirements.Contains(Requirements.Features.ProofKeyForCodeExchange)
            };
        }

        private static List<string> Unprefix(List<string> permissions, string prefix)
        {
            return permissions
                .Where(permission => permission.StartsWith(prefix, StringComparison.Ordinal))
                .Select(permission => permission.Substring(prefix.Length))
                .ToList();
        }
    }
}
