using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// Registers the HtmlText module's store on the persistence seam (D55) — the REFERENCE the module template
    /// copies. Both providers register EVERY module closed store contract, exactly as the framework's two
    /// extensions do, so the module-scoped reflective gate (<c>ModuleStoreRegistrationTests</c>) can hold each
    /// provider to the same interface set with no hand-maintained list.
    /// </summary>
    /// <remarks>
    /// Split into two provider methods, then collapsed by <c>SelectStoreProviderByConfig</c> at resolve time —
    /// mirroring the framework's <c>AddStommeFrameworkStore</c> / <c>AddRavenFrameworkStore</c> /
    /// <c>SelectFrameworkStoreProviderByConfig</c>. D17 rule 1: only CLOSED module contracts are registered here;
    /// the ambient open-generic <c>IRepository&lt;,&gt;</c> and <c>IDocumentSessionFactory</c> are left to
    /// <c>AddFeatureManagement</c>.
    /// </remarks>
    public static class HtmlTextStoreServiceExtensions
    {
        /// <summary>The implementation namespace both twins share — the key <see cref="AddHtmlTextStore"/> collapses on.</summary>
        internal const string ImplementationNamespace = "Stomme.Modules.HtmlText.Repository";

        /// <summary>The relational (EF Core) twin of the module's store and session factory.</summary>
        public static IServiceCollection AddHtmlTextEfStore(this IServiceCollection services)
        {
            services.AddTransient<IHtmlTextStore, EfHtmlTextStore>();
            services.AddTransient<IHtmlTextStoreSessionFactory, EfHtmlTextStoreSessionFactory>();
            return services;
        }

        /// <summary>
        /// The RavenDB twin of the module's store and session factory. Reuses the host's Raven singletons from
        /// <c>AddRavenFrameworkStore</c> — no second RavenDB DI path.
        /// </summary>
        public static IServiceCollection AddHtmlTextRavenStore(this IServiceCollection services)
        {
            services.AddTransient<IHtmlTextStore, RavenHtmlTextStore>();
            services.AddTransient<IHtmlTextStoreSessionFactory, RavenHtmlTextStoreSessionFactory>();
            return services;
        }

        /// <summary>
        /// Registers both provider twins and collapses each into a single resolve-time-selecting registration keyed
        /// on the configured database type. Call from the module's <c>IServerStartup.ConfigureServices</c>.
        /// </summary>
        public static IServiceCollection AddHtmlTextStore(this IServiceCollection services)
        {
            services.AddHtmlTextEfStore();
            services.AddHtmlTextRavenStore();
            services.SelectStoreProviderByConfig(ImplementationNamespace);
            return services;
        }
    }
}
