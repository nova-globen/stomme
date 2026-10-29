using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests
{
    /// <summary>
    /// The SAME Guestbook conformance suite against the FILE store, the third provider, alongside the EF
    /// (SQLite) and live RavenDB fixtures. Composed EXACTLY as the module's <c>ServerStartup</c> composes it on a
    /// <c>DefaultDBType=File</c> install: the N-way module persistence registry selects the file provider from the
    /// configured type, and the UNCHANGED storage-blind <see cref="GuestbookRepository"/> is the closed
    /// <see cref="IGuestbookRepository"/> over the generic <c>FileRepository</c>. EF == Raven == File must produce
    /// the SAME observable results — including the <see cref="AuthorName"/> value object surviving the JSON
    /// round-trip, which is a serialization risk, and this fixture DRIVES against real
    /// files on disk.
    /// <para>
    /// This is the proof that the persistence registry is genuinely provider-extensible: a real module runs on a
    /// third store it has never heard of with ZERO module edits.
    /// </para>
    /// </summary>
    public sealed class FileGuestbookStoreConformanceTests : GuestbookStoreConformanceTests
    {
        private const string FileType = "Stomme.Database.File.FileDatabase, Stomme.Server";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-guestbook-file-conformance-{Guid.NewGuid():N}");

        /// <summary>Value-object equality is case-insensitive on this store.</summary>

        protected override bool ValueObjectEqualityIsCaseInsensitive => true;


        protected override void AddStore(IServiceCollection services)
        {
            // Compose exactly as the module's ServerStartup does on a File install: the STORAGE-BLIND overload
            // The registry reads Database:DefaultDBType, resolves the file provider, and binds the
            // generic FileRepository; ModuleStore:FilePath points the store at this fixture's throwaway directory.
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = FileType,
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());

            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            services.AddScoped<IGuestbookRepository, GuestbookRepository>();
        }

        protected override Task DisposeStoreAsync()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }
}
