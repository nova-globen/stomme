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
using Stomme.Databases.Interfaces;
using Stomme.Models;
using Stomme.Persistence.File;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The framework's own data on a RELATIONAL install: the master database's tables and every tenant
    /// database's - the framework store's aggregates and the ASP.NET Identity tables - with their ORIGINAL keys. One part for
    /// all of them, because the tables carry foreign keys (some NO ACTION: PageModule -&gt; Module, Permission -&gt; User and
    /// Role, UserRole -&gt; Role), so a replace has to delete child-first and insert parent-first across tables - an order no
    /// single-store participant could keep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Physical databases, not tenants.</b> Several tenants may share one database, and the master usually shares the
    /// first tenant's (the stock install) - then the <c>Setting</c> table is ONE table serving both, carried once, through the
    /// tenant context. Databases are keyed by provider and resolved connection string; the connection string itself never
    /// enters the archive, only the layout (which tenants, whether the master lives there), and a restore refuses a layout
    /// that differs from this installation's.
    /// </para>
    /// <para>
    /// <b>Replace.</b> Per context, in ONE transaction: the search index is cleared (it is rebuilt), every
    /// carried table deleted child-first, the rows inserted parent-first with their keys, each table's count checked, and the
    /// identity moved past the highest key (<see cref="IRelationalDatabase.ResetIdentityAsync"/>). Audit stamping is off
    /// while it writes (<see cref="AuditStampSuppression"/>) - a restored row keeps its history. SQL Server takes the original
    /// keys with IDENTITY_INSERT, one table and one batch at a time; a provider without a logical restore is refused
    /// (<see cref="IRelationalDatabase.SupportsLogicalRestore"/>). <c>MigrationHistory</c> is not carried: a restore needs
    /// this part's shape, migrations included, to be the host's own, so the host's history already describes it.
    /// </para>
    /// </remarks>
    public sealed partial class FrameworkDatabasesBackupParticipant(IServiceScopeFactory scopes) : IBackupParticipant
    {
        public const string PartName = "framework:databases";

        private const int BatchSize = 500;

        /// <summary>The master tables, parents first. <c>Setting</c> only when no tenant shares the database.</summary>
        internal static readonly Type[] MasterTables =
        {
            typeof(TenantAggregate), typeof(AliasAggregate), typeof(JobAggregate), typeof(JobLogAggregate),
            typeof(ThemeAggregate), typeof(ModuleDefinitionAggregate), typeof(SettingAggregate),
        };

        /// <summary>A tenant database's framework tables, parents first. The Identity tables follow, read from the model.</summary>
        internal static readonly Type[] TenantTables =
        {
            typeof(SiteAggregate), typeof(RoleAggregate), typeof(UserAggregate), typeof(PageAggregate), typeof(ModuleAggregate),
            typeof(FolderAggregate), typeof(FileAggregate), typeof(LanguageAggregate), typeof(LogAggregate),
            typeof(NotificationAggregate), typeof(PermissionAggregate), typeof(ProfileAggregate), typeof(SiteTaskAggregate),
            typeof(SiteGroupAggregate), typeof(SiteGroupMemberAggregate), typeof(UrlMappingAggregate), typeof(VisitorAggregate),
            typeof(UserRoleAggregate), typeof(PageModuleAggregate), typeof(SettingAggregate),
        };

        /// <summary>The search index - cleared on replace, never carried: it is rebuilt from the restored content.</summary>
        internal static readonly Type[] SearchTables =
        {
            typeof(SearchContentWordAggregate), typeof(SearchContentPropertyAggregate), typeof(SearchWordAggregate), typeof(SearchContentAggregate),
        };

        private static readonly MethodInfo ExportTableMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(ExportTableAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo InsertMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(InsertAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo DeleteMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(DeleteAllAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CountMethod = typeof(FrameworkDatabasesBackupParticipant).GetMethod(nameof(CountAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo DeserializeMethod = typeof(FileStoreSerializer).GetMethod(nameof(FileStoreSerializer.Deserialize), BindingFlags.Public | BindingFlags.Static)!;

        public string Name => PartName;

        /// <summary>
        /// What the part's rows are: the carried aggregates as the serializer writes them, and every master and tenant
        /// migration this host has - a column added between two versions comes with a migration, so the part from before it
        /// is refused, while a version that changed neither restores an older archive. It was a hand-kept "1" nothing bumped.
        /// </summary>
        public string SchemaVersion => Shape.Value;

        private static readonly Lazy<string> Shape = new(() => FileStoreSerializer.Shape(MasterTables.Concat(TenantTables), FrameworkMigrations()));

        /// <summary>The master and tenant migrations compiled into this host, as context and id.</summary>
        internal static IEnumerable<string> FrameworkMigrations() =>
            typeof(TenantDBContext).Assembly.GetTypes()
                .Select(type => (Context: type.GetCustomAttribute<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute>()?.ContextType,
                    Id: type.GetCustomAttribute<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>()?.Id))
                .Where(migration => migration.Id is not null && (migration.Context == typeof(TenantDBContext) || migration.Context == typeof(MasterDBContext)))
                .Select(migration => migration.Context!.Name + ":" + migration.Id);

        /// <summary>First: everything else refers to sites, users and modules.</summary>
        public int RestoreOrder => 100;

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            if (IsRavenInstall(di.ServiceProvider))
            {
                await RavenExportAsync(di.ServiceProvider, writer, cancellationToken);
                return;
            }

            foreach (var database in await LayoutAsync(di.ServiceProvider, scope, cancellationToken))
            {
                foreach (var target in database.Contexts)
                {
                    await Header(writer.Content, new RecordHeader { Database = database.Label, Context = target.Kind, Tenants = database.TenantIds }, cancellationToken);
                    using var contextScope = scopes.CreateScope();
                    await using var context = target.Create(contextScope.ServiceProvider);
                    foreach (var table in target.Tables(context))
                    {
                        await Header(writer.Content, new RecordHeader { Table = TableName(table) }, cancellationToken);
                        await (Task)ExportTableMethod.MakeGenericMethod(table).Invoke(null, new object[] { context, writer.Content, cancellationToken })!;
                    }
                }
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            if (IsRavenInstall(di.ServiceProvider))
            {
                return await RavenValidateAsync(di.ServiceProvider, reader, cancellationToken);
            }

            var problems = new List<string>();
            List<DatabaseTarget> layout;
            try
            {
                layout = await LayoutAsync(di.ServiceProvider, scope, cancellationToken);
            }
            catch (NotSupportedException ex)
            {
                return BackupPartValidation.Invalid(ex.Message);
            }

            var expected = new List<string>();
            var tablesOf = new Dictionary<string, Dictionary<string, Type>>();
            foreach (var database in layout)
            {
                foreach (var target in database.Contexts)
                {
                    var key = LayoutKey(database.Label, target.Kind, database.TenantIds);
                    expected.Add(key);
                    using var contextScope = scopes.CreateScope();
                    await using var context = target.Create(contextScope.ServiceProvider);
                    tablesOf[key] = target.Tables(context).ToDictionary(TableName, type => type, StringComparer.Ordinal);
                }
            }

            var found = new List<string>();
            Dictionary<string, Type>? tables = null;
            Type? table = null;
            var skipping = false; // A context a partial restore leaves alone is read past, not judged
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
                            skipping = scope.IsPartial && tables is null;
                        }
                        else if (header.Table is not null && !skipping)
                        {
                            table = tables?.GetValueOrDefault(header.Table);
                            if (table is null)
                            {
                                problems.Add($"record {index} names a table this database does not carry: {header.Table}");
                            }
                        }

                        continue;
                    }

                    if (skipping)
                    {
                        continue;
                    }

                    if (table is null)
                    {
                        problems.Add($"record {index} is a row outside any known table");
                    }
                    else if (!Deserializes(table, json))
                    {
                        problems.Add($"record {index} is not a {table.Name}");
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

            // A whole restore: the snapshot's databases are exactly this installation's. A partial one: every
            // selected tenant's database is in the snapshot; the rest of the snapshot is read past.
            if (scope.IsPartial ? expected.Any(key => !found.Contains(key)) : !found.SequenceEqual(expected))
            {
                problems.Add($"the snapshot's databases ({string.Join(", ", found)}) are not this installation's ({string.Join(", ", expected)})");
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            using var di = scopes.CreateScope();
            if (IsRavenInstall(di.ServiceProvider))
            {
                if (scope.IsPartial)
                {
                    throw new NotSupportedException($"{Name}: a tenant-scoped restore is not supported on a RavenDB installation.");
                }

                await RavenReplaceAsync(di.ServiceProvider, reader, cancellationToken);
                return;
            }

            var layout = await LayoutAsync(di.ServiceProvider, scope, cancellationToken);
            var targets = layout.SelectMany(database => database.Contexts.Select(context => (Key: LayoutKey(database.Label, context.Kind, database.TenantIds), Context: context))).ToDictionary(pair => pair.Key, pair => pair.Context);

            using var suppressed = AuditStampSuppression.Begin();
            ContextReplace? current = null;
            var skipping = false; // A context the partial restore leaves alone - its tables and rows are read past
            try
            {
                await using var stream = reader.OpenRead();
                await foreach (var (_, json) in BackupRecords.ReadAsync(stream, cancellationToken))
                {
                    if (RecordHeader.TryParse(json) is { } header)
                    {
                        if (header.Context is not null)
                        {
                            if (current is not null)
                            {
                                await current.CompleteAsync(cancellationToken);
                                await current.DisposeAsync();
                                current = null;
                            }

                            var key = LayoutKey(header.Database ?? string.Empty, header.Context, header.Tenants ?? new List<int>());
                            if (targets.TryGetValue(key, out var target))
                            {
                                skipping = false;
                                current = await ContextReplace.BeginAsync(scopes, target, cancellationToken);
                            }
                            else if (scope.IsPartial)
                            {
                                skipping = true;
                            }
                            else
                            {
                                throw new InvalidOperationException($"{Name}: the snapshot names a database this installation does not have ({key}).");
                            }
                        }
                        else if (header.Table is not null && !skipping)
                        {
                            await (current ?? throw new InvalidDataException("A table before any database.")).StartTableAsync(header.Table, cancellationToken);
                        }

                        continue;
                    }

                    if (skipping)
                    {
                        continue;
                    }

                    await (current ?? throw new InvalidDataException("A row before any database.")).AddRowAsync(json, cancellationToken);
                }

                if (current is not null)
                {
                    await current.CompleteAsync(cancellationToken);
                }
            }
            finally
            {
                if (current is not null)
                {
                    await current.DisposeAsync();
                }
            }
        }

        /// <summary>A table's name in the part: namespace, name and type arguments - never an assembly version.</summary>
        internal static string TableName(Type type) =>
            type.IsGenericType
                ? $"{type.Namespace}.{type.Name[..type.Name.IndexOf('`')]}<{string.Join(",", type.GetGenericArguments().Select(TableName))}>"
                : $"{type.Namespace}.{type.Name}";

        private static string LayoutKey(string database, string context, IEnumerable<int> tenants) =>
            $"{database}/{context}[{string.Join(",", tenants)}]";

        private static bool Deserializes(Type table, string json)
        {
            try
            {
                return DeserializeMethod.MakeGenericMethod(table).Invoke(null, new object[] { json }) is not null;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        private static Task Header(Stream stream, RecordHeader header, CancellationToken cancellationToken) =>
            BackupRecords.WriteAsync(stream, null, JsonSerializer.Serialize(header, RecordHeader.Json), cancellationToken);

        private static async Task ExportTableAsync<T>(DbContext context, Stream stream, CancellationToken cancellationToken) where T : class
        {
            var entity = context.Model.FindEntityType(typeof(T))!;
            IQueryable<T> rows = context.Set<T>().AsNoTracking();
            IOrderedQueryable<T>? ordered = null;
            foreach (var key in entity.FindPrimaryKey()!.Properties)
            {
                ordered = ordered is null ? rows.OrderBy(row => EF.Property<object>(row, key.Name)) : ordered.ThenBy(row => EF.Property<object>(row, key.Name));
            }

            await foreach (var row in (ordered ?? rows).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                await BackupRecords.WriteAsync(stream, null, FileStoreSerializer.Serialize(row), cancellationToken);
            }
        }

        private static async Task InsertAsync<T>(DbContext context, List<string> rows, CancellationToken cancellationToken) where T : class
        {
            foreach (var json in rows)
            {
                context.Set<T>().Add(FileStoreSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"A {typeof(T).Name} row did not deserialise."));
            }

            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }

        private static Task<int> DeleteAllAsync<T>(DbContext context, CancellationToken cancellationToken) where T : class =>
            context.Set<T>().IgnoreQueryFilters().ExecuteDeleteAsync(cancellationToken);

        private static Task<int> CountAsync<T>(DbContext context, CancellationToken cancellationToken) where T : class =>
            context.Set<T>().IgnoreQueryFilters().CountAsync(cancellationToken);

        /// <summary>The physical databases of this installation and which context carries what in each.</summary>
        private static async Task<List<DatabaseTarget>> LayoutAsync(IServiceProvider provider, BackupScope scope, CancellationToken cancellationToken)
        {
            var config = provider.GetRequiredService<IConfigManager>();
            var dataDirectory = config.GetDataDirectory();
            string Resolve(string? connectionString) => (connectionString ?? string.Empty).Replace($"|{Constants.DataDirectory}|", dataDirectory);

            var masterType = config.GetDatabaseType();
            RequireLogicalRestore(masterType);
            var masterKey = masterType + "|" + Resolve(config.GetConnectionString(SettingKeys.ConnectionStringKey));

            var tenants = (await provider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
                .Where(tenant => scope.TenantIds.Contains(tenant.TenantId))
                .OrderBy(tenant => tenant.TenantId)
                .ToList();
            foreach (var tenant in tenants)
            {
                RequireLogicalRestore(tenant.DBType);
            }

            var byKey = tenants.GroupBy(tenant => tenant.DBType + "|" + Resolve(config.GetConnectionString(tenant.DBConnectionString))).ToList();
            var databases = new List<DatabaseTarget>();
            var masterShared = byKey.FirstOrDefault(group => group.Key == masterKey);
            // A partial restore leaves the master context (tenants, aliases, jobs, the installation's settings)
            // alone - only the selected tenants' own contexts are targets.
            databases.Add(new DatabaseTarget("master", masterShared?.ToList() ?? new List<Tenant>(), hasMaster: !scope.IsPartial));
            foreach (var group in byKey.Where(group => group.Key != masterKey).OrderBy(group => group.Min(tenant => tenant.TenantId)))
            {
                databases.Add(new DatabaseTarget("tenants-" + string.Join("-", group.Select(tenant => tenant.TenantId)), group.ToList(), hasMaster: false));
            }

            return databases;
        }

        private static void RequireLogicalRestore(string databaseType)
        {
            var type = Type.GetType(databaseType) ?? throw new NotSupportedException($"The database type '{databaseType}' is not loadable.");
            if (Activator.CreateInstance(type) is not IRelationalDatabase relational)
            {
                throw new NotSupportedException($"{PartName} carries relational databases; '{type.Name}' is not one.");
            }

            if (!relational.SupportsLogicalRestore)
            {
                throw new NotSupportedException($"A logical restore is not supported on {type.Name} (D106).");
            }
        }

        /// <summary>A physical database: a label, the tenants in it, and the contexts that carry its tables.</summary>
        private sealed class DatabaseTarget
        {
            public DatabaseTarget(string label, List<Tenant> tenants, bool hasMaster)
            {
                Label = label;
                TenantIds = tenants.Select(tenant => tenant.TenantId).ToList();
                var contexts = new List<ContextTarget>();
                if (hasMaster)
                {
                    contexts.Add(ContextTarget.Master(sharesSettings: tenants.Count > 0));
                }

                if (tenants.Count > 0)
                {
                    contexts.Add(ContextTarget.Tenant(tenants[0]));
                    contexts.Add(ContextTarget.HtmlText(tenants[0]));
                }

                Contexts = contexts;
            }

            public string Label { get; }

            public List<int> TenantIds { get; }

            public IReadOnlyList<ContextTarget> Contexts { get; }
        }

        /// <summary>One context on a physical database and the tables it carries there.</summary>
        private sealed class ContextTarget
        {
            private ContextTarget(string kind, Func<IServiceProvider, DbContext> create, Func<DbContext, IReadOnlyList<Type>> tables, IReadOnlyList<Type> cleared)
            {
                Kind = kind;
                Create = create;
                Tables = tables;
                Cleared = cleared;
            }

            public string Kind { get; }

            public Func<IServiceProvider, DbContext> Create { get; }

            public Func<DbContext, IReadOnlyList<Type>> Tables { get; }

            public IReadOnlyList<Type> Cleared { get; }

            public static ContextTarget Master(bool sharesSettings) => new(
                "master",
                provider => provider.GetRequiredService<IDbContextFactory<MasterDBContext>>().CreateDbContext(),
                _ => sharesSettings ? MasterTables.Where(type => type != typeof(SettingAggregate)).ToList() : MasterTables,
                Array.Empty<Type>());

            public static ContextTarget Tenant(Tenant tenant) => new(
                "tenant",
                provider =>
                {
                    provider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    return provider.GetRequiredService<IDbContextFactory<TenantDBContext>>().CreateDbContext();
                },
                context => TenantTables.Concat(IdentityTables(context)).ToList(),
                SearchTables);

            /// <summary>
            /// HtmlText - the one module still on the framework seam, in its own context on the tenant database. Its
            /// rows go with a CASCADE when the tenant context deletes Module, and come back here, after it.
            /// </summary>
            public static ContextTarget HtmlText(Tenant tenant) => new(
                "htmltext",
                provider =>
                {
                    provider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    return provider.GetRequiredService<IDbContextFactory<Stomme.Modules.HtmlText.Repository.HtmlTextContext>>().CreateDbContext();
                },
                _ => new[] { typeof(Stomme.Modules.HtmlText.Repository.HtmlTextAggregate) },
                Array.Empty<Type>());

            /// <summary>The ASP.NET Identity tables in the tenant context's model - the user first, the rest refer to it.</summary>
            private static IEnumerable<Type> IdentityTables(DbContext context) =>
                context.Model.GetEntityTypes()
                    .Where(entity => !entity.IsOwned())
                    .Select(entity => entity.ClrType)
                    .Where(type => type.Namespace == "Microsoft.AspNetCore.Identity")
                    .OrderBy(type => type == typeof(Microsoft.AspNetCore.Identity.IdentityUser) ? 0 : 1)
                    .ThenBy(type => type.FullName, StringComparer.Ordinal);
        }

        /// <summary>Replacing one context's tables: one transaction, delete child-first, insert parent-first, check, reset.</summary>
        private sealed class ContextReplace : IAsyncDisposable
        {
            private readonly IServiceScope _scope;
            private readonly DbContext _context;
            private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;
            private readonly IReadOnlyList<Type> _tables;
            private readonly IRelationalDatabase _database;
            private readonly List<(string Table, string Column)> _relaxed;
            private readonly Dictionary<Type, int> _expected = new();
            private readonly List<string> _batch = new();
            private Type? _table;

            private ContextReplace(IServiceScope scope, DbContext context, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, IReadOnlyList<Type> tables, IRelationalDatabase database, List<(string Table, string Column)> relaxed)
            {
                _scope = scope;
                _context = context;
                _transaction = transaction;
                _tables = tables;
                _database = database;
                _relaxed = relaxed;
            }

            public static async Task<ContextReplace> BeginAsync(IServiceScopeFactory scopes, ContextTarget target, CancellationToken cancellationToken)
            {
                var scope = scopes.CreateScope();
                var context = target.Create(scope.ServiceProvider);
                var tables = target.Tables(context);
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                foreach (var table in target.Cleared.Concat(tables.Reverse()))
                {
                    await (Task<int>)DeleteMethod.MakeGenericMethod(table).Invoke(null, new object[] { context, cancellationToken })!;
                }

                var database = (context as Stomme.Repository.Databases.Interfaces.IMultiDatabase)?.ActiveDatabase as IRelationalDatabase
                    ?? throw new InvalidOperationException("The context has no relational provider.");
                var relaxed = new List<(string Table, string Column)>();
                foreach (var identity in tables.Select(table => IdentityColumn(context, table)).OfType<(string Table, string Column)>())
                {
                    if (await database.AllowExplicitKeysAsync(context, identity.Table, identity.Column, cancellationToken))
                    {
                        relaxed.Add(identity);
                    }
                }

                return new ContextReplace(scope, context, transaction, tables, database, relaxed);
            }

            public async Task StartTableAsync(string name, CancellationToken cancellationToken)
            {
                await FlushAsync(cancellationToken);
                _table = _tables.FirstOrDefault(type => TableName(type) == name)
                    ?? throw new InvalidDataException($"The snapshot carries a table this database does not: {name}.");
                _expected[_table] = 0;
            }

            public async Task AddRowAsync(string json, CancellationToken cancellationToken)
            {
                if (_table is null)
                {
                    throw new InvalidDataException("A row before any table.");
                }

                _batch.Add(json);
                _expected[_table]++;
                if (_batch.Count >= BatchSize)
                {
                    await FlushAsync(cancellationToken);
                }
            }

            public async Task CompleteAsync(CancellationToken cancellationToken)
            {
                await FlushAsync(cancellationToken);
                foreach (var table in _tables)
                {
                    var count = await (Task<int>)CountMethod.MakeGenericMethod(table).Invoke(null, new object[] { _context, cancellationToken })!;
                    var expected = _expected.GetValueOrDefault(table);
                    if (count != expected)
                    {
                        throw new InvalidOperationException($"{PartName}: {table.Name} holds {count} rows after the replace, the snapshot {expected}. The replace is not trusted.");
                    }

                    if (IdentityColumn(_context, table) is { } identity)
                    {
                        await _database.ResetIdentityAsync(_context, identity.Table, identity.Column, cancellationToken);
                    }
                }

                foreach (var (table, column) in _relaxed)
                {
                    await _database.RestoreGeneratedAlwaysAsync(_context, table, column, cancellationToken);
                }

                await _transaction.CommitAsync(cancellationToken);
            }

            /// <summary>The table and column of a single database-generated int key, or null.</summary>
            private static (string Table, string Column)? IdentityColumn(DbContext context, Type table)
            {
                var entity = context.Model.FindEntityType(table)!;
                var key = entity.FindPrimaryKey()!.Properties;
                if (key.Count != 1 || key[0].ClrType != typeof(int) || key[0].ValueGenerated != ValueGenerated.OnAdd)
                {
                    return null;
                }

                var tableName = entity.GetTableName()!;
                return (tableName, key[0].GetColumnName(StoreObjectIdentifier.Table(tableName, entity.GetSchema()))!);
            }

            private async Task FlushAsync(CancellationToken cancellationToken)
            {
                if (_table is null || _batch.Count == 0)
                {
                    return;
                }

                // SQL Server takes original identity values only with IDENTITY_INSERT ON, for one table at a time.
                var identity = IdentityColumn(_context, _table);
                if (identity is { } keyed)
                {
                    await _database.BeginExplicitKeyInsertsAsync(_context, keyed.Table, cancellationToken);
                }

                try
                {
                    await (Task)InsertMethod.MakeGenericMethod(_table).Invoke(null, new object[] { _context, _batch.ToList(), cancellationToken })!;
                }
                catch when (identity is not null)
                {
                    // Switch it back before the connection returns to the pool; the insert's failure is the one reported.
                    try
                    {
                        await _database.EndExplicitKeyInsertsAsync(_context, identity.Value.Table, CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // the transaction is already doomed; the rollback and the context's disposal follow
                    }

                    throw;
                }

                if (identity is { } inserted)
                {
                    await _database.EndExplicitKeyInsertsAsync(_context, inserted.Table, cancellationToken);
                }

                _batch.Clear();
            }

            public async ValueTask DisposeAsync()
            {
                await _transaction.DisposeAsync();
                await _context.DisposeAsync();
                _scope.Dispose();
            }
        }

        /// <summary>A structural record in the part: a context on a database, or the table the rows that follow belong to.</summary>
        private sealed class RecordHeader
        {
            public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

            [System.Text.Json.Serialization.JsonPropertyName("$database")]
            public string? Database { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("$context")]
            public string? Context { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("$tenants")]
            public List<int>? Tenants { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("$table")]
            public string? Table { get; set; }

            /// <summary>A header, or null for a row - a row is a store document, which never has a "$" member.</summary>
            public static RecordHeader? TryParse(string json) =>
                json.StartsWith("{\"$", StringComparison.Ordinal) ? JsonSerializer.Deserialize<RecordHeader>(json, Json) : null;
        }
    }
}
