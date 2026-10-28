using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Stomme.Databases.Interfaces;

namespace Stomme.Databases
{
    public abstract class DatabaseBase : IRelationalDatabase
    {
        private static string _assemblyName;

        private static string _typeName;

        protected DatabaseBase(string name, string friendlyName)
        {
            Name = name;
            FriendlyName = friendlyName;
        }

        protected static void Initialize(Type type)
        {
            var typeQualifiedName = type.AssemblyQualifiedName;
            var assembly = type.Assembly;
            var assemblyName = assembly.FullName;

            _typeName = typeQualifiedName.Substring(0, typeQualifiedName.IndexOf(", Version"));
            _assemblyName = assemblyName.Substring(0, assemblyName.IndexOf(", Version"));
        }

        public string AssemblyName => _assemblyName;

        public string FriendlyName { get; }

        public string Name { get; }

        public abstract string Provider { get; }

        public string TypeName => _typeName;

        public abstract OperationBuilder<AddColumnOperation> AddAutoIncrementColumn(ColumnsBuilder table, string name);

        public virtual bool SupportsLogicalRestore => true;

        public virtual Task ResetIdentityAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task<bool> AllowExplicitKeysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken) => Task.FromResult(false);

        public virtual Task RestoreGeneratedAlwaysAsync(DbContext context, string table, string keyColumn, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task BeginExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task EndExplicitKeyInsertsAsync(DbContext context, string table, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual string ConcatenateSql(params string[] values)
        {
            var returnValue = String.Empty;
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    returnValue += " + ";
                }
                returnValue += values[i];
            }

            return returnValue;
        }

        public abstract Task<int> ExecuteNonQueryAsync(string connectionString, string query);

        public abstract Task<IDataReader> ExecuteReaderAsync(string connectionString, string query);

        public virtual string DelimitName(string name)
        {
            return name;
        }

        public virtual string RewriteName(string name)
        {
            return name;
        }

        public virtual string RewriteValue(object value)
        {
            return value.ToString();
        }

        public virtual void UpdateIdentityStoreTableNames(ModelBuilder builder)
        {

        }

        public virtual void DropColumn(MigrationBuilder builder, string name, string table)
        {
            builder.DropColumn(name, table);
        }

        public virtual void AlterStringColumn(MigrationBuilder builder, string name, string table, int length, bool nullable, bool unicode, string index)
        {
            builder.AlterColumn<string>(RewriteName(name), RewriteName(table), maxLength: length, nullable: nullable, unicode: unicode);
        }

        public abstract DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder optionsBuilder, string connectionString);
    }
}
