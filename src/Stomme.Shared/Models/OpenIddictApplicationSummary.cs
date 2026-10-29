using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// A registered client application's PUBLIC shape — deliberately no secret, which is hashed at rest and
    /// unrecoverable. Returned by <c>api/OpenIddict/applications</c> and rendered by the admin UI.
    /// </summary>
    /// <remarks>
    /// <see cref="GrantTypes"/>, <see cref="Scopes"/> and <see cref="RequirePkce"/> are DERIVED server-side from
    /// the stored OpenIddict permission/requirement strings, so the seed round-trip (list → edit form → upsert)
    /// never has to re-implement OpenIddict's <c>gt:</c>/<c>scp:</c> prefixes on the client.
    /// </remarks>
    public class OpenIddictApplicationSummary
    {
        public string ClientId { get; set; }
        public string DisplayName { get; set; }

        /// <summary><c>confidential</c> or <c>public</c>.</summary>
        public string ClientType { get; set; }

        /// <summary><c>implicit</c> or <c>explicit</c>.</summary>
        public string ConsentType { get; set; }

        /// <summary>The raw OpenIddict permission strings (endpoints, grants, scopes) — the stored truth.</summary>
        public List<string> Permissions { get; set; } = new List<string>();

        /// <summary>The grant types, unprefixed (<c>authorization_code</c>, <c>client_credentials</c>, …).</summary>
        public List<string> GrantTypes { get; set; } = new List<string>();

        /// <summary>The permitted scopes, unprefixed (<c>openid</c>, <c>email</c>, …).</summary>
        public List<string> Scopes { get; set; } = new List<string>();

        public List<string> RedirectUris { get; set; } = new List<string>();

        public List<string> PostLogoutRedirectUris { get; set; } = new List<string>();

        /// <summary>The sites the client is limited to, by alias; empty for every site of the tenant.</summary>
        public List<string> Sites { get; set; } = new List<string>();

        /// <summary>Whether the stored application carries the PKCE requirement.</summary>
        public bool RequirePkce { get; set; }

        /// <summary>Rebuilds the editable seed for this application. The secret is NOT carried (unrecoverable);
        /// leaving it empty on the upsert keeps the stored one.</summary>
        public OpenIddictApplicationSeed ToSeed()
        {
            return new OpenIddictApplicationSeed
            {
                ClientId = ClientId,
                DisplayName = DisplayName,
                Type = string.IsNullOrEmpty(ClientType) ? "confidential" : ClientType,
                ConsentType = string.IsNullOrEmpty(ConsentType) ? "implicit" : ConsentType,
                GrantTypes = new List<string>(GrantTypes ?? new List<string>()),
                Scopes = new List<string>(Scopes ?? new List<string>()),
                RedirectUris = new List<string>(RedirectUris ?? new List<string>()),
                PostLogoutRedirectUris = new List<string>(PostLogoutRedirectUris ?? new List<string>()),
                Sites = new List<string>(Sites ?? new List<string>()),
                RequirePkce = RequirePkce
            };
        }
    }
}
