using System.Collections.Generic;
using Stomme.Models;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// The declarative client + scope configuration for the per-tenant OpenIddict authority (a+b+c work).
    /// Bound from the <c>OpenIddict</c> configuration section; reconciled into each tenant's store by
    /// <see cref="OpenIddictConfigSeeder"/>. This is the operator-facing way to register a relying party:
    /// declare it in <c>appsettings</c> (source-controlled, promotable across environments) and it is
    /// upserted at startup. Runtime registration through the management controller/admin UI coexists — the
    /// seeder is additive and never deletes a client it did not create.
    /// </summary>
    /// <remarks>
    /// The item shapes (<see cref="OpenIddictApplicationSeed"/>, <see cref="OpenIddictScopeSeed"/>) live in
    /// <c>Stomme.Shared</c>: they are ALSO the admin UI's wire contract, and the Blazor client cannot
    /// reference the server. Config binding is unaffected — the property names are unchanged.
    /// </remarks>
    public sealed class OpenIddictSeedOptions
    {
        public const string SectionName = "OpenIddict";

        /// <summary>Client applications (relying parties) to ensure exist.</summary>
        public List<OpenIddictApplicationSeed> Applications { get; set; } = new();

        /// <summary>Custom scopes to ensure exist (beyond the standard openid/email/profile).</summary>
        public List<OpenIddictScopeSeed> Scopes { get; set; } = new();
    }
}
