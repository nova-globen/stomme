using System;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// On a non-relational install, <c>GET /api/migrationhistory</c> returns an
    /// <b>empty list</b>: a document store has no EF migration ledger, and inventing a bootstrap record would
    /// present fiction as history. The guard lives in <see cref="MigrationHistoryRepository"/>, keyed on the
    /// current tenant's <c>DBType</c> through the same predicate the install pipeline branches on
    /// (<c>FrameworkStoreProviderSelection.IsRelationalProvider</c>). The factory stub here throws if EF is
    /// touched, so the two tests validate the guard in BOTH directions: a document tenant never opens the EF
    /// factory, and a relational tenant still does.
    /// </summary>
    public class MigrationHistoryNonRelationalTests
    {
        [Fact]
        public async Task Document_store_tenant_reads_an_empty_ledger_without_touching_EF()
        {
            var factory = new RecordingThrowingFactory();
            var repository = new MigrationHistoryRepository(
                factory, new StubTenantManager(typeof(Stomme.Database.RavenDb.RavenDatabase).AssemblyQualifiedName));

            var history = await repository.GetMigrationHistoryAsync();

            Assert.Empty(history);
            Assert.False(factory.Touched, "the EF ledger context must never be opened on a document-store tenant");
        }

        [Fact]
        public async Task Relational_tenant_still_reads_through_the_EF_ledger()
        {
            var factory = new RecordingThrowingFactory();
            var repository = new MigrationHistoryRepository(
                factory, new StubTenantManager(typeof(Stomme.Database.Sqlite.SqliteDatabase).AssemblyQualifiedName));

            await Assert.ThrowsAsync<EfLedgerTouchedException>(() => repository.GetMigrationHistoryAsync());
            Assert.True(factory.Touched, "a relational tenant must still read EF's own ledger");
        }

        private sealed class EfLedgerTouchedException : Exception
        {
        }

        private sealed class RecordingThrowingFactory : IDbContextFactory<TenantDBContext>
        {
            public bool Touched { get; private set; }

            public TenantDBContext CreateDbContext()
            {
                Touched = true;
                throw new EfLedgerTouchedException();
            }
        }

        private sealed class StubTenantManager : ITenantManager
        {
            private readonly Tenant _tenant;

            public StubTenantManager(string dbType) => _tenant = new Tenant { DBType = dbType };

            public Task<Alias> GetAliasAsync() => Task.FromResult<Alias>(null);

            public Task<Tenant> GetTenantAsync() => Task.FromResult(_tenant);

            public Tenant GetResolvedTenant() => _tenant;

            public Task SetAliasAsync(Alias alias) => Task.CompletedTask;

            public Task SetAliasAsync(int tenantId, int siteId) => Task.CompletedTask;

            public void SetTenant(Tenant tenant)
            {
            }
        }
    }
}
