using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Database.File;
using Stomme.Database.RavenDb;
using Stomme.Persistence.File;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// A provider entry in the N-way module persistence registry. Given a module aggregate + its id type
    /// and the active <c>Database:DefaultDBType</c>, it registers the CLOSED <c>IRepository&lt;TAggregate, TId&gt;</c>
    /// (and any provider-specific supporting services) for exactly one persistence provider. This REPLACES the
    /// hard-coded <c>relational ? EF : Raven</c> binary that had nowhere for a third store to go: a new provider is
    /// a new <see cref="IModuleRepositoryProvider"/> in the registry, and modules are UNCHANGED.
    /// </summary>
    public interface IModuleRepositoryProvider
    {
        /// <summary>True when this provider persists module aggregates for the given <c>DefaultDBType</c>.</summary>
        bool CanHandle(string databaseType);

        /// <summary>
        /// Registers the closed <c>IRepository&lt;TAggregate, TId&gt;</c> for this provider.
        /// <paramref name="registerRelationalRepository"/> is the caller's overload-specific EF wiring (the shared
        /// <c>ModuleAggregatesDbContext</c> for the 2-arg helper, a module's own <c>DbContext</c> for the 3-arg
        /// helper); the RELATIONAL provider invokes it, while the document/file providers bind their own generic
        /// repository and ignore it.
        /// </summary>
        void Register<TAggregate, TId>(
            IServiceCollection services, string databaseType, Action registerRelationalRepository)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull;
    }

    /// <summary>
    /// The N-way registry keyed on <c>Database:DefaultDBType</c>. It maps the active provider type to the
    /// factory that registers the closed <c>IRepository</c> for it — relational EF providers, RavenDB, and the
    /// file store, each an <see cref="IModuleRepositoryProvider"/>. <see cref="Resolve"/> FAILS LOUDLY on a
    /// <c>DefaultDBType</c> no provider handles, rather than silently defaulting — an unknown provider is a
    /// misconfiguration, not a fall-through to EF or Raven.
    /// <para>
    /// <b>Extensible:</b> a new store provider calls <see cref="Register"/> once (e.g. from a startup) and every
    /// storage-blind module can then persist on it with no module edit — which is exactly how the file store was
    /// added, and what the file-store proof drives.
    /// </para>
    /// </summary>
    public static class ModuleRepositoryProviderRegistry
    {
        // Later registrations take precedence over the three built-ins, so a deployment can override how a given
        // DefaultDBType is persisted. The built-ins are mutually exclusive (a type is relational, OR RavenDB, OR
        // the file store), so their order among themselves is immaterial.
        private static readonly List<IModuleRepositoryProvider> _providers =
        [
            new RelationalModuleRepositoryProvider(),
            new RavenModuleRepositoryProvider(),
            new FileModuleRepositoryProvider(),
        ];

        /// <summary>Adds a provider, taking precedence over the built-ins. The hook for a new store.</summary>
        public static void Register(IModuleRepositoryProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            lock (_providers)
            {
                _providers.Insert(0, provider);
            }
        }

        /// <summary>The provider that handles <paramref name="databaseType"/>, or throws if none does (fail-loud).</summary>
        public static IModuleRepositoryProvider Resolve(string databaseType)
        {
            ArgumentException.ThrowIfNullOrEmpty(databaseType);
            IModuleRepositoryProvider[] snapshot;
            lock (_providers)
            {
                snapshot = _providers.ToArray();
            }

            return snapshot.FirstOrDefault(provider => provider.CanHandle(databaseType))
                ?? throw new InvalidOperationException(
                    $"No module persistence provider handles Database:DefaultDBType '{databaseType}'. " +
                    $"Registered providers: {string.Join(", ", snapshot.Select(provider => provider.GetType().Name))}. " +
                    "Register an IModuleRepositoryProvider for this provider via ModuleRepositoryProviderRegistry.Register " +
                    "(see Stomme.Persistence.File / FileModuleRepositoryProvider for the file-store example).");
        }

        // Whether DefaultDBType resolves to a type assignable to TDatabase, WITHOUT constructing it. Used by the
        // document/file providers to identify their own metadata type precisely, so an unknown type matches
        // nothing and Resolve fails loud rather than falling through to a default.
        internal static bool ResolvesTo<TDatabase>(string databaseType) where TDatabase : class =>
            Type.GetType(databaseType) is { } resolved && typeof(TDatabase).IsAssignableFrom(resolved);
    }

    /// <summary>
    /// The relational (EF Core) module persistence provider — SqlServer/Sqlite/MySql/PostgreSql and any other
    /// <c>IRelationalDatabase</c>. It defers to the caller's overload-specific EF wiring (the shared
    /// <c>ModuleAggregatesDbContext</c> or a module's own context), which is the ONLY branch that differs between
    /// the two <c>AddModuleAggregatePersistence</c> overloads.
    /// </summary>
    internal sealed class RelationalModuleRepositoryProvider : IModuleRepositoryProvider
    {
        public bool CanHandle(string databaseType) =>
            FrameworkStoreProviderSelection.IsRelationalProvider(databaseType);

        public void Register<TAggregate, TId>(
            IServiceCollection services, string databaseType, Action registerRelationalRepository)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
            => registerRelationalRepository();
    }

    /// <summary>
    /// The RavenDB (document store) module persistence provider — the generic <see cref="RavenRepository{TAggregate, TId}"/>
    /// round-trips any aggregate through the shared Newtonsoft conventions <c>AddRavenFrameworkStore</c> installs, so
    /// there is no per-aggregate twin and no relational wiring.
    /// </summary>
    internal sealed class RavenModuleRepositoryProvider : IModuleRepositoryProvider
    {
        public bool CanHandle(string databaseType) =>
            ModuleRepositoryProviderRegistry.ResolvesTo<RavenDatabase>(databaseType);

        public void Register<TAggregate, TId>(
            IServiceCollection services, string databaseType, Action registerRelationalRepository)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
            => services.AddScoped(typeof(IRepository<TAggregate, TId>), typeof(RavenRepository<TAggregate, TId>));
    }

    /// <summary>
    /// The FILE (JSON-per-aggregate) module persistence provider — the third store, proving the registry is
    /// provider-extensible. Binds the generic <see cref="FileRepository{TAggregate, TId}"/> and registers the store
    /// root (<c>ModuleStore:FilePath</c>, default <c>{DataDirectory}/module-store</c>) once. No relational wiring;
    /// the store is schemaless (a directory tree).
    /// </summary>
    internal sealed class FileModuleRepositoryProvider : IModuleRepositoryProvider
    {
        public bool CanHandle(string databaseType) =>
            ModuleRepositoryProviderRegistry.ResolvesTo<FileDatabase>(databaseType);

        public void Register<TAggregate, TId>(
            IServiceCollection services, string databaseType, Action registerRelationalRepository)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
        {
            EnsureFileStoreOptions(services);
            services.AddScoped(typeof(IRepository<TAggregate, TId>), typeof(FileRepository<TAggregate, TId>));
        }

        // Registers the store root ONCE, resolved at composition time from ModuleStore:FilePath (the same
        // IConfigurationRoot-off-the-descriptor-list pattern the shared EF store uses), defaulting to a
        // module-store directory under the app's data directory.
        private static void EnsureFileStoreOptions(IServiceCollection services)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleFileStoreOptions)))
            {
                return;
            }

            var configuration = services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?
                .ImplementationInstance as IConfigurationRoot;

            var configured = configuration?.GetSection("ModuleStore")["FilePath"];
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(DataDirectory(services), "module-store")
                : configured;

            Directory.CreateDirectory(root);
            services.AddSingleton(new ModuleFileStoreOptions { RootPath = root });
        }

        // THIS host's data directory (the |DataDirectory| token the framework/SQLite stores resolve), falling
        // back to an App_Data folder beside the running assembly when there is no host to ask (a minimal
        // test/fixture). Asking the composing host rather than the process is deliberate - see HostDataDirectory.
        private static string DataDirectory(IServiceCollection services)
        {
            var dataDirectory = HostDataDirectory.Resolve(services);
            return string.IsNullOrEmpty(dataDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "App_Data")
                : dataDirectory;
        }
    }
}
