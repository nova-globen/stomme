using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents.Session;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>
    /// Yields a RavenDB async session bound to the current tenant's dedicated OpenIddict database. The four
    /// OpenIddict stores below take this instead of a raw <c>IDocumentStore</c> so every session they open is
    /// per-tenant (D62). See <see cref="TenantRavenOpenIddictSessionProvider"/>.
    /// </summary>
    internal interface IRavenOpenIddictSessionProvider
    {
        ValueTask<IAsyncDocumentSession> OpenSessionAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Wires the Stomme RavenDB OpenIddict stores into the OpenIddict core builder (D62): the four default
    /// entity types and the four <c>IOpenIddict*Store</c> implementations, registered as SCOPED — unlike
    /// the predecessor platform's singletons — because each store resolves the PER-REQUEST tenant through
    /// <see cref="IRavenOpenIddictSessionProvider"/>, and the tenant is a property of the request scope.
    /// </summary>
    internal static class OpenIddictRavenStoreExtensions
    {
        public static OpenIddictCoreBuilder UseStommeRavenOpenIddictStores(this OpenIddictCoreBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.SetDefaultApplicationEntity<OpenIddictRavenApplication>()
                   .SetDefaultAuthorizationEntity<OpenIddictRavenAuthorization>()
                   .SetDefaultScopeEntity<OpenIddictRavenScope>()
                   .SetDefaultTokenEntity<OpenIddictRavenToken>();

            builder.ReplaceApplicationStore<OpenIddictRavenApplication, OpenIddictRavenApplicationStore>(ServiceLifetime.Scoped)
                   .ReplaceAuthorizationStore<OpenIddictRavenAuthorization, OpenIddictRavenAuthorizationStore>(ServiceLifetime.Scoped)
                   .ReplaceScopeStore<OpenIddictRavenScope, OpenIddictRavenScopeStore>(ServiceLifetime.Scoped)
                   .ReplaceTokenStore<OpenIddictRavenToken, OpenIddictRavenTokenStore>(ServiceLifetime.Scoped);

            return builder;
        }
    }
}
