using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Dashboards.Services
{
    public interface IDashboardsService
    {
        Task<List<DashboardView>> ListAsync();

        Task<DashboardView> GetAsync(Guid id);

        Task<WidgetDataView> GetWidgetDataAsync(Guid id, Guid widgetId);

        Task<DashboardView> CreateAsync(CreateDashboardRequest request);

        Task<DashboardView> RenameAsync(Guid id, RenameDashboardRequest request);

        Task<DashboardView> CloneAsync(Guid id, CloneDashboardRequest request);

        Task<DashboardView> PublishAsync(Guid id);

        Task<DashboardView> RearrangeLayoutAsync(Guid id, LayoutRequest request);

        Task DeleteAsync(Guid id);

        Task<DashboardView> AddWidgetAsync(Guid id, AddWidgetRequest request);

        Task<DashboardView> UpdateWidgetAsync(Guid id, Guid widgetId, UpdateWidgetRequest request);

        Task<DashboardView> MoveWidgetAsync(Guid id, Guid widgetId, MoveWidgetRequest request);

        Task<DashboardView> SaveGridStateAsync(Guid id, Guid widgetId, GridStateRequest request);

        Task<DashboardView> ReorderWidgetAsync(Guid id, Guid widgetId, ReorderWidgetRequest request);

        Task<DashboardView> RemoveWidgetAsync(Guid id, Guid widgetId);
    }

    public class DashboardsService : ServiceBase, IDashboardsService, IClientService
    {
        /// <summary>A refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public DashboardsService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Dashboards");

        public async Task<List<DashboardView>> ListAsync() => await GetJsonAsync<List<DashboardView>>(ApiUrl) ?? new List<DashboardView>();

        public Task<DashboardView> GetAsync(Guid id) => GetJsonAsync<DashboardView>($"{ApiUrl}/{id}");

        public Task<WidgetDataView> GetWidgetDataAsync(Guid id, Guid widgetId) => GetJsonAsync<WidgetDataView>($"{ApiUrl}/{id}/widgets/{widgetId}/data");

        public Task<DashboardView> CreateAsync(CreateDashboardRequest request) => PostJsonAsync<CreateDashboardRequest, DashboardView>(ApiUrl, request);

        public Task<DashboardView> RenameAsync(Guid id, RenameDashboardRequest request) => PutJsonAsync<RenameDashboardRequest, DashboardView>($"{ApiUrl}/{id}", request);

        public Task<DashboardView> CloneAsync(Guid id, CloneDashboardRequest request) => PostJsonAsync<CloneDashboardRequest, DashboardView>($"{ApiUrl}/{id}/clone", request);

        public Task<DashboardView> PublishAsync(Guid id) => PostJsonAsync<object, DashboardView>($"{ApiUrl}/{id}/publish", new object());

        public Task<DashboardView> RearrangeLayoutAsync(Guid id, LayoutRequest request) => PostJsonAsync<LayoutRequest, DashboardView>($"{ApiUrl}/{id}/layout", request);

        public Task DeleteAsync(Guid id) => DeleteAsync($"{ApiUrl}/{id}");

        public Task<DashboardView> AddWidgetAsync(Guid id, AddWidgetRequest request) => PostJsonAsync<AddWidgetRequest, DashboardView>($"{ApiUrl}/{id}/widgets", request);

        public Task<DashboardView> UpdateWidgetAsync(Guid id, Guid widgetId, UpdateWidgetRequest request) => PutJsonAsync<UpdateWidgetRequest, DashboardView>($"{ApiUrl}/{id}/widgets/{widgetId}", request);

        public Task<DashboardView> MoveWidgetAsync(Guid id, Guid widgetId, MoveWidgetRequest request) => PostJsonAsync<MoveWidgetRequest, DashboardView>($"{ApiUrl}/{id}/widgets/{widgetId}/move", request);

        public Task<DashboardView> SaveGridStateAsync(Guid id, Guid widgetId, GridStateRequest request) => PutJsonAsync<GridStateRequest, DashboardView>($"{ApiUrl}/{id}/widgets/{widgetId}/grid-state", request);

        public Task<DashboardView> ReorderWidgetAsync(Guid id, Guid widgetId, ReorderWidgetRequest request) => PostJsonAsync<ReorderWidgetRequest, DashboardView>($"{ApiUrl}/{id}/widgets/{widgetId}/reorder", request);

        public async Task<DashboardView> RemoveWidgetAsync(Guid id, Guid widgetId)
        {
            await DeleteAsync($"{ApiUrl}/{id}/widgets/{widgetId}");
            return await GetAsync(id);
        }
    }
}
