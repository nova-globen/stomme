#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Identity.OpenIddict;
using Stomme.Identity.OpenIddict.Raven;
using Stomme.Models;
using Stomme.Persistence.File;
using Stomme.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OpenIddict.EntityFrameworkCore.Models;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The per-tenant OpenIddict authority's registrations (BL-81, D106 item 3) - registered only while
    /// <c>OpenIddict:Enabled</c> is on. It carries the APPLICATIONS (the clients, with their hashed secrets) and the SCOPES,
    /// and on replace deletes every TOKEN and AUTHORIZATION: a restored installation must not honour a token issued after
    /// the snapshot, nor one it had revoked before - every client signs in again.
    /// </summary>
    /// <remarks>
    /// Each tenant has its own OpenIddict store (D61/D62: <c>{key}_OpenIddict</c>, or derived from the tenant's own), provisioned
    /// here before it is read, because a store is created lazily on the first request that reaches it. Relational: one
    /// transaction per tenant; RavenDB: delete by query, then the documents with their ids.
    /// </remarks>
    public sealed class OpenIddictBackupParticipant(IServiceScopeFactory scopes) : IBackupParticipant
    {
        public const string PartName = "identity:openiddict";

        private static readonly Type[] EfCarried = { typeof(OpenIddictEntityFrameworkCoreScope), typeof(OpenIddictEntityFrameworkCoreApplication) };
        private static readonly Type[] EfRevoked = { typeof(OpenIddictEntityFrameworkCoreToken), typeof(OpenIddictEntityFrameworkCoreAuthorization) };
        private static readonly Type[] RavenCarried = { typeof(OpenIddictRavenScope), typeof(OpenIddictRavenApplication) };
        private static readonly Type[] RavenRevoked = { typeof(OpenIddictRavenToken), typeof(OpenIddictRavenAuthorization) };

        private static readonly MethodInfo EfExport = typeof(OpenIddictBackupParticipant).GetMethod(nameof(EfExportAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo EfDelete = typeof(OpenIddictBackupParticipant).GetMethod(nameof(EfDeleteAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo EfCount = typeof(OpenIddictBackupParticipant).GetMethod(nameof(EfCountAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo RavenExport = typeof(OpenIddictBackupParticipant).GetMethod(nameof(RavenExportAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo Deserialize = typeof(FileStoreSerializer).GetMethod(nameof(FileStoreSerializer.Deserialize), BindingFlags.Public | BindingFlags.Static)!;

        public string Name => PartName;

        /// <summary>The carried entities as the serializer writes them, on either store (BL-170): an OpenIddict upgrade that
        /// reshapes an application or a scope refuses the parts from before it.</summary>
        public string SchemaVersion => Shape.Value;

        private static readonly Lazy<string> Shape = new(() => FileStoreSerializer.Shape(EfCarried.Concat(RavenCarried)));

        /// <summary>After the framework's tables (the tenants), before the modules.</summary>
        public int RestoreOrder => 150;

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            foreach (var tenant in await TenantsAsync(scope))
            {
                await Header(writer.Content, new JObject { ["$tenant"] = tenant.TenantId }, cancellationToken);
                using var tenantScope = await TenantScopeAsync(tenant);
                var provider = tenantScope.ServiceProvider;
                if (provider.GetService<TenantOpenIddictDbContext>() is { } context)
                {
                    foreach (var type in EfCarried)
                    {
                        await Header(writer.Content, new JObject { ["$table"] = type.Name }, cancellationToken);
                        await (Task)EfExport.MakeGenericMethod(type).Invoke(null, new object[] { context, writer.Content, cancellationToken })!;
                    }
                }
                else
                {
                    using var session = await provider.GetRequiredService<IRavenOpenIddictSessionProvider>().OpenSessionAsync(cancellationToken);
                    foreach (var type in RavenCarried)
                    {
                        await Header(writer.Content, new JObject { ["$table"] = type.Name }, cancellationToken);
                        await (Task)RavenExport.MakeGenericMethod(type).Invoke(null, new object[] { session, writer.Content, cancellationToken })!;
                    }
                }
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            var expected = (await TenantsAsync(scope)).Select(tenant => tenant.TenantId).ToList();
            var found = new List<int>();
            var problems = new List<string>();
            var known = EfCarried.Concat(RavenCarried).ToDictionary(type => type.Name, type => type, StringComparer.Ordinal);
            Type? table = null;
            await using var stream = reader.OpenRead();
            try
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    var record = JObject.Parse(json);
                    if (record["$tenant"] is { } tenant)
                    {
                        found.Add(tenant.Value<int>());
                        table = null;
                    }
                    else if (record["$table"] is { } name)
                    {
                        table = known.GetValueOrDefault(name.Value<string>() ?? string.Empty);
                        if (table is null)
                        {
                            problems.Add($"the part names a table this version does not carry: {name}");
                        }
                    }
                    else if (table is null || Deserialize.MakeGenericMethod(table).Invoke(null, new object[] { (record["doc"] ?? record).ToString(Newtonsoft.Json.Formatting.None) }) is null)
                    {
                        problems.Add("a row is not an OpenIddict registration");
                    }

                    if (problems.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or Newtonsoft.Json.JsonException or TargetInvocationException)
            {
                problems.Add("the part is malformed: " + ex.GetType().Name);
            }

            if (!found.SequenceEqual(expected))
            {
                problems.Add($"the snapshot's tenants ({string.Join(", ", found)}) are not this installation's ({string.Join(", ", expected)})");
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            // The part is small (registrations, not tokens): read it per tenant, then replace each tenant's store.
            var byTenant = new Dictionary<int, Dictionary<string, List<JObject>>>();
            Dictionary<string, List<JObject>>? current = null;
            List<JObject>? rows = null;
            await using (var stream = reader.OpenRead())
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    var record = JObject.Parse(json);
                    if (record["$tenant"] is { } tenant)
                    {
                        byTenant[tenant.Value<int>()] = current = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
                    }
                    else if (record["$table"] is { } name)
                    {
                        (current ?? throw new InvalidDataException("A table before any tenant."))[name.Value<string>()!] = rows = new List<JObject>();
                    }
                    else
                    {
                        (rows ?? throw new InvalidDataException("A row before any table.")).Add(record);
                    }
                }
            }

            foreach (var tenant in await TenantsAsync(scope))
            {
                var tables = byTenant.GetValueOrDefault(tenant.TenantId) ?? throw new InvalidOperationException($"{PartName}: the snapshot has no section for tenant {tenant.TenantId}.");
                using var tenantScope = await TenantScopeAsync(tenant);
                var provider = tenantScope.ServiceProvider;
                if (provider.GetService<TenantOpenIddictDbContext>() is { } context)
                {
                    await ReplaceEfAsync(context, tables, cancellationToken);
                }
                else
                {
                    await ReplaceRavenAsync(provider, tables, cancellationToken);
                }
            }
        }

        private static async Task ReplaceEfAsync(TenantOpenIddictDbContext context, Dictionary<string, List<JObject>> tables, CancellationToken cancellationToken)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            foreach (var type in EfRevoked.Concat(EfCarried.Reverse()))
            {
                await (Task<int>)EfDelete.MakeGenericMethod(type).Invoke(null, new object[] { context, cancellationToken })!;
            }

            foreach (var type in EfCarried)
            {
                foreach (var row in tables.GetValueOrDefault(type.Name) ?? new List<JObject>())
                {
                    context.Add(Deserialize.MakeGenericMethod(type).Invoke(null, new object[] { row.ToString(Newtonsoft.Json.Formatting.None) })!);
                }

                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
                var count = await (Task<int>)EfCount.MakeGenericMethod(type).Invoke(null, new object[] { context, cancellationToken })!;
                if (count != (tables.GetValueOrDefault(type.Name)?.Count ?? 0))
                {
                    throw new InvalidOperationException($"{PartName}: {type.Name} holds {count} rows after the replace. The replace is not trusted.");
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task ReplaceRavenAsync(IServiceProvider provider, Dictionary<string, List<JObject>> tables, CancellationToken cancellationToken)
        {
            using var session = await provider.GetRequiredService<IRavenOpenIddictSessionProvider>().OpenSessionAsync(cancellationToken);
            var store = session.Advanced.DocumentStore;
            var database = (session as InMemoryDocumentSessionOperations)?.DatabaseName ?? throw new InvalidOperationException("The OpenIddict session names no database.");
            foreach (var type in RavenRevoked.Concat(RavenCarried))
            {
                var deletion = await store.Operations.ForDatabase(database).SendAsync(
                    new DeleteByQueryOperation(new IndexQuery { Query = $"from '{store.Conventions.FindCollectionName(type)}'" }), token: cancellationToken);
                await deletion.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
            }

            using (var writer = store.OpenAsyncSession(new SessionOptions { Database = database }))
            {
                foreach (var type in RavenCarried)
                {
                    foreach (var row in tables.GetValueOrDefault(type.Name) ?? new List<JObject>())
                    {
                        var document = Deserialize.MakeGenericMethod(type).Invoke(null, new object[] { row["doc"]!.ToString(Newtonsoft.Json.Formatting.None) })!;
                        await writer.StoreAsync(document, row["id"]!.Value<string>(), cancellationToken);
                    }
                }

                await writer.SaveChangesAsync(cancellationToken);
            }

            var statistics = await store.Maintenance.ForDatabase(database).SendAsync(new Raven.Client.Documents.Operations.GetCollectionStatisticsOperation(), cancellationToken);
            foreach (var type in RavenCarried)
            {
                var count = statistics.Collections.TryGetValue(store.Conventions.FindCollectionName(type), out var held) ? held : 0;
                if (count != (tables.GetValueOrDefault(type.Name)?.Count ?? 0))
                {
                    throw new InvalidOperationException($"{PartName}: {type.Name} holds {count} documents after the replace. The replace is not trusted.");
                }
            }
        }

        private async Task<List<Tenant>> TenantsAsync(BackupScope scope)
        {
            using var di = scopes.CreateScope();
            return (await di.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
                .Where(tenant => scope.TenantIds.Contains(tenant.TenantId)).OrderBy(tenant => tenant.TenantId).ToList();
        }

        /// <summary>A DI scope bound to <paramref name="tenant"/>, its OpenIddict store provisioned.</summary>
        private async Task<IServiceScope> TenantScopeAsync(Tenant tenant)
        {
            var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
            await scope.ServiceProvider.GetRequiredService<IOpenIddictStoreProvisioner>().EnsureProvisionedAsync();
            return scope;
        }

        private static Task Header(Stream stream, JObject header, CancellationToken cancellationToken) =>
            BackupRecords.WriteAsync(stream, null, header.ToString(Newtonsoft.Json.Formatting.None), cancellationToken);

        private static async Task EfExportAsync<T>(DbContext context, Stream stream, CancellationToken cancellationToken) where T : class
        {
            foreach (var row in (await context.Set<T>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(row => context.Entry(row).Property("Id").CurrentValue?.ToString(), StringComparer.Ordinal))
            {
                await BackupRecords.WriteAsync(stream, null, JToken.Parse(FileStoreSerializer.Serialize(row)).ToString(Newtonsoft.Json.Formatting.None), cancellationToken);
            }
        }

        private static Task<int> EfDeleteAsync<T>(DbContext context, CancellationToken cancellationToken) where T : class => context.Set<T>().ExecuteDeleteAsync(cancellationToken);

        private static Task<int> EfCountAsync<T>(DbContext context, CancellationToken cancellationToken) where T : class => context.Set<T>().CountAsync(cancellationToken);

        private static async Task RavenExportAsync<T>(IAsyncDocumentSession session, Stream stream, CancellationToken cancellationToken) where T : class
        {
            var rows = new List<(string Id, T Document)>();
            await using (var results = await session.Advanced.StreamAsync(session.Query<T>(collectionName: session.Advanced.DocumentStore.Conventions.FindCollectionName(typeof(T))), cancellationToken))
            {
                while (await results.MoveNextAsync())
                {
                    rows.Add((results.Current.Id, results.Current.Document));
                }
            }

            foreach (var (id, document) in rows.OrderBy(row => row.Id, StringComparer.Ordinal))
            {
                var envelope = new JObject { ["id"] = id, ["doc"] = JToken.Parse(FileStoreSerializer.Serialize(document)) };
                await BackupRecords.WriteAsync(stream, null, envelope.ToString(Newtonsoft.Json.Formatting.None), cancellationToken);
            }
        }
    }
}
