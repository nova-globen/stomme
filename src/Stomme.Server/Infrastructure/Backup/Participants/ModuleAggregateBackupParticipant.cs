#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Persistence.File;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The participant every storage-blind module aggregate gets with ZERO module code (BL-81, D106 item 3): registered by
    /// <c>AddModuleAggregatePersistence</c> unless the aggregate is <see cref="BackupExcludedAttribute"/>. It works through
    /// the aggregate's own <see cref="IRepository{TAggregate,TId}"/>, so it is as storage-blind as the module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where a row lives.</b> The stores partition differently (D106 refutation 2): EF and RavenDB filter only
    /// tenant-OWNED rows, and system scope sees every tenant's; the File store keeps a directory PER AMBIENT TENANT for every
    /// aggregate, owned or not. So a row is read in system scope and in each installation tenant's scope, and its HOME is
    /// recorded with it: a tenant-owned row's own tenant; otherwise none when every scope sees it (EF, RavenDB), or the one
    /// scope that does (File). A restore writes each row back in its home scope.
    /// </para>
    /// <para>
    /// <b>Replace, not merge.</b> Everything visible in any of those scopes is deleted, the part is added, and the store is
    /// read back: the same ids, or the replace throws. On a relational store it runs in ONE transaction on the aggregate's
    /// context, so a failure leaves the rows as they were; RavenDB and File have none (D106 item 7 - the safety snapshot
    /// covers them).
    /// </para>
    /// </remarks>
    public sealed class ModuleAggregateBackupParticipant<TAggregate, TId, TContext>(IServiceScopeFactory scopes) : IBackupParticipant
        where TAggregate : AggregateRoot<TId>
        where TId : notnull
        where TContext : DbContext
    {
        public string Name => "module-aggregate:" + typeof(TAggregate).FullName;

        /// <summary>The aggregate as the serializer writes it (BL-170): a module that adds a property refuses its older parts.</summary>
        public string SchemaVersion => Shape;

        private static readonly string Shape = FileStoreSerializer.Shape(new[] { typeof(TAggregate) });

        /// <summary>After the framework's parts: module rows refer to sites and users, never the other way round.</summary>
        public int RestoreOrder => 500;

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            foreach (var (_, home, row) in await ReadEverywhereAsync(di.ServiceProvider, scope, cancellationToken))
            {
                await BackupRecords.WriteAsync(writer.Content, home, FileStoreSerializer.Serialize(row), cancellationToken);
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            // A record may live in any tenant the SNAPSHOT holds; a partial restore (BL-171) skips the ones outside its scope.
            var allowed = TenantScopes(scope.SnapshotTenantIds).ToHashSet();
            var seen = new HashSet<TId>();
            var problems = new List<string>();
            var index = 0;
            await using var stream = reader.OpenRead();
            try
            {
                await foreach (var (home, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    index++;
                    TAggregate? row;
                    try
                    {
                        row = FileStoreSerializer.Deserialize<TAggregate>(json);
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        row = null;
                    }

                    if (row is null)
                    {
                        problems.Add($"record {index} is not a {typeof(TAggregate).Name}");
                    }
                    else if (!seen.Add(row.Id))
                    {
                        problems.Add($"record {index} repeats id {row.Id}");
                    }
                    else if (home is { } tenant && !allowed.Contains(tenant))
                    {
                        problems.Add($"record {index} lives in a tenant that is not in the snapshot");
                    }
                    else if (row is ITenantOwned owned && owned.TenantId.Value != home)
                    {
                        problems.Add($"record {index} is owned by a tenant other than the one it is filed under");
                    }

                    if (problems.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (System.IO.InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            // What to delete is read in a scope of its OWN: RavenDB remembers the change vector of every document a scope
            // loads, and a re-add of a deleted id in that scope is then refused as a concurrency conflict (measured).
            List<(TId Id, Guid? Home, TAggregate Row)> existing;
            using (var reading = scopes.CreateScope())
            {
                existing = await ReadEverywhereAsync(reading.ServiceProvider, scope, cancellationToken);
            }

            using var di = scopes.CreateScope();
            var provider = di.ServiceProvider;
            var repository = provider.GetRequiredService<IRepository<TAggregate, TId>>();
            var tenantScopes = provider.GetService<ITenantScopeFactory>();

            // A relational store gets one transaction for the whole replace - the repository's context is this scope's.
            var context = repository is EfRepository<TAggregate, TId> ? provider.GetService<TContext>() : null;
            await using var transaction = context is null ? null : await context.Database.BeginTransactionAsync(cancellationToken);

            // BL-171: a partial restore touches the selected tenants' rows only - an installation-wide row (no home) and
            // another tenant's row stay exactly as they are, on both sides of the replace.
            var restoring = TenantScopes(scope.TenantIds).ToHashSet();
            bool InScope(Guid? home) => !scope.IsPartial || (home is { } tenant && restoring.Contains(tenant));

            foreach (var (_, home, row) in existing.Where(entry => InScope(entry.Home)))
            {
                using (tenantScopes?.BeginScope(home))
                {
                    await repository.DeleteAsync(row, cancellationToken);
                }
            }

            var written = new HashSet<TId>();
            await using (var stream = reader.OpenRead())
            {
                await foreach (var (home, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    if (!InScope(home))
                    {
                        continue;
                    }

                    var row = FileStoreSerializer.Deserialize<TAggregate>(json)
                        ?? throw new InvalidOperationException($"A {typeof(TAggregate).Name} record did not deserialise.");
                    using (tenantScopes?.BeginScope(home))
                    {
                        await repository.AddAsync(row, cancellationToken);
                    }

                    written.Add(row.Id);
                }
            }

            var after = (await ReadEverywhereAsync(provider, scope, cancellationToken)).Where(entry => InScope(entry.Home)).ToList();
            if (after.Count != written.Count || after.Any(entry => !written.Contains(entry.Id)))
            {
                throw new InvalidOperationException(
                    $"{Name}: after the replace the store holds {after.Count} rows, the snapshot {written.Count}. The replace is not trusted.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        /// <summary>Every row, read in system scope and in each tenant's, with the scope it lives in - ordered by id.</summary>
        private static async Task<List<(TId Id, Guid? Home, TAggregate Row)>> ReadEverywhereAsync(IServiceProvider provider, BackupScope scope, CancellationToken cancellationToken)
        {
            var repository = provider.GetRequiredService<IRepository<TAggregate, TId>>();
            var tenantScopes = provider.GetService<ITenantScopeFactory>();
            var readIn = tenantScopes is null ? new Guid?[] { null } : new Guid?[] { null }.Concat(TenantScopes(scope).Select(tenant => (Guid?)tenant)).ToArray();

            var seen = new Dictionary<TId, (TAggregate Row, List<Guid?> In)>();
            foreach (var tenant in readIn)
            {
                using (tenantScopes?.BeginScope(tenant))
                {
                    foreach (var row in await repository.GetAllAsync(cancellationToken))
                    {
                        if (seen.TryGetValue(row.Id, out var entry))
                        {
                            entry.In.Add(tenant);
                        }
                        else
                        {
                            seen[row.Id] = (row, new List<Guid?> { tenant });
                        }
                    }
                }
            }

            return seen
                .Select(pair => (pair.Key, Home(pair.Value.Row, pair.Value.In, readIn.Length, tenantScopes is not null), pair.Value.Row))
                .OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
                .ToList();
        }

        private static Guid? Home(TAggregate row, List<Guid?> seenIn, int scopeCount, bool scoped)
        {
            if (row is ITenantOwned owned)
            {
                return owned.TenantId.Value;
            }

            return !scoped || seenIn.Count == scopeCount ? null : seenIn[0];
        }

        private static IEnumerable<Guid> TenantScopes(BackupScope scope) => TenantScopes(scope.TenantIds);

        private static IEnumerable<Guid> TenantScopes(IReadOnlyList<int> tenantIds) =>
            tenantIds.Select(FrameworkTenantId.ToTenantIdOrNull).Where(id => id is not null).Select(id => id!.Value);
    }
}
