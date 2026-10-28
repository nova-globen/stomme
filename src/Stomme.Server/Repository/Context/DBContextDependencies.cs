using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Stomme.Infrastructure;

namespace Stomme.Repository
{
    public interface IDBContextDependencies
    {
        ITenantManager TenantManager { get; }
        IHttpContextAccessor Accessor { get; }
        IConfigurationRoot Config { get; }

        /// <summary>
        /// The host's own settings view. Carried here for one reason: the contexts expand the
        /// <c>|DataDirectory|</c> token in a tenant connection string, and that value has to belong to THIS host
        /// rather than to a process-wide slot the last-composed host wrote (BL-153,
        /// <c>IConfigManager.GetDataDirectory</c>).
        /// </summary>
        IConfigManager ConfigManager { get; }
    }

    public class DBContextDependencies : IDBContextDependencies
    {
        public DBContextDependencies(
            ITenantManager tenantManager,
            IHttpContextAccessor httpContextAccessor,
            IConfigurationRoot config,
            IConfigManager configManager)
        {
            TenantManager = tenantManager;
            Accessor = httpContextAccessor;
            Config = config;
            ConfigManager = configManager;
        }

        public ITenantManager TenantManager { get; }
        public IHttpContextAccessor Accessor { get; }
        public IConfigurationRoot Config { get; }
        public IConfigManager ConfigManager { get; }
    }
}
