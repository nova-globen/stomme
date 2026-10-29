#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Infrastructure.Backup;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Pipelines.Persistence.EfCore;
using Stomme.Modules.Pipelines.Persistence.Raven;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Persistence.File;
using Stomme.Persistence.RavenDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence
{
    /// <summary>
    /// The site's PIPELINES in a backup: the engine's definitions and their versions, runs, waits, triggers, queued
    /// resumes, step executions, execution logs and key-value store (folders, tags, archive marks, site variables, data
    /// tables, reviews). Registered with the engine's stores, so only where Pipelines is composed. Until it, no participant
    /// read them, and a restore kept whatever pipelines the live store had - or, onto a new host, none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>As stored, not as the engine reads it.</b> An engine entity keeps its polymorphic members as text the stores write
    /// through <see cref="EngineEntitySerializer"/> - in shadow columns on the relational stores, in document fields on
    /// RavenDB - so the part carries the ROWS and the DOCUMENTS exactly: a relational row is every property of the model,
    /// shadow and complex ones included, put back through the same model; a document is put back under its own id. Nothing
    /// is re-serialised through the engine, so nothing a serializer cannot round-trip is lost.
    /// </para>
    /// <para>
    /// <b>Scope.</b> An engine tenant is a site (<see cref="EngineTenantIds"/>: <c>{tenant}/{site}</c>), so a row belongs to
    /// the framework tenant its id names. A full restore replaces every row; a partial one the restored tenants'
    /// rows only - another tenant's, and the engine's default tenant's, stay as they are.
    /// </para>
    /// <para>
    /// <b>Replace.</b> Relational: one transaction - the rows in scope deleted, the part's inserted in batches, each table's
    /// count checked. RavenDB: per database (BL-176 gives a tenant its own), delete by query, then the documents with their
    /// ids, each collection's count checked - RavenDB has no transaction across that, as for every RavenDB part.
    /// </para>
    /// </remarks>
    public sealed class EngineBackupParticipant(IServiceScopeFactory scopes) : IBackupParticipant
    {
        public const string PartName = "pipelines:engine";

        private const int BatchSize = 500;

        /// <summary>The engine's relational tables, as <see cref="EngineDbContext"/> maps them.</summary>
        public static readonly Type[] Tables =
        {
            typeof(PipelineDefinition), typeof(PipelineInstance), typeof(StoredBookmark), typeof(StoredTrigger), typeof(BookmarkQueueItem),
            typeof(StepExecutionRecord), typeof(PipelineExecutionLogRecord), typeof(SerializedKeyValuePair),
        };

        /// <summary>The engine's RavenDB documents, one collection each.</summary>
        internal static readonly Type[] Documents =
        {
            typeof(PipelineDefinitionDocument), typeof(PipelineInstanceDocument), typeof(BookmarkDocument), typeof(TriggerDocument), typeof(BookmarkQueueDocument),
            typeof(StepExecutionDocument), typeof(ExecutionLogDocument), typeof(KeyValueDocument),
        };

        private static readonly MethodInfo EfExport = Method(nameof(EfExportAsync));
        private static readonly MethodInfo EfTenants = Method(nameof(EfTenantsAsync));
        private static readonly MethodInfo EfDelete = Method(nameof(EfDeleteAsync));
        private static readonly MethodInfo EfInsert = Method(nameof(EfInsertAsync));
        private static readonly MethodInfo EfCount = Method(nameof(EfCountAsync));
        private static readonly MethodInfo RavenExport = Method(nameof(RavenExportAsync));
        private static readonly MethodInfo RavenTenants = Method(nameof(RavenTenantsAsync));
        private static readonly MethodInfo RavenCount = Method(nameof(RavenCountAsync));
        private static readonly MethodInfo Deserialize = typeof(FileStoreSerializer).GetMethod(nameof(FileStoreSerializer.Deserialize), BindingFlags.Public | BindingFlags.Static)!;

        public string Name => PartName;

        /// <summary>The engine's tables and documents as the serializer reads their members, and the stores' text columns.</summary>
        public string SchemaVersion => Shape.Value;

        private static readonly Lazy<string> Shape = new(() => FileStoreSerializer.Shape(Tables.Concat(Documents), new[]
        {
            EngineDbContext.DataColumn, EngineDbContext.MetadataColumn, EngineDbContext.StepStateColumn, EngineDbContext.PayloadColumn,
            EngineDbContext.OutputsColumn, EngineDbContext.PropertiesColumn, EngineDbContext.ExceptionColumn,
        }));

        /// <summary>After the module aggregates: pipelines refer to sites, connections and tables, never the other way round.</summary>
        public int RestoreOrder => 600;

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            if (di.ServiceProvider.GetService<IDbContextFactory<EngineDbContext>>() is { } contexts)
            {
                await Header(writer.Content, new JObject { ["$store"] = "relational" }, cancellationToken);
                await using var db = await contexts.CreateDbContextAsync(cancellationToken);
                foreach (var table in Tables)
                {
                    await Header(writer.Content, new JObject { ["$table"] = table.Name }, cancellationToken);
                    await (Task)EfExport.MakeGenericMethod(table).Invoke(null, new object[] { db, writer.Content, cancellationToken })!;
                }

                return;
            }

            var store = di.ServiceProvider.GetRequiredService<global::Raven.Client.Documents.IDocumentStore>();
            await Header(writer.Content, new JObject { ["$store"] = "ravendb" }, cancellationToken);
            foreach (var (database, tenants) in Databases(di.ServiceProvider, scope.TenantIds))
            {
                await Header(writer.Content, new JObject { ["$database"] = new JArray(tenants) }, cancellationToken);
                using var session = store.OpenAsyncSession(new SessionOptions { Database = database ?? store.Database });
                foreach (var document in Documents)
                {
                    await Header(writer.Content, new JObject { ["$table"] = document.Name }, cancellationToken);
                    await (Task)RavenExport.MakeGenericMethod(document).Invoke(null, new object[] { session, writer.Content, cancellationToken })!;
                }
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            var relational = di.ServiceProvider.GetService<IDbContextFactory<EngineDbContext>>() is not null;
            var known = (relational ? Tables : Documents).ToDictionary(type => type.Name, type => type, StringComparer.Ordinal);
            var problems = new List<string>();
            string? storeKind = null;
            Type? table = null;
            var sections = new List<string>();
            await using var stream = reader.OpenRead();
            try
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    var record = Parse(json);
                    if (record["$store"] is { } kind)
                    {
                        storeKind = kind.Value<string>();
                    }
                    else if (record["$database"] is JArray tenants)
                    {
                        sections.Add(string.Join(",", tenants.Select(tenant => tenant.Value<int>())));
                        table = null;
                    }
                    else if (record["$table"] is { } name)
                    {
                        table = known.GetValueOrDefault(name.Value<string>() ?? string.Empty);
                        if (table is null)
                        {
                            problems.Add($"the part names a table this install does not keep: {name}");
                        }
                    }
                    else if (table is null)
                    {
                        problems.Add("a row comes before its table");
                    }
                    else if (relational ? record["Id"]?.Type != JTokenType.String : record["id"]?.Type != JTokenType.String || record["doc"] is not JObject)
                    {
                        problems.Add($"a {table.Name} row has no id");
                    }

                    if (problems.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                problems.Add("the part is malformed: " + ex.GetType().Name);
            }

            if (storeKind != (relational ? "relational" : "ravendb"))
            {
                problems.Add($"the part was taken from {(storeKind ?? "no")} engine store; this install keeps its pipelines on {(relational ? "a relational database" : "RavenDB")}");
            }
            else if (!relational)
            {
                var layout = Databases(di.ServiceProvider, scope.SnapshotTenantIds).Select(database => string.Join(",", database.Tenants)).ToList();
                if (!sections.SequenceEqual(layout))
                {
                    problems.Add($"the snapshot's engine databases ({string.Join("; ", sections)}) are not this installation's ({string.Join("; ", layout)})");
                }
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            // Read once: per database (one, relational), per table, the rows in this restore's scope.
            var parts = new List<(List<int> Tenants, Dictionary<string, List<JObject>> Tables)>();
            Dictionary<string, List<JObject>>? current = null;
            List<JObject>? rows = null;
            var relational = false;
            await using (var stream = reader.OpenRead())
            {
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    var record = Parse(json);
                    if (record["$store"] is { } kind)
                    {
                        relational = kind.Value<string>() == "relational";
                        if (relational)
                        {
                            parts.Add((new List<int>(), current = new Dictionary<string, List<JObject>>(StringComparer.Ordinal)));
                        }
                    }
                    else if (record["$database"] is JArray tenants)
                    {
                        parts.Add((tenants.Select(tenant => tenant.Value<int>()).ToList(), current = new Dictionary<string, List<JObject>>(StringComparer.Ordinal)));
                    }
                    else if (record["$table"] is { } name)
                    {
                        (current ?? throw new InvalidDataException("A table before its store."))[name.Value<string>()!] = rows = new List<JObject>();
                    }
                    else
                    {
                        var tenant = relational ? record["TenantId"]?.Value<string>() : record["doc"]?["TenantId"]?.Value<string>();
                        if (InScope(scope, tenant))
                        {
                            (rows ?? throw new InvalidDataException("A row before its table.")).Add(record);
                        }
                    }
                }
            }

            using var di = scopes.CreateScope();
            if (di.ServiceProvider.GetService<IDbContextFactory<EngineDbContext>>() is { } contexts)
            {
                var tables = parts.SingleOrDefault().Tables ?? throw new InvalidOperationException($"{PartName}: the snapshot holds no relational engine store.");
                await using var db = await contexts.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                foreach (var table in Tables)
                {
                    var tenants = scope.IsPartial
                        ? (await (Task<List<string>>)EfTenants.MakeGenericMethod(table).Invoke(null, new object[] { db, cancellationToken })!).Where(tenant => InScope(scope, tenant)).ToList()
                        : null;
                    await (Task<int>)EfDelete.MakeGenericMethod(table).Invoke(null, new object?[] { db, tenants, cancellationToken })!;
                    var restored = tables.GetValueOrDefault(table.Name) ?? new List<JObject>();
                    await (Task)EfInsert.MakeGenericMethod(table).Invoke(null, new object[] { db, restored, cancellationToken })!;
                    var inScope = scope.IsPartial ? tenants!.Concat(restored.Select(row => row["TenantId"]?.Value<string>() ?? "")).Distinct(StringComparer.Ordinal).ToList() : null;
                    var count = await (Task<int>)EfCount.MakeGenericMethod(table).Invoke(null, new object?[] { db, inScope, cancellationToken })!;
                    if (count != restored.Count)
                    {
                        throw new InvalidOperationException($"{PartName}: {table.Name} holds {count} rows after the replace, the snapshot {restored.Count}. The replace is not trusted.");
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var store = di.ServiceProvider.GetRequiredService<global::Raven.Client.Documents.IDocumentStore>();
            var databases = Databases(di.ServiceProvider, scope.SnapshotTenantIds);
            foreach (var (database, tenants) in databases)
            {
                if (scope.IsPartial && !tenants.Any(scope.TenantIds.Contains))
                {
                    continue; // nothing of this restore lives here
                }

                var section = parts.FirstOrDefault(part => part.Tenants.SequenceEqual(tenants)).Tables
                    ?? throw new InvalidOperationException($"{PartName}: the snapshot has no section for the engine database of tenants {string.Join(", ", tenants)}.");
                await ReplaceRavenAsync(store, database ?? store.Database, section, scope, cancellationToken);
            }
        }

        private static async Task ReplaceRavenAsync(global::Raven.Client.Documents.IDocumentStore store, string database, Dictionary<string, List<JObject>> section, BackupScope scope, CancellationToken cancellationToken)
        {
            // The engine tenants this restore owns in this database: every one, or the restored tenants' sites.
            List<string>? tenants = null;
            if (scope.IsPartial)
            {
                using var reading = store.OpenAsyncSession(new SessionOptions { Database = database });
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var document in Documents)
                {
                    present.UnionWith(await (Task<List<string>>)RavenTenants.MakeGenericMethod(document).Invoke(null, new object[] { reading, cancellationToken })!);
                }

                tenants = present.Concat(section.Values.SelectMany(rows => rows).Select(row => row["doc"]?["TenantId"]?.Value<string>() ?? ""))
                    .Where(tenant => InScope(scope, tenant)).Distinct(StringComparer.Ordinal).ToList();
            }

            foreach (var document in Documents)
            {
                var collection = store.Conventions.FindCollectionName(document);
                var deletion = await store.Operations.ForDatabase(database).SendAsync(new DeleteByQueryOperation(tenants is null
                    ? new IndexQuery { Query = $"from '{collection}'" }
                    : new IndexQuery { Query = $"from '{collection}' where TenantId in ($tenants)", QueryParameters = new global::Raven.Client.Parameters { ["tenants"] = tenants } }), token: cancellationToken);
                await deletion.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
            }

            foreach (var document in Documents)
            {
                var restored = section.GetValueOrDefault(document.Name) ?? new List<JObject>();
                foreach (var batch in restored.Chunk(BatchSize))
                {
                    using var writer = store.OpenAsyncSession(new SessionOptions { Database = database });
                    foreach (var row in batch)
                    {
                        var value = Deserialize.MakeGenericMethod(document).Invoke(null, new object[] { row["doc"]!.ToString(Formatting.None) })!;
                        await writer.StoreAsync(value, row["id"]!.Value<string>(), cancellationToken);
                    }

                    await writer.SaveChangesAsync(cancellationToken);
                }

                using var counting = store.OpenAsyncSession(new SessionOptions { Database = database });
                var count = await (Task<long>)RavenCount.MakeGenericMethod(document).Invoke(null, new object?[] { counting, tenants, cancellationToken })!;
                if (count != restored.Count)
                {
                    throw new InvalidOperationException($"{PartName}: {document.Name} holds {count} documents after the replace, the snapshot {restored.Count}. The replace is not trusted.");
                }
            }
        }

        // An engine row belongs to the framework tenant its id names; the default tenant's ("") and an unreadable one belong to
        // the installation, so only a full restore touches them.
        private static bool InScope(BackupScope scope, string? engineTenant) =>
            !scope.IsPartial || (EngineTenantIds.TryParse(engineTenant, out var tenantId, out _) && scope.TenantIds.Contains(tenantId));

        /// <summary>The RavenDB databases the engine's documents live in, with the framework tenants each serves, in a stable order.</summary>
        private static List<(string? Database, List<int> Tenants)> Databases(IServiceProvider provider, IReadOnlyList<int> tenantIds)
        {
            var resolver = provider.GetService<IRavenTenantDatabaseResolver>();
            return tenantIds.OrderBy(id => id)
                .Select(id => (Id: id, Database: resolver?.Resolve(FrameworkTenantId.ToTenantIdOrNull(id))))
                .GroupBy(entry => entry.Database ?? string.Empty, StringComparer.Ordinal)
                .Select(group => (Database: group.First().Database, Tenants: group.Select(entry => entry.Id).ToList()))
                .ToList();
        }

        private static async Task EfExportAsync<T>(EngineDbContext db, Stream stream, CancellationToken cancellationToken) where T : class
        {
            // Tracked, a page at a time: a shadow column's value lives in the change tracker, not on the entity.
            for (var skip = 0; ; skip += BatchSize)
            {
                var page = await db.Set<T>().IgnoreQueryFilters().AsTracking()
                    .OrderBy(entity => EF.Property<string>(entity, "Id")).Skip(skip).Take(BatchSize).ToListAsync(cancellationToken);
                foreach (var entity in page)
                {
                    await BackupRecords.WriteAsync(stream, null, RowOf(db.Entry(entity)).ToString(Formatting.None), cancellationToken);
                }

                db.ChangeTracker.Clear();
                if (page.Count < BatchSize)
                {
                    return;
                }
            }
        }

        private static Task<List<string>> EfTenantsAsync<T>(EngineDbContext db, CancellationToken cancellationToken) where T : class =>
            db.Set<T>().IgnoreQueryFilters().Select(entity => EF.Property<string>(entity, "TenantId") ?? "").Distinct().ToListAsync(cancellationToken);

        private static Task<int> EfDeleteAsync<T>(EngineDbContext db, List<string>? tenants, CancellationToken cancellationToken) where T : class =>
            (tenants is null ? db.Set<T>().IgnoreQueryFilters() : db.Set<T>().IgnoreQueryFilters().Where(entity => tenants.Contains(EF.Property<string>(entity, "TenantId"))))
                .ExecuteDeleteAsync(cancellationToken);

        private static Task<int> EfCountAsync<T>(EngineDbContext db, List<string>? tenants, CancellationToken cancellationToken) where T : class =>
            (tenants is null ? db.Set<T>().IgnoreQueryFilters() : db.Set<T>().IgnoreQueryFilters().Where(entity => tenants.Contains(EF.Property<string>(entity, "TenantId"))))
                .CountAsync(cancellationToken);

        private static async Task EfInsertAsync<T>(EngineDbContext db, List<JObject> rows, CancellationToken cancellationToken) where T : class
        {
            var entityType = db.Model.FindEntityType(typeof(T))!;
            foreach (var batch in rows.Chunk(BatchSize))
            {
                foreach (var row in batch)
                {
                    var entity = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                    foreach (var property in entityType.GetProperties().Where(property => !property.IsShadowProperty()))
                    {
                        if (row.TryGetValue(property.Name, out var token))
                        {
                            Set(property, entity, ValueOf(token, property.ClrType));
                        }
                    }

                    foreach (var complex in entityType.GetComplexProperties())
                    {
                        var value = Activator.CreateInstance(complex.ClrType, nonPublic: true)!;
                        if (row["$" + complex.Name] is JObject fields)
                        {
                            foreach (var property in complex.ComplexType.GetProperties())
                            {
                                if (fields.TryGetValue(property.Name, out var token))
                                {
                                    Set(property, value, ValueOf(token, property.ClrType));
                                }
                            }
                        }

                        Set(complex, entity, value);
                    }

                    var entry = db.Add(entity);
                    foreach (var property in entityType.GetProperties().Where(property => property.IsShadowProperty()))
                    {
                        if (row.TryGetValue(property.Name, out var token))
                        {
                            entry.Property(property.Name).CurrentValue = ValueOf(token, property.ClrType);
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }

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
                await BackupRecords.WriteAsync(stream, null, envelope.ToString(Formatting.None), cancellationToken);
            }
        }

        private static async Task<List<string>> RavenTenantsAsync<T>(IAsyncDocumentSession session, CancellationToken cancellationToken) where T : EngineDocument
        {
            var tenants = await session.Advanced.AsyncDocumentQuery<T>(collectionName: session.Advanced.DocumentStore.Conventions.FindCollectionName(typeof(T)))
                .WaitForNonStaleResults().SelectFields<TenantOnly>(nameof(EngineDocument.TenantId)).Distinct().ToListAsync(cancellationToken);
            return tenants.Select(tenant => tenant.TenantId ?? "").ToList();
        }

        private static Task<long> RavenCountAsync<T>(IAsyncDocumentSession session, List<string>? tenants, CancellationToken cancellationToken) where T : EngineDocument
        {
            var query = session.Advanced.AsyncDocumentQuery<T>(collectionName: session.Advanced.DocumentStore.Conventions.FindCollectionName(typeof(T))).WaitForNonStaleResults();
            if (tenants is not null)
            {
                query = query.WhereIn(nameof(EngineDocument.TenantId), tenants.Cast<object>().ToList(), exact: true);
            }

            return query.LongCountAsync(cancellationToken);
        }

        private sealed class TenantOnly
        {
            public string? TenantId { get; set; }
        }

        /// <summary>Every property the model maps, shadow ones included, and each complex property's under <c>$name</c>.</summary>
        private static JObject RowOf(EntityEntry entry)
        {
            var row = new JObject();
            foreach (var property in entry.Properties)
            {
                row[property.Metadata.Name] = TokenOf(property.CurrentValue);
            }

            foreach (var complex in entry.ComplexProperties)
            {
                var fields = new JObject();
                foreach (var property in complex.Properties)
                {
                    fields[property.Metadata.Name] = TokenOf(property.CurrentValue);
                }

                row["$" + complex.Metadata.Name] = fields;
            }

            return row;
        }

        // The model's CLR values as JSON, each type the engine's entities use written so it reads back exactly.
        private static JToken TokenOf(object? value) => value switch
        {
            null => JValue.CreateNull(),
            Type type => new JValue(type.AssemblyQualifiedName),
            Version version => new JValue(version.ToString()),
            DateTimeOffset instant => new JValue(instant.ToString("O", CultureInfo.InvariantCulture)),
            DateTime instant => new JValue(instant.ToString("O", CultureInfo.InvariantCulture)),
            Enum member => new JValue(Convert.ToInt64(member, CultureInfo.InvariantCulture)),
            _ => JToken.FromObject(value),
        };

        private static object? ValueOf(JToken token, Type type)
        {
            if (token.Type == JTokenType.Null)
            {
                return null;
            }

            var target = Nullable.GetUnderlyingType(type) ?? type;
            if (target == typeof(Type))
            {
                return Type.GetType(token.Value<string>()!, throwOnError: true);
            }

            if (target == typeof(Version))
            {
                return Version.Parse(token.Value<string>()!);
            }

            if (target == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(token.Value<string>()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (target == typeof(DateTime))
            {
                return DateTime.Parse(token.Value<string>()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            return target.IsEnum ? Enum.ToObject(target, token.Value<long>()) : token.ToObject(target);
        }

        private static void Set(IPropertyBase property, object target, object? value)
        {
            if (property.PropertyInfo is { CanWrite: true } writable)
            {
                writable.SetValue(target, value);
            }
            else
            {
                (property.FieldInfo ?? throw new InvalidOperationException($"{property.DeclaringType.DisplayName()}.{property.Name} cannot be set."))
                    .SetValue(target, value);
            }
        }

        // DateParseHandling.None: an instant stays the text it was written as, so its offset survives the read.
        private static JObject Parse(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static Task Header(Stream stream, JObject header, CancellationToken cancellationToken) =>
            BackupRecords.WriteAsync(stream, null, header.ToString(Formatting.None), cancellationToken);

        private static MethodInfo Method(string name) => typeof(EngineBackupParticipant).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
