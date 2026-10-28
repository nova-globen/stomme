namespace Stomme.Models
{
    /// <summary>
    /// Whether this install's per-tenant OpenIddict authority is switched on, plus the endpoint addresses a
    /// relying party needs. Lets the admin UI say "the IdP is disabled — set OpenIddict:Enabled" instead of
    /// inferring it from a 404 (every other action 404s when disabled, which is indistinguishable from a
    /// missing route).
    /// </summary>
    /// <remarks>
    /// The issuer is INFERRED from the request host — <c>SetIssuer</c> is deliberately never called (D61), which
    /// is what gives each tenant a distinct <c>iss</c> for free. So these values are per-alias, resolved from the
    /// request that asked, not from configuration.
    /// </remarks>
    public class OpenIddictStatus
    {
        /// <summary>The <c>OpenIddict:Enabled</c> gate. Everything else is meaningless when false.</summary>
        public bool Enabled { get; set; }

        /// <summary>The issuer this tenant's tokens carry — the request's scheme + host.</summary>
        public string Issuer { get; set; }

        public string DiscoveryEndpoint { get; set; }
        public string AuthorizationEndpoint { get; set; }
        public string TokenEndpoint { get; set; }
        public string UserInfoEndpoint { get; set; }
        public string EndSessionEndpoint { get; set; }
        public string JsonWebKeySetEndpoint { get; set; }

        /// <summary>Whether a persistent signing certificate is loaded (false = ephemeral key, rotates per restart).</summary>
        public bool HasPersistentSigningKey { get; set; }

        /// <summary>BL-224: the <c>OpenIddict:ServiceAccounts:Enabled</c> gate - whether a client-credentials application may have a service account.</summary>
        public bool ServiceAccountsEnabled { get; set; }
    }
}
