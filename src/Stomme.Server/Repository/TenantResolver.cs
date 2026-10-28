using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;

namespace Stomme.Repository
{
    // class deprecated and replaced by ITenantManager
    public interface ITenantResolver
    {
        Task<Alias> GetAliasAsync();
        Task<Tenant> GetTenantAsync();
    }

    public class TenantResolver : ITenantResolver
    {
        private readonly ITenantManager _tenantManager;

        public TenantResolver(ITenantManager tenantManager)
        {
            _tenantManager = tenantManager;
        }

        public Task<Alias> GetAliasAsync()
        {
            return _tenantManager.GetAliasAsync();
        }

        public Task<Tenant> GetTenantAsync()
        {
            return _tenantManager.GetTenantAsync();
        }
    }
}
