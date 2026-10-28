using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Erasure;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Erasure.Services
{
    /// <summary>
    /// The erasure receipts contract (BL-116 increment 4, D112). Host only: the server answers 403 to anyone else, and
    /// the browser side reports that as an empty list. No method takes a site - the request is the site.
    /// </summary>
    public interface IErasureService
    {
        /// <summary>The site's latest receipts, newest first.</summary>
        Task<List<ErasureReceiptView>> GetReceiptsAsync(int take);

        /// <summary>Marks a failed run for another attempt by the job.</summary>
        Task<ErasureReceiptView> RetryAsync(Guid id);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Erasure</c>.</summary>
    public class ErasureService : ServiceBase, IErasureService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public ErasureService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Erasure");

        public async Task<List<ErasureReceiptView>> GetReceiptsAsync(int take) =>
            await GetJsonAsync<List<ErasureReceiptView>>($"{ApiUrl}/receipts?take={take}") ?? new List<ErasureReceiptView>();

        public async Task<ErasureReceiptView> RetryAsync(Guid id) =>
            await PostJsonAsync<object, ErasureReceiptView>($"{ApiUrl}/receipts/{id}/retry", new { });
    }
}
