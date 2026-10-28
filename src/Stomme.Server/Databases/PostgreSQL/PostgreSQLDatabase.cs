using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using EFCore.NamingConventions.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Stomme.Databases;

namespace Stomme.Database.PostgreSQL
{
    public class PostgreSQLDatabase : DatabaseBase
    {
        private static string _friendlyName => "PostgreSQL";

        private static string _name => "PostgreSQL";

        private readonly INameRewriter _rewriter;

        static PostgreSQLDatabase()
        {
            Initialize(typeof(PostgreSQLDatabase));
        }

        public PostgreSQLDatabase() : base(_name, _friendlyName)
        {
            _rewriter = new SnakeCaseNameRewriter(CultureInfo.InvariantCulture);
        }

        public override string Provider => "Npgsql.EntityFrameworkCore.PostgreSQL";

        public override OperationBuilder<AddColumnOperation> AddAutoIncrementColumn(ColumnsBuilder table, string name)
        {
            return table.Column<int>(name: name, nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);
        }

        public override string ConcatenateSql(params string[] values)
        {
            var returnValue = String.Empty;
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    returnValue += " || ";
                }
                returnValue += values[i];
            }

            return returnValue;
        }

        public override async Task<int> ExecuteNonQueryAsync(string connectionString, string query)
        {
            var conn = new NpgsqlConnection(connectionString);
            var cmd = conn.CreateCommand();
            await using (conn)
            {
                await PrepareCommandAsync(conn, cmd, query);
                var val = -1;
                try
                {
                    val = await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // an error occurred executing the query
                }
                return val;
            }

        }

        public override async Task<IDataReader> ExecuteReaderAsync(string connectionString, string query)
        {
            var conn = new NpgsqlConnection(connectionString);
            var cmd = conn.CreateCommand();
            await PrepareCommandAsync(conn, cmd, query);
            var dr = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            return dr;
        }

        public override string RewriteName(string name)
        {
            return _rewriter.RewriteName(name);
        }

        public override string DelimitName(string name)
        {
            return $"\"{name}\"";
        }

        // BL-81: the column's identity sequence moves to one past the highest restored key (is_called=false: the next
        // nextval RETURNS that value). A column with no sequence yields NULL and setval, being STRICT, does nothing.
        public override async Task ResetIdentityAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken)
        {
            var delimitedTable = DelimitName(table);
            var sql = $"SELECT setval(pg_get_serial_sequence('{delimitedTable.Replace("'", "''")}', '{keyColumn.Replace("'", "''")}'), " +
                $"COALESCE((SELECT MAX({DelimitName(keyColumn)}) FROM {delimitedTable}), 0) + 1, false)";
#pragma warning disable EF1002 // names from the EF model, never input
            await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
#pragma warning restore EF1002
        }

        // BL-81: some tables' keys are GENERATED ALWAYS (the Tenant table, measured), which refuses an explicit value. PostgreSQL
        // DDL is transactional, so relaxing it inside the restore's transaction is undone by a rollback as well.
        public override async Task<bool> AllowExplicitKeysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken)
        {
#pragma warning disable EF1002 // names from the EF model, never input
            var generation = await context.Database.SqlQueryRaw<string>(
                $"SELECT identity_generation AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() " +
                $"AND table_name = '{table.Replace("'", "''")}' AND column_name = '{keyColumn.Replace("'", "''")}'").ToListAsync(cancellationToken);
            if (generation.FirstOrDefault() != "ALWAYS")
            {
                return false;
            }

            await context.Database.ExecuteSqlRawAsync($"ALTER TABLE {DelimitName(table)} ALTER COLUMN {DelimitName(keyColumn)} SET GENERATED BY DEFAULT", cancellationToken);
#pragma warning restore EF1002
            return true;
        }

        public override async Task RestoreGeneratedAlwaysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken)
        {
#pragma warning disable EF1002 // names from the EF model, never input
            await context.Database.ExecuteSqlRawAsync($"ALTER TABLE {DelimitName(table)} ALTER COLUMN {DelimitName(keyColumn)} SET GENERATED ALWAYS", cancellationToken);
#pragma warning restore EF1002
        }

        public override string RewriteValue(object value)
        {
            var type = value.GetType().Name;
            if (type == "DateTime")
            {
                return $"'{value}'";
            }
            if (type == "Boolean")
            {
                return (bool)value ? "true" : "false"; // PostgreSQL uses true/false for boolean values
            }
            return value.ToString();
        }

        public override void UpdateIdentityStoreTableNames(ModelBuilder builder)
        {
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                // the IdentityPasskeyData entity was introduced in .NET 10 and is not mapped to a database table so should be ignored
                if (entity.ClrType.Name != "IdentityPasskeyData")
                {
                    var tableName = entity.GetTableName();
                    if (tableName.StartsWith("AspNetUser"))
                    {
                        // replace table name
                        entity.SetTableName(RewriteName(entity.GetTableName()));

                        // replace column names
                        foreach (var property in entity.GetProperties())
                        {
                            property.SetColumnName(RewriteName(property.Name));
                        }

                        // replace key names
                        foreach (var key in entity.GetKeys())
                        {
                            key.SetName(RewriteName(key.GetName()));
                        }

                        // replace foreign key names
                        foreach (var key in entity.GetForeignKeys())
                        {
                            key.PrincipalKey.SetName(RewriteName(key.PrincipalKey.GetName()));
                        }

                        // replace index names
                        foreach (var index in entity.GetIndexes())
                        {
                            index.SetDatabaseName(RewriteName(index.GetDatabaseName()));
                        }
                    }
                }
            }
        }

        public override DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder optionsBuilder, string connectionString)
        {
            return optionsBuilder.UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .ReplaceService<IHistoryRepository, HistoryRepository>();
        }

        private async Task PrepareCommandAsync(NpgsqlConnection conn, NpgsqlCommand cmd, string query)
        {
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            cmd.Connection = conn;
            cmd.CommandText = query;
            cmd.CommandType = CommandType.Text;
        }
    }
}
