using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Stomme.Databases;

namespace Stomme.Database.SqlServer
{
    public class SqlServerDatabase : DatabaseBase
    {
        private static string _friendlyName => "SQL Server";

        private static string _name => "SqlServer";

        static SqlServerDatabase()
        {
            Initialize(typeof(SqlServerDatabase));
        }

        public SqlServerDatabase() : base(_name, _friendlyName)
        {
        }

        public override string Provider => "Microsoft.EntityFrameworkCore.SqlServer";

        public override OperationBuilder<AddColumnOperation> AddAutoIncrementColumn(ColumnsBuilder table, string name)
        {
            return table.Column<int>(name: name, nullable: false).Annotation("SqlServer:Identity", "1, 1");
        }

        public override void AlterStringColumn(MigrationBuilder builder, string name, string table, int length, bool nullable, bool unicode, string index)
        {
            var elements = index.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (elements.Length != 0)
            {
                builder.DropIndex(elements[0], table);
            }
            builder.AlterColumn<string>(name, table, maxLength: length, nullable: nullable, unicode: unicode);
            if (elements.Length != 0)
            {
                var columns = elements[1].Split(',');
                builder.CreateIndex(elements[0], table, columns, null, bool.Parse(elements[2]), null);
            }
        }

        // A restore inserts ORIGINAL keys, which SQL Server accepts only with IDENTITY_INSERT ON for that table - and
        // only one table per session at a time, so each batch is bracketed. No reseed follows (ResetIdentityAsync stays the
        // base no-op): an explicit identity value above the table's current one BECOMES the current one, so after the restore
        // the next insert already follows the highest restored key - measured with the identity rewound first.
        // That also keeps the permission bar at ALTER on the table; DBCC CHECKIDENT would need db_owner or db_ddladmin.
        public override async Task BeginExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken)
        {
#pragma warning disable EF1002 // names from the EF model, never input
            await context.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {DelimitName(table)} ON", cancellationToken);
#pragma warning restore EF1002
        }

        public override async Task EndExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken)
        {
#pragma warning disable EF1002 // names from the EF model, never input
            await context.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {DelimitName(table)} OFF", cancellationToken);
#pragma warning restore EF1002
        }

        public override string DelimitName(string name)
        {
            return $"[{name.Replace("]", "]]")}]";
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
                return (bool)value ? "1" : "0"; // SQL Server uses 1/0 for boolean values
            }
            return value.ToString();
        }

        public override async Task<int> ExecuteNonQueryAsync(string connectionString, string query)
        {
            var conn = new SqlConnection(FormatConnectionString(connectionString));
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
            var conn = new SqlConnection(FormatConnectionString(connectionString));
            var cmd = conn.CreateCommand();
            await PrepareCommandAsync(conn, cmd, query);
            var dr = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            return dr;
        }

        public override DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder optionsBuilder, string connectionString)
        {
            return optionsBuilder.UseSqlServer(connectionString)
                .ReplaceService<IHistoryRepository, HistoryRepository>();
        }

        private string FormatConnectionString(string connectionString)
        {
            return connectionString.Replace("|DataDirectory|", AppDomain.CurrentDomain.GetData("DataDirectory").ToString());
        }

        private async Task PrepareCommandAsync(SqlConnection conn, SqlCommand cmd, string query)
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
