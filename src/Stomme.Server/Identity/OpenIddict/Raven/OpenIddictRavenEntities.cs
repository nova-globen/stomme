using System;
using System.Collections.Generic;

namespace Stomme.Identity.OpenIddict.Raven
{
    // RavenDB document entities backing the custom OpenIddict stores (lifted verbatim from the predecessor platform's
    // the predecessor platform.Stores.OpenIddict.RavenDb.Entities — same OpenIddict 7.5.0 surface, zero version gap). They are
    // deliberately serializer-friendly POCOs: scalars, string lists, and string->string dictionaries round-trip
    // through RavenDB's Newtonsoft serializer directly, while the "complex" members OpenIddict models as
    // ImmutableDictionary<string, JsonElement> / JsonWebKeySet are kept as raw JSON strings and (de)serialized by
    // the stores with System.Text.Json. The string Id is both the entity identifier and the RavenDB document id
    // (InitializeStommeStore disables the identity-property convention, so the stores assign ids explicitly).

    /// <summary>An OpenIddict client application (relying party / API resource).</summary>
    public class OpenIddictRavenApplication
    {
        public string Id { get; set; }
        public string ApplicationType { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string ClientType { get; set; }
        public string ConsentType { get; set; }
        public string DisplayName { get; set; }
        public Dictionary<string, string> DisplayNames { get; set; }
        public string JsonWebKeySet { get; set; }
        public List<string> Permissions { get; set; } = new();
        public List<string> PostLogoutRedirectUris { get; set; } = new();
        public string Properties { get; set; }
        public List<string> RedirectUris { get; set; } = new();
        public List<string> Requirements { get; set; } = new();
        public Dictionary<string, string> Settings { get; set; }
        public string ConcurrencyToken { get; set; }
    }

    /// <summary>An OpenIddict authorization (a grant linking a subject, client, and scopes).</summary>
    public class OpenIddictRavenAuthorization
    {
        public string Id { get; set; }
        public string ApplicationId { get; set; }
        public DateTimeOffset? CreationDate { get; set; }
        public string Properties { get; set; }
        public List<string> Scopes { get; set; } = new();
        public string Status { get; set; }
        public string Subject { get; set; }
        public string Type { get; set; }
        public string ConcurrencyToken { get; set; }
    }

    /// <summary>An OpenIddict scope (a named permission an application may request).</summary>
    public class OpenIddictRavenScope
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> Descriptions { get; set; }
        public string DisplayName { get; set; }
        public Dictionary<string, string> DisplayNames { get; set; }
        public string Name { get; set; }
        public string Properties { get; set; }
        public List<string> Resources { get; set; } = new();
        public string ConcurrencyToken { get; set; }
    }

    /// <summary>An OpenIddict token (access/refresh/authorization-code/device-code…).</summary>
    public class OpenIddictRavenToken
    {
        public string Id { get; set; }
        public string ApplicationId { get; set; }
        public string AuthorizationId { get; set; }
        public DateTimeOffset? CreationDate { get; set; }
        public DateTimeOffset? ExpirationDate { get; set; }
        public string Payload { get; set; }
        public string Properties { get; set; }
        public DateTimeOffset? RedemptionDate { get; set; }
        public string ReferenceId { get; set; }
        public string Status { get; set; }
        public string Subject { get; set; }
        public string Type { get; set; }
        public string ConcurrencyToken { get; set; }
    }
}
