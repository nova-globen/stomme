#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Persistence.File;
using Stomme.Persistence.RavenDb;
using Stomme.Repository;
using Stomme.Repository.Identity;
using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The RavenDB half of <c>framework:databases</c>: the framework store's
    /// collections and the Identity users, in the framework database and - when it is a different one - the master
    /// database, each document with its ORIGINAL id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Replace.</b> Per database: every carried collection and the search index are deleted by query, the documents
    /// bulk-inserted with their ids, each collection's count checked from the database's own statistics - then the HiLo.
    /// RavenDB has no transaction across collections; the safety snapshot is the answer.
    /// </para>
    /// <para>
    /// <b>HiLo (refutation 14).</b> Ids are reserved in ranges per collection, recorded server-side in
    /// <c>Raven/Hilo/{tag}</c> and CACHED in this process. After documents come back with their original ids, the next
    /// range must start past the highest: the server document's <c>Max</c> is raised to at least it (created when missing -
    /// a fresh database has none, and would start at 1), and the process's cached ranges are dropped
    /// (<see cref="RavenHiLoIdGenerator.Forget"/>).
    /// </para>
    /// </remarks>
    public sealed partial class FrameworkDatabasesBackupParticipant
    {
        private static readonly MethodInfo RavenExportMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(RavenExportCollectionAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo RavenInsertMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(RavenInsertAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static bool IsRavenInstall(IServiceProvider provider)
        {
            var type = Type.GetType(provider.GetRequiredService<IConfigManager>().GetDatabaseType() ?? string.Empty);
            return type is not null && type.Name == "RavenDatabase";
        }

        /// <summary>
        /// The Raven databases of this installation and what each carries - framework first, then a separate master, then
        /// each database of its own that tenants have, labelled by the tenants in it, so a snapshot restores only into
        /// an installation whose tenants sit in the same databases.
        /// </summary>
        private static List<(string Label, string Database, IReadOnlyList<Type> Types, IReadOnlyList<int> Tenants)> RavenLayout(IServiceProvider provider)
        {
            var store = provider.GetRequiredService<IDocumentStore>();
            var resolver = provider.GetService<IRavenTenantDatabaseResolver>();
            var routed = resolver is TenantRavenDatabaseResolver ? provider.GetRequiredService<RavenTenantDatabases>() : null;
            if (resolver is not null && resolver is not NullRavenTenantDatabaseResolver && routed is null)
            {
                throw new NotSupportedException($"{PartName} carries the databases this host's tenants are routed to; a resolver that cannot name them ({resolver.GetType().Name}) is not supported.");
            }

            var options = provider.GetRequiredService<RavenFrameworkStoreOptions>();
            var framework = string.IsNullOrEmpty(options.FrameworkDatabase) ? store.Database : options.FrameworkDatabase;
            var master = string.IsNullOrEmpty(options.MasterDatabase) ? store.Database : options.MasterDatabase;
            var tenantTypes = TenantTables.Append(typeof(RavenIdentityUser)).Append(typeof(Stomme.Modules.HtmlText.Repository.HtmlTextAggregate)).ToList();
            var none = Array.Empty<int>();
            var layout = string.Equals(framework, master, StringComparison.OrdinalIgnoreCase)
                ? new List<(string, string, IReadOnlyList<Type>, IReadOnlyList<int>)> { ("raven-framework+master", framework, MasterTables.Where(type => type != typeof(SettingAggregate)).Concat(tenantTypes).ToList(), none) }
                : new List<(string, string, IReadOnlyList<Type>, IReadOnlyList<int>)> { ("raven-framework", framework, tenantTypes, none), ("raven-master", master, MasterTables, none) };
            foreach (var database in routed?.OwnDatabases ?? Array.Empty<string>())
            {
                layout.Add(("raven-tenant", database, tenantTypes, routed!.TenantsIn(database)));
            }

            return layout;
        }

        private async Task RavenExportAsync(IServiceProvider provider, IBackupPartWriter writer, CancellationToken cancellationToken)
        {
            var store = provider.GetRequiredService<IDocumentStore>();
            foreach (var (label, database, types, tenants) in RavenLayout(provider))
            {
                await Header(writer.Content, new RecordHeader { Database = label, Context = "raven", Tenants = tenants.ToList() }, cancellationToken);
                foreach (var type in types)
                {
                    await Header(writer.Content, new RecordHeader { Table = TableName(type) }, cancellationToken);
                    await (Task)RavenExportMethod.MakeGenericMethod(type).Invoke(null, new object[] { store, database, writer.Content, cancellationToken })!;
                }
            }
        }

        private static async Task RavenExportCollectionAsync<T>(IDocumentStore store, string database, Stream stream, CancellationToken cancellationToken) where T : class
        {
            using var session = store.OpenAsyncSession(new SessionOptions { Database = database, NoTracking = true });
            var query = session.Query<T>(collectionName: store.Conventions.FindCollectionName(typeof(T)));
            var rows = new List<(string Id, T Document)>();
            await using (var results = await session.Advanced.StreamAsync(query, cancellationToken))
            {
                while (await results.MoveNextAsync())
                {
                    rows.Add((results.Current.Id, results.Current.Document));
                }
            }

            // Ordered by id, so the same data is the same bytes.
            foreach (var (id, document) in rows.OrderBy(row => row.Id, StringComparer.Ordinal))
            {
                var envelope = new JObject { ["id"] = id, ["doc"] = JToken.Parse(FileStoreSerializer.Serialize(document)) };
                await BackupRecords.WriteAsync(stream, null, envelope.ToString(Newtonsoft.Json.Formatting.None), cancellationToken);
            }
        }

        private async Task<BackupPartValidation> RavenValidateAsync(IServiceProvider provider, IBackupPartReader reader, CancellationToken cancellationToken)
        {
            List<(string Label, string Database, IReadOnlyList<Type> Types, IReadOnlyList<int> Tenants)> layout;
            try
            {
                layout = RavenLayout(provider);
            }
            catch (NotSupportedException ex)
            {
                return BackupPartValidation.Invalid(ex.Message);
            }

            var expected = layout.Select(entry => LayoutKey(entry.Label, "raven", entry.Tenants)).ToList();
            var tablesOf = layout.ToDictionary(entry => LayoutKey(entry.Label, "raven", entry.Tenants), entry => entry.Types.ToDictionary(TableName, type => type, StringComparer.Ordinal));
            var found = new List<string>();
            var problems = new List<string>();
            Dictionary<string, Type>? tables = null;
            Type? table = null;
            var index = 0;
            await using var stream = reader.OpenRead();
            try
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    index++;
                    if (RecordHeader.TryParse(json) is { } header)
                    {
                        if (header.Context is not null)
                        {
                            var key = LayoutKey(header.Database ?? string.Empty, header.Context, header.Tenants ?? new List<int>());
                            found.Add(key);
                            tables = tablesOf.GetValueOrDefault(key);
                            table = null;
                        }
                        else if (header.Table is not null)
                        {
                            table = tables?.GetValueOrDefault(header.Table);
                            if (table is null)
                            {
                                problems.Add($"record {index} names a collection this database does not carry: {header.Table}");
                            }
                        }

                        continue;
                    }

                    if (table is null)
                    {
                        problems.Add($"record {index} is a document outside any known collection");
                    }
                    else if (!RavenEnvelope(json, out _, out var document) || !Deserializes(table, document))
                    {
                        problems.Add($"record {index} is not a {table.Name} document");
                    }

                    if (problems.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }

            if (!found.SequenceEqual(expected))
            {
                problems.Add($"the snapshot's databases ({string.Join(", ", found)}) are not this installation's ({string.Join(", ", expected)})");
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        private async Task RavenReplaceAsync(IServiceProvider provider, IBackupPartReader reader, CancellationToken cancellationToken)
        {
            var store = provider.GetRequiredService<IDocumentStore>();
            var layout = RavenLayout(provider).ToDictionary(entry => LayoutKey(entry.Label, "raven", entry.Tenants));

            // Group the part by database and collection first: each collection is cleared, then filled in one bulk insert.
            // Documents are held per collection while one is read - the framework's collections are small next to the
            // module data; a streamed variant is booked if a site ever proves otherwise.
            var byDatabase = new List<(string Database, IReadOnlyList<Type> Types, Dictionary<Type, List<(string Id, string Json)>> Rows)>();
            (string Database, IReadOnlyList<Type> Types, Dictionary<Type, List<(string Id, string Json)>> Rows)? current = null;
            Type? table = null;
            await using (var stream = reader.OpenRead())
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    if (RecordHeader.TryParse(json) is { } header)
                    {
                        if (header.Context is not null)
                        {
                            var key = LayoutKey(header.Database ?? string.Empty, header.Context, header.Tenants ?? new List<int>());
                            var target = layout.TryGetValue(key, out var found) ? found : throw new InvalidOperationException($"{PartName}: the snapshot names a database this installation does not have ({key}).");
                            current = (target.Database, target.Types, target.Types.ToDictionary(type => type, _ => new List<(string, string)>()));
                            byDatabase.Add(current.Value);
                            table = null;
                        }
                        else if (header.Table is not null)
                        {
                            table = current?.Types.FirstOrDefault(type => TableName(type) == header.Table)
                                ?? throw new InvalidDataException($"The snapshot carries a collection this database does not: {header.Table}.");
                        }

                        continue;
                    }

                    if (current is null || table is null || !RavenEnvelope(json, out var id, out var document))
                    {
                        throw new InvalidDataException("A document outside any collection, or not in the part's form.");
                    }

                    current.Value.Rows[table].Add((id, document));
                }
            }

            foreach (var (database, types, rows) in byDatabase)
            {
                var operations = store.Operations.ForDatabase(database);
                foreach (var type in SearchTables.Concat(types))
                {
                    var collection = store.Conventions.FindCollectionName(type);
                    var deletion = await operations.SendAsync(new DeleteByQueryOperation(new IndexQuery { Query = $"from '{collection}'" }), token: cancellationToken);
                    await deletion.WaitForCompletionAsync(TimeSpan.FromMinutes(10));
                }

                foreach (var type in types)
                {
                    await (Task)RavenInsertMethod.MakeGenericMethod(type).Invoke(null, new object[] { store, database, rows[type], cancellationToken })!;
                }

                var statistics = await store.Maintenance.ForDatabase(database).SendAsync(new GetCollectionStatisticsOperation(), cancellationToken);
                foreach (var type in types)
                {
                    var collection = store.Conventions.FindCollectionName(type);
                    var count = statistics.Collections.TryGetValue(collection, out var held) ? held : 0;
                    if (count != rows[type].Count)
                    {
                        throw new InvalidOperationException($"{PartName}: {collection} holds {count} documents after the replace, the snapshot {rows[type].Count}. The replace is not trusted.");
                    }
                }

                foreach (var type in types.Where(IsIntKeyed))
                {
                    var highest = rows[type].Select(row => int.TryParse(row.Id[(row.Id.LastIndexOf('/') + 1)..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
                    await operations.SendAsync(new PatchOperation($"Raven/Hilo/{type.Name}", null,
                        new PatchRequest { Script = "this.Max = Math.max(this.Max || 0, args.max);", Values = { ["max"] = highest } },
                        patchIfMissing: new PatchRequest { Script = "this.Max = args.max; this['@metadata'] = { '@collection': '@hilo' };", Values = { ["max"] = highest } }),
                        token: cancellationToken);
                }
            }

            (provider.GetService<IRavenIdGenerator>() as RavenHiLoIdGenerator)?.Forget();
        }

        private static async Task RavenInsertAsync<T>(IDocumentStore store, string database, List<(string Id, string Json)> rows, CancellationToken cancellationToken) where T : class
        {
            await using var bulk = store.BulkInsert(database);
            foreach (var (id, json) in rows)
            {
                await bulk.StoreAsync(FileStoreSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"A {typeof(T).Name} document did not deserialise."), id);
            }
        }

        /// <summary>An aggregate keyed by an int - its ids come from HiLo.</summary>
        private static bool IsIntKeyed(Type type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Stomme.SharedKernel.Domain.AggregateRoot<>))
                {
                    return current.GetGenericArguments()[0] == typeof(int);
                }
            }

            return false;
        }

        private static bool RavenEnvelope(string json, out string id, out string document)
        {
            id = string.Empty;
            document = string.Empty;
            try
            {
                var envelope = JObject.Parse(json);
                if (envelope["id"]?.Value<string>() is not { Length: > 0 } value || envelope["doc"] is not JObject body)
                {
                    return false;
                }

                id = value;
                document = body.ToString(Newtonsoft.Json.Formatting.None);
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }
    }
}
