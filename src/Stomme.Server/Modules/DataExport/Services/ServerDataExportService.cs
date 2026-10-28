#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Application;
using Stomme.Shared;

namespace Stomme.Modules.DataExport.Services
{
    /// <summary>
    /// The server-side <see cref="IDataExportService"/>: what the module's components get when they render on the
    /// server (static and interactive-server modes), where there is no HTTP hop to make.
    /// </summary>
    /// <remarks>
    /// It goes through the SAME mediator requests the controller sends, so <c>[Requires]</c> is enforced here exactly
    /// as it is over HTTP - a component rendering on the server is not a way round the keys. A refused request comes
    /// back as <see cref="Guid.Empty"/>, which is what the HTTP client returns for a 400.
    /// </remarks>
    public sealed class ServerDataExportService(IMediator mediator, SiteState siteState) : IDataExportService, ITransientService
    {
        public async Task<List<ExportSourceView>> GetSourcesAsync() =>
            (await mediator.Send(new ListExportSourcesQuery())).ToList();

        public async Task<List<ExportJobView>> GetMyExportsAsync() =>
            (await mediator.Send(new ListMyExportsQuery())).ToList();

        public async Task<List<ExportJobView>> GetSiteExportsAsync() =>
            (await mediator.Send(new ListSiteExportsQuery())).ToList();

        public async Task<Guid> RequestAsync(ExportRequest request)
        {
            try
            {
                return await mediator.Send(new RequestExportCommand(request));
            }
            catch (ExportRequestRejectedException)
            {
                return Guid.Empty;
            }
        }

        public Task CancelAsync(Guid exportId) => mediator.Send(new CancelExportCommand(exportId));

        public async Task<List<ExportScheduleView>> GetSchedulesAsync() =>
            (await mediator.Send(new ListExportSchedulesQuery())).ToList();

        // A refusal throws its own message, which is written to be shown - what the HTTP client's ServiceRefusalException carries.
        public Task<ExportScheduleView> CreateScheduleAsync(ExportScheduleRequest schedule) =>
            mediator.Send(new CreateExportScheduleCommand(schedule));

        public Task PauseScheduleAsync(Guid scheduleId) => ChangeAsync(scheduleId, ExportScheduleAction.Pause);

        public Task ResumeScheduleAsync(Guid scheduleId) => ChangeAsync(scheduleId, ExportScheduleAction.Resume);

        public Task DeleteScheduleAsync(Guid scheduleId) => ChangeAsync(scheduleId, ExportScheduleAction.Delete);

        public async Task TakeOverScheduleAsync(Guid scheduleId)
        {
            if (!await mediator.Send(new TakeOverExportScheduleCommand(scheduleId)))
            {
                throw new ExportRequestRejectedException("There is no such schedule on this site.");
            }
        }

        private async Task ChangeAsync(Guid scheduleId, ExportScheduleAction action)
        {
            if (!await mediator.Send(new ChangeExportScheduleCommand(scheduleId, action)))
            {
                throw new ExportRequestRejectedException("There is no such schedule for you on this site.");
            }
        }

        public string DownloadUrl(Guid exportId)
        {
            // The same shape ServiceBase.CreateApiUrl builds on the client: the alias path scopes a virtual-folder tenant.
            var path = siteState.Alias?.Path;
            return (string.IsNullOrEmpty(path) ? "/" : "/" + path + "/") + "api/DataExport/" + exportId + "/download";
        }
    }
}
