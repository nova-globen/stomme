using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Stomme.Infrastructure;
using Stomme.Models;

namespace Stomme.Repository
{
    public interface IMigrationHistoryRepository
    {
        Task<IEnumerable<MigrationHistory>> GetMigrationHistoryAsync();
    }
    public class MigrationHistoryRepository : IMigrationHistoryRepository
    {
        private readonly IDbContextFactory<TenantDBContext> _dbContextFactory;
        private readonly ITenantManager _tenantManager;

        public MigrationHistoryRepository(IDbContextFactory<TenantDBContext> dbContextFactory, ITenantManager tenantManager)
        {
            _dbContextFactory = dbContextFactory;
            _tenantManager = tenantManager;
        }

        public async Task<IEnumerable<MigrationHistory>> GetMigrationHistoryAsync()
        {
            // The ledger is EF Core's own migration bookkeeping. A non-relational tenant
            // has no such ledger — return an empty list rather than opening an EF context a document store
            // cannot back (or inventing a bootstrap record that would present fiction as history).
            var tenant = await _tenantManager.GetTenantAsync();
            if (!Stores.FrameworkStoreProviderSelection.IsRelationalProvider(tenant?.DBType))
            {
                return new List<MigrationHistory>();
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();
            return await db.MigrationHistory.ToListAsync();
        }
    }
}
