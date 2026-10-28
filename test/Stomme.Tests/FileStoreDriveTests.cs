using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests
{
    /// <summary>
    /// Drives the D80 file store end-to-end and INSPECTS the artefact on disk — the evidence that the file
    /// provider actually persists, not just that the abstract conformance suite is green. It signs the unchanged
    /// Guestbook on a <c>DefaultDBType=File</c> install, then asserts (a) a real <c>{id}.json</c> file exists
    /// under the per-tenant directory, (b) its raw JSON stores the <see cref="AuthorName"/> value object as a
    /// PLAIN STRING (not a nested <c>{"Value":...}</c> object — proving the same scalar-VO serialization the
    /// RavenDB store uses), and (c) a fresh-scope re-read reconstructs the private-ctor aggregate with
    /// <c>AuthorName.Value</c> intact. This is the round-trip the brief flagged as the serialization RISK.
    /// </summary>
    public sealed class FileStoreDriveTests : IAsyncLifetime
    {
        private const string FileType = "Stomme.Database.File.FileDatabase, Stomme.Server";
        private const int ModuleId = 7777;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-file-drive-{Guid.NewGuid():N}");

        private ServiceProvider _services = default!;

        public Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = FileType,
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());
            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            services.AddScoped<IGuestbookRepository, GuestbookRepository>();
            _services = services.BuildServiceProvider();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private async Task<T> InScopeAsync<T>(Func<IGuestbookRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IGuestbookRepository>());
        }

        [Fact]
        public async Task Signing_writes_a_real_json_file_that_stores_the_author_as_a_scalar_and_re_reads_intact()
        {
            var id = await InScopeAsync(async repository =>
            {
                var entry = GuestbookEntryAggregate.Sign(TenantId.Default, ModuleId, "Ada Lovelace", "First to compute.");
                await repository.AddAsync(entry);
                return entry.Id;
            });

            // (a) A real JSON file exists on disk, under a per-tenant / per-aggregate-type directory.
            var files = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories);
            var file = Assert.Single(files);
            Assert.EndsWith(id.ToString("D") + ".json", file, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(GuestbookEntryAggregate), file);

            // (b) The AuthorName value object is stored as a PLAIN STRING, exactly like the RavenDB scalar-VO
            // convention — not a nested object, and the transient DomainEvents collection is not persisted.
            var json = await File.ReadAllTextAsync(file);
            Assert.Contains("\"Ada Lovelace\"", json);
            Assert.DoesNotContain("\"Value\"", json);          // not a {"Value":"..."} nested VO
            Assert.DoesNotContain("DomainEvents", json);       // transient events are ignored

            // (c) A fresh scope reconstructs the private-ctor aggregate with the value object intact.
            var reread = await InScopeAsync(repository => repository.GetByIdAsync(id));
            Assert.NotNull(reread);
            Assert.NotNull(reread!.Author);
            Assert.Equal("Ada Lovelace", reread.Author.Value);
            Assert.Equal("First to compute.", reread.Message);
            Assert.Equal(ModuleId, reread.ModuleId);
        }
    }
}
