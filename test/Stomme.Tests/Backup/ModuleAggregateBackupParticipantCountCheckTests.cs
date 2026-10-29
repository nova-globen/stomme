#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// A replace is TRUSTED only when the store, read back, holds exactly the snapshot's ids. A store
    /// that silently drops a write (a filter, a trigger, a provider quirk) makes the replace throw, never report success.
    /// </summary>
    public class ModuleAggregateBackupParticipantCountCheckTests
    {
        [Fact]
        public async Task A_store_that_drops_a_write_fails_the_replace()
        {
            var store = new DroppingRepository();
            var services = new ServiceCollection();
            services.AddSingleton<IRepository<GuestbookEntryAggregate, Guid>>(store);
            using var provider = services.BuildServiceProvider();
            var participant = new ModuleAggregateBackupParticipant<GuestbookEntryAggregate, Guid, ModuleAggregatesDbContext>(provider.GetRequiredService<IServiceScopeFactory>());
            var scope = new BackupScope("10.2.3", new[] { 1 });

            await store.AddAsync(GuestbookEntryAggregate.Sign(TenantId.Default, 1, "Ada", "kept"));
            await store.AddAsync(GuestbookEntryAggregate.Sign(TenantId.Default, 1, "Grace", "dropped on the way back"));

            using var output = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(output, "count check passphrase"))
            {
                await using (var part = writer.OpenPart(participant.Name, participant.SchemaVersion, "test"))
                {
                    await participant.ExportAsync(new ArchivePartWriter(participant.Name, part), scope, CancellationToken.None);
                }

                await writer.CompleteAsync("10.2.3", scope.TenantIds, DateTime.UtcNow);
            }

            output.Position = 0;
            using var reader = await BackupArchiveReader.OpenAsync(output, "count check passphrase");
            var archivePart = new ArchivePartReader(reader, reader.Manifest.Parts.Single());
            Assert.True((await participant.ValidateAsync(archivePart, scope, CancellationToken.None)).IsValid);

            store.DropOnAdd = "dropped on the way back";
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ReplaceAsync(archivePart, scope, CancellationToken.None));
            Assert.Contains("not trusted", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>An in-memory store that ignores an add whose message is <see cref="DropOnAdd"/>.</summary>
        private sealed class DroppingRepository : IRepository<GuestbookEntryAggregate, Guid>
        {
            private readonly Dictionary<Guid, GuestbookEntryAggregate> _rows = new();

            public string? DropOnAdd { get; set; }

            public Task<GuestbookEntryAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(id));

            public Task<IReadOnlyList<GuestbookEntryAggregate>> GetAllAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<GuestbookEntryAggregate>>(_rows.Values.ToList());

            public Task<IReadOnlyList<GuestbookEntryAggregate>> FindAsync(Expression<Func<GuestbookEntryAggregate, bool>> predicate, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<GuestbookEntryAggregate>>(_rows.Values.Where(predicate.Compile()).ToList());

            public Task AddAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                if (aggregate.Message != DropOnAdd)
                {
                    _rows[aggregate.Id] = aggregate;
                }

                return Task.CompletedTask;
            }

            public Task UpdateAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                _rows[aggregate.Id] = aggregate;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                _rows.Remove(aggregate.Id);
                return Task.CompletedTask;
            }
        }
    }
}
