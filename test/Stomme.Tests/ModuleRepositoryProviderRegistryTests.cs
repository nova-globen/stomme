using System;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The N-way module persistence registry (D80, Part A). It replaces the hard-coded
    /// <c>relational ? EF : Raven</c> binary with a lookup keyed on <c>Database:DefaultDBType</c>, so a third
    /// store (the file provider) is one registration and modules are unchanged. These tests pin the two contract
    /// properties the binary could not offer: every known provider resolves to its own factory, and an UNKNOWN
    /// provider FAILS LOUDLY instead of silently falling through to a default.
    /// </summary>
    public class ModuleRepositoryProviderRegistryTests
    {
        private const string SqlServerType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server";
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
        private const string PostgresType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
        private const string FileType = "Stomme.Database.File.FileDatabase, Stomme.Server";

        [Theory]
        [InlineData(SqlServerType)]
        [InlineData(SqliteType)]
        [InlineData(PostgresType)]
        public void Relational_types_resolve_to_the_relational_provider(string databaseType) =>
            Assert.IsType<RelationalModuleRepositoryProvider>(ModuleRepositoryProviderRegistry.Resolve(databaseType));

        [Fact]
        public void The_raven_type_resolves_to_the_raven_provider() =>
            Assert.IsType<RavenModuleRepositoryProvider>(ModuleRepositoryProviderRegistry.Resolve(RavenType));

        [Fact]
        public void The_file_type_resolves_to_the_file_provider() =>
            Assert.IsType<FileModuleRepositoryProvider>(ModuleRepositoryProviderRegistry.Resolve(FileType));

        // The whole point of the registry over the binary: an unknown provider is a misconfiguration that must
        // surface, not be silently persisted on EF or Raven.
        [Fact]
        public void An_unknown_provider_fails_loudly()
        {
            var unknown = "Stomme.Database.Nope.NopeDatabase, Stomme.Server";
            var ex = Assert.Throws<InvalidOperationException>(() => ModuleRepositoryProviderRegistry.Resolve(unknown));
            Assert.Contains(unknown, ex.Message);
            Assert.Contains("No module persistence provider", ex.Message);
        }

        [Fact]
        public void A_null_or_empty_provider_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => ModuleRepositoryProviderRegistry.Resolve(""));
            Assert.Throws<ArgumentNullException>(() => ModuleRepositoryProviderRegistry.Resolve(null!));
        }
    }
}
