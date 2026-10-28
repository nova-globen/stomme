using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Services;
using Stomme.Shared;
using [Owner].Module.[Module].Models;

namespace [Owner].Module.[Module].Services
{
    /// <summary>The service in the BROWSER: the module's API over HTTP.</summary>
    public class [Module]Service : ServiceBase, I[Module]Service
    {
        public [Module]Service(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("[Module]");

        public async Task<List<[Module]EntryModel>> ListEntriesAsync() =>
            await GetJsonAsync<List<[Module]EntryModel>>(ApiUrl) ?? new List<[Module]EntryModel>();

        public async Task<[Module]EntryModel> AddEntryAsync(Add[Module]EntryRequest request) =>
            await PostJsonAsync<Add[Module]EntryRequest, [Module]EntryModel>(ApiUrl, request);
    }
}
