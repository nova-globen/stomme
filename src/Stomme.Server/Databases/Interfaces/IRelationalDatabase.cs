using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Stomme.Databases.Interfaces
{
    /// <summary>
    /// The relational-migrations surface of a database provider (D53) — the eleven members that only make
    /// sense for an EF-Core-backed relational store: EF options wiring, SQL name/value rewriting and the DDL
    /// helpers the migration builders call. Split off <see cref="IDatabase"/> so a document-store provider
    /// (<c>RavenDatabase</c>) can implement the storage-agnostic core WITHOUT pretending to supply
    /// <see cref="UseDatabase"/> (RavenDB has no EF Core provider) or any of the SQL helpers.
    /// <para>
    /// <c>DatabaseBase</c> implements this; the three concrete providers (PostgreSQL/SqlServer/Sqlite) extend
    /// <c>DatabaseBase</c> unchanged. The migration ecosystem still receives an <see cref="IDatabase"/> by
    /// reflection (the concrete migration/entity-builder constructors take <see cref="IDatabase"/> and the
    /// reflective ctor lookup keys on it), then reads it through the sub-interface — migrations only ever run
    /// on a relational install, so the narrowing is always valid there.
    /// </para>
    /// </summary>
    public interface IRelationalDatabase : IDatabase
    {
        public OperationBuilder<AddColumnOperation> AddAutoIncrementColumn(ColumnsBuilder table, string name);

        public string ConcatenateSql(params string[] values);

        public Task<int> ExecuteNonQueryAsync(string connectionString, string query);

        public Task<IDataReader> ExecuteReaderAsync(string connectionString, string query);

        public string DelimitName(string name); // only used in conjunction with method using MigrationBuilder.Sql()

        public string RewriteName(string name);

        public string RewriteValue(object value);

        public void UpdateIdentityStoreTableNames(ModelBuilder builder);

        public void DropColumn(MigrationBuilder builder, string name, string table);

        public void AlterStringColumn(MigrationBuilder builder, string name, string table, int length, bool nullable, bool unicode, string index);

        public DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder optionsBuilder, string connectionString);

        /// <summary>
        /// BL-81 (D106 Q5): whether a logical restore - rows inserted with their ORIGINAL keys, then the identity moved past
        /// them - is supported on this provider. Every relational provider supports it since BL-173 drove SQL Server.
        /// </summary>
        public bool SupportsLogicalRestore { get; }

        /// <summary>
        /// BL-81: after a restore inserted rows with their original keys, move <paramref name="table"/>'s identity past the
        /// highest one, so the next insert collides with nothing. Runs on <paramref name="context"/>'s connection and
        /// transaction. SQLite needs nothing - its next rowid follows the highest present.
        /// </summary>
        public Task ResetIdentityAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// BL-81: lets the restore insert ORIGINAL keys into <paramref name="table"/> - on PostgreSQL an identity declared
        /// GENERATED ALWAYS refuses them (measured: 428C9 on the Tenant table), so it is switched to BY DEFAULT inside the
        /// restore's transaction. True when it switched one; the caller then calls <see cref="RestoreGeneratedAlwaysAsync"/>.
        /// </summary>
        public Task<bool> AllowExplicitKeysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken);

        /// <summary>BL-81: puts back the GENERATED ALWAYS that <see cref="AllowExplicitKeysAsync"/> relaxed.</summary>
        public Task RestoreGeneratedAlwaysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// BL-173: called around each batch of rows a restore inserts with their ORIGINAL keys into <paramref name="table"/>,
        /// whose key the database generates - <see cref="EndExplicitKeyInsertsAsync"/> after it. SQL Server refuses an explicit
        /// identity value unless IDENTITY_INSERT is ON for the table, and allows it ON for ONE table per session at a time, so
        /// the switch brackets each batch rather than the whole restore. Runs on <paramref name="context"/>'s connection.
        /// </summary>
        public Task BeginExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken);

        /// <summary>BL-173: ends what <see cref="BeginExplicitKeyInsertsAsync"/> began.</summary>
        public Task EndExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken);
    }
}
