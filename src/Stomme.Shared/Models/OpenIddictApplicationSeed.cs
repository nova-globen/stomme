using System;
using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// One relying party (OAuth/OIDC client application) of the per-tenant OpenIddict authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is BOTH the declarative config shape (bound from <c>OpenIddict:Applications</c> and reconciled by
    /// the config seeder) and the wire shape the admin UI POSTs to <c>api/OpenIddict/applications</c> — one
    /// type, so a client registered through the wizard-era config file and one registered through the admin UI
    /// are the same thing. It lives in <c>Stomme.Shared</c> because the Blazor client cannot reference the
    /// server (the Model → Controller → IService contract).
    /// </para>
    /// <para>
    /// Secrets are hashed at rest and unrecoverable, so a summary never carries one. On the admin UI's EDIT
    /// path an empty <see cref="ClientSecret"/> means <b>keep the stored secret</b>; supplying one ROTATES it.
    /// </para>
    /// </remarks>
    public class OpenIddictApplicationSeed
    {
        /// <summary>The OAuth <c>client_id</c>. Required; identifies the application in the tenant store.</summary>
        public string ClientId { get; set; }

        /// <summary>
        /// A literal client secret. Prefer <see cref="ClientSecretEnv"/> in config so the value never enters the
        /// file. Ignored for a public client (<see cref="Type"/> = <c>public</c>).
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>Name of an environment variable holding the client secret; takes precedence over the literal.</summary>
        public string ClientSecretEnv { get; set; }

        /// <summary>Human-readable name shown on consent screens / the admin UI. Defaults to the client id.</summary>
        public string DisplayName { get; set; }

        /// <summary><c>confidential</c> (default, requires a secret) or <c>public</c> (SPA/native, PKCE only).</summary>
        public string Type { get; set; } = "confidential";

        /// <summary><c>implicit</c> (default, no consent screen for a first-party client) or <c>explicit</c>.</summary>
        public string ConsentType { get; set; } = "implicit";

        /// <summary>Allowed grants: <c>authorization_code</c>, <c>client_credentials</c>, <c>refresh_token</c>.</summary>
        public List<string> GrantTypes { get; set; } = new List<string>();

        /// <summary>Allowed scopes (e.g. <c>openid</c>, <c>email</c>, <c>profile</c>, <c>offline_access</c>, custom).</summary>
        public List<string> Scopes { get; set; } = new List<string>();

        public List<string> RedirectUris { get; set; } = new List<string>();

        public List<string> PostLogoutRedirectUris { get; set; } = new List<string>();

        /// <summary>
        /// The sites this client may be used at, by alias (<c>example.com/shop</c>). Empty: every site of the tenant. A
        /// client that names sites is refused (<c>unauthorized_client</c>) at any other, even one sharing its tenant.
        /// </summary>
        public List<string> Sites { get; set; } = new List<string>();

        /// <summary>Require PKCE on the authorization-code flow. Defaults true (the secure default).</summary>
        public bool RequirePkce { get; set; } = true;

        public bool IsPublic =>
            !string.IsNullOrWhiteSpace(Type) && Type.Trim().Equals("public", StringComparison.OrdinalIgnoreCase);

        public bool ConsentIsExplicit =>
            !string.IsNullOrWhiteSpace(ConsentType) && ConsentType.Trim().Equals("explicit", StringComparison.OrdinalIgnoreCase);

        /// <summary>Resolves the effective secret: the env var (if named + present) else the literal.</summary>
        public string ResolveSecret()
        {
            if (!string.IsNullOrWhiteSpace(ClientSecretEnv))
            {
                var fromEnv = Environment.GetEnvironmentVariable(ClientSecretEnv.Trim());
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    return fromEnv;
                }
            }

            return ClientSecret;
        }
    }
}
