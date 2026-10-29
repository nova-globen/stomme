using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;

namespace Stomme.Migrations
{
    public abstract class MultiDatabaseMigration : Migration
    {
        // Ctor param stays IDatabase (the reflective ctor lookup in MultiDatabaseMigrationsAssembly keys on
        // typeof(IDatabase) and every concrete migration declares that signature). The narrowing to the
        // relational surface happens here — migrations run only on a relational install — and ActiveDatabase is
        // exposed as IRelationalDatabase so migrations reach both metadata (Name/TypeName) and SQL helpers
        // (RewriteName/DelimitName/ConcatenateSql) unchanged.
        protected MultiDatabaseMigration(IDatabase database)
        {
            ActiveDatabase = (IRelationalDatabase)database;
        }

        protected IRelationalDatabase ActiveDatabase { get; }

        protected string DelimitName(string name)
        {
            return ActiveDatabase.DelimitName(name);
        }

        protected string RewriteName(string name)
        {
            return ActiveDatabase.RewriteName(name);
        }

        protected string RewriteValue(object value)
        {
            return ActiveDatabase.RewriteValue(value);
        }
    }
}
