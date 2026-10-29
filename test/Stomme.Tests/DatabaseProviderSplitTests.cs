using System;
using System.Reflection;
using Stomme.Database.PostgreSQL;
using Stomme.Database.RavenDb;
using Stomme.Database.Sqlite;
using Stomme.Database.SqlServer;
using Stomme.Databases;
using Stomme.Databases.Interfaces;
using Stomme.Infrastructure;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The split of <see cref="IDatabase"/> into a storage-agnostic core and the relational sub-interface
    /// <see cref="IRelationalDatabase"/>, the metadata-only <see cref="RavenDatabase"/>, and the install
    /// branch predicate <c>DatabaseManager.IsRelationalProvider</c> that gates EF Core create/migrate.
    /// <para>
    /// These are the parts verifiable WITHOUT a live RavenDB install (which is not drivable until the RavenDB
    /// store provider and its <c>IUserStore</c> are wired in). They do NOT claim a RavenDB install works — they pin the
    /// type shape and the branch predicate the install path keys on.
    /// </para>
    /// </summary>
    public class DatabaseProviderSplitTests
    {
        // The assembly-qualified provider strings exactly as AvailableDatabases / Tenant.DBType store them.
        private const string PostgresType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
        private const string SqlServerType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server";
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        [Fact]
        public void RavenDatabase_is_IDatabase_but_not_IRelationalDatabase()
        {
            var raven = new RavenDatabase();

            Assert.IsAssignableFrom<IDatabase>(raven);
            Assert.False(raven is IRelationalDatabase,
                "RavenDatabase must implement the storage-agnostic core only; deriving it from DatabaseBase/" +
                "IRelationalDatabase would make DatabaseManager take the EF create/migrate branch and throw on " +
                "IRelationalDatabaseCreator.");
        }

        [Fact]
        public void The_three_relational_providers_are_IRelationalDatabase()
        {
            Assert.IsAssignableFrom<IRelationalDatabase>(new PostgreSQLDatabase());
            Assert.IsAssignableFrom<IRelationalDatabase>(new SqliteDatabase());
            Assert.IsAssignableFrom<IRelationalDatabase>(new SqlServerDatabase());

            // DatabaseBase itself declares the relational contract, so every current and future provider that
            // extends it inherits it.
            Assert.True(typeof(IRelationalDatabase).IsAssignableFrom(typeof(DatabaseBase)));
        }

        [Fact]
        public void RavenDatabase_is_constructible_reflectively_like_the_other_providers()
        {
            // The installer resolves a provider by Type.GetType(dbtype) + Activator.CreateInstance (the
            // AvailableDatabases entry names it). RavenDatabase.TypeName must round-trip through that path.
            var typeName = new RavenDatabase().TypeName;
            Assert.Equal(RavenType, typeName);

            var type = Type.GetType(typeName);
            Assert.NotNull(type);

            var instance = Activator.CreateInstance(type) as IDatabase;
            Assert.NotNull(instance);
            Assert.IsType<RavenDatabase>(instance);
            Assert.False(instance is IRelationalDatabase);
            Assert.Equal("RavenDB", instance.Name);
            Assert.Equal("RavenDB", instance.FriendlyName);
            Assert.Equal("Stomme.Server", instance.AssemblyName);
        }

        [Theory]
        [InlineData(PostgresType, true)]
        [InlineData(SqliteType, true)]
        [InlineData(SqlServerType, true)]
        [InlineData(RavenType, false)]      // the non-relational branch: skips IRelationalDatabaseCreator + MigrateAsync
        [InlineData("Not.A.Real.Type, Nowhere", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsRelationalProvider_gates_the_install_branch(string? databaseType, bool expected)
        {
            // Reflect the PRODUCTION predicate DatabaseManager branches on (CreateDatabaseAsync's creator, and
            // MigrateMaster/MigrateTenants' MigrateAsync are all gated by this). Testing the predicate directly
            // is mutation-checked: an "always true" mutation fails the RavenDB/garbage cases, an "always false"
            // mutation fails the three relational cases.
            var method = typeof(DatabaseManager).GetMethod(
                "IsRelationalProvider",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var actual = (bool)method.Invoke(null, new object?[] { databaseType })!;
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void The_relational_metadata_members_survive_on_the_core_interface()
        {
            // The five metadata members are the storage-agnostic core both providers share; the eleven
            // relational members moved to IRelationalDatabase. Pin the split so neither drifts back.
            var core = typeof(IDatabase);
            Assert.NotNull(core.GetProperty(nameof(IDatabase.AssemblyName)));
            Assert.NotNull(core.GetProperty(nameof(IDatabase.FriendlyName)));
            Assert.NotNull(core.GetProperty(nameof(IDatabase.Name)));
            Assert.NotNull(core.GetProperty(nameof(IDatabase.Provider)));
            Assert.NotNull(core.GetProperty(nameof(IDatabase.TypeName)));

            // The relational surface is NOT on the core interface.
            Assert.Null(core.GetMethod("UseDatabase"));
            Assert.Null(core.GetMethod("RewriteName"));
            Assert.Null(core.GetMethod("ExecuteNonQueryAsync"));

            // ... it is on the sub-interface.
            var relational = typeof(IRelationalDatabase);
            Assert.NotNull(relational.GetMethod("UseDatabase"));
            Assert.NotNull(relational.GetMethod("RewriteName"));
            Assert.NotNull(relational.GetMethod("ExecuteNonQueryAsync"));
            Assert.NotNull(relational.GetMethod("UpdateIdentityStoreTableNames"));
        }
    }
}
