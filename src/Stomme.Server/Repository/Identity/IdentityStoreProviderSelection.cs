using System;
using System.Linq;
using Stomme.Repository.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Repository.Identity
{
    /// <summary>
    /// Resolve-time selection between the two ASP.NET Identity user stores (D54b), the identity-side twin of
    /// <see cref="FrameworkStoreProviderSelection"/>. <c>AddIdentityCore&lt;IdentityUser&gt;()
    /// .AddEntityFrameworkStores&lt;TenantDBContext&gt;()</c> registers EF's <c>UserOnlyStore</c> for
    /// <see cref="IUserStore{IdentityUser}"/>; this replaces that ONE registration with a factory that picks the
    /// EF store or the hand-written <see cref="RavenIdentityUserStore"/> at first resolve, keyed on
    /// <c>IConfigManager.GetDatabaseType()</c> — exactly as the framework store is selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why swapping only <see cref="IUserStore{IdentityUser}"/> is enough.</b> <c>UserManager</c> obtains the
    /// single <c>IUserStore</c> instance and CASTS it to each facet it needs (<c>GetPasswordStore()</c> does
    /// <c>Store as IUserPasswordStore&lt;TUser&gt;</c>, and so on). Both concrete stores implement all eight
    /// interfaces, so one swap carries email/password/security-stamp/lockout/two-factor/logins/passkeys with it.
    /// </para>
    /// <para>
    /// <b>Why resolve-time, not composition-time.</b> The database type is written mid-install, after this
    /// container is built; on the fresh-install boot the config is empty and reads as the relational default, so
    /// a composition-time branch could never see the Raven provider the one install pass writes to. Reading the
    /// type fresh on each resolve sees the Raven type by the time <c>CreateSiteAsync</c>'s host-account step
    /// resolves <c>UserManager</c> → <see cref="IUserStore{IdentityUser}"/>.
    /// </para>
    /// <para>
    /// <b>Relational is byte-for-byte unchanged.</b> On a relational install the factory rebuilds EF's captured
    /// <c>UserOnlyStore</c> implementation type with <c>ActivatorUtilities</c> (same ctor deps: the
    /// <c>TenantDBContext</c> and the <c>IdentityErrorDescriber</c>), at the same lifetime EF registered it, so
    /// login on PostgreSQL/SQLite resolves the exact same store it does today. The Raven branch — and therefore
    /// the lazy <c>IDocumentStore</c> — is never touched on a relational install.
    /// </para>
    /// </remarks>
    public static class IdentityStoreProviderSelection
    {
        /// <summary>
        /// Collapses the EF <see cref="IUserStore{IdentityUser}"/> registration and the Raven store into a single
        /// resolve-time-selecting registration. Call AFTER <c>AddIdentityCore().AddEntityFrameworkStores()</c>.
        /// </summary>
        public static IServiceCollection SelectIdentityUserStoreProviderByConfig(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            // The Raven store, resolvable so the factory (and any test) can obtain it by type. Scoped to match
            // EF's UserStore lifetime.
            services.TryAddScoped<RavenIdentityUserStore>();

            var efDescriptor = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IUserStore<IdentityUser>));
            if (efDescriptor?.ImplementationType == null)
            {
                // No concrete EF registration to swap (e.g. a future change to a factory registration). Leaving
                // it untouched is safer than guessing; the framework-store selection is unaffected.
                return services;
            }

            var efImplementationType = efDescriptor.ImplementationType;
            var lifetime = efDescriptor.Lifetime;

            services.Remove(efDescriptor);

            services.Add(new ServiceDescriptor(
                typeof(IUserStore<IdentityUser>),
                provider => FrameworkStoreProviderSelection.IsRelationalProviderConfigured(provider)
                    ? ActivatorUtilities.CreateInstance(provider, efImplementationType)
                    : provider.GetRequiredService<RavenIdentityUserStore>(),
                lifetime));

            return services;
        }
    }
}
