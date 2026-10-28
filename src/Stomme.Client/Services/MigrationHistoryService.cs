using Stomme.Models;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Stomme.Documentation;
using Stomme.Shared;

namespace Stomme.Services
{
    /// <summary>
    /// Service to manage <see cref="MigrationHistory/>s on the Stomme installation.
    /// </summary>
    public interface IMigrationHistoryService
    {
        /// <summary>
        /// Get all <see cref="MigrationHistory"/>s
        /// </summary>
        /// <returns></returns>
        Task<List<MigrationHistory>> GetMigrationHistoryAsync();
    }

    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class MigrationHistoryService : ServiceBase, IMigrationHistoryService
    {
        public MigrationHistoryService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string Apiurl => CreateApiUrl("MigrationHistory");

        public async Task<List<MigrationHistory>> GetMigrationHistoryAsync()
        {
            return await GetJsonAsync<List<MigrationHistory>>(Apiurl);
        }
    }
}
