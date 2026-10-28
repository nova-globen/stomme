using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Bunit;
using Stomme.Export;
using Stomme.Models;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Services;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ExportPage = Stomme.Modules.DataExport.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The data-export page says why a request or a schedule was refused (found driving BL-95's schedule form, 2026-10-05).
    /// </summary>
    /// <remarks>
    /// Every action reloads the page's lists afterwards, and the reload cleared the error first - so the server's reason,
    /// caught and set, was wiped before it was drawn: a schedule refused for running every five minutes, or an export the
    /// server refused, left the page exactly as it was. The reload now keeps what the action said; Refresh clears it.
    /// </remarks>
    public sealed class DataExportPageTests : BunitContext
    {
        private readonly FakeDataExportService _service = new();

        public DataExportPageTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IDataExportService>(_service);
        }

        [Fact]
        public void A_refused_schedule_shows_the_servers_reason_after_the_reload()
        {
            var cut = Render<ExportPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            var loadsBefore = _service.ScheduleLoads;

            cut.Find("#dataexport-schedule-name").Change("Nightly rows");
            cut.Find("#dataexport-schedule-cron").Change("*/5 * * * *");
            cut.Find("#dataexport-schedule").Click();

            cut.WaitForAssertion(() => Assert.Equal("The schedule was not created: Runs must be at least 15 minutes apart.", cut.Find("#dataexport-error").TextContent.Trim()));
            Assert.True(_service.ScheduleLoads > loadsBefore, "the page reloads its lists after the action - the reload that used to wipe the reason");

            // Refresh is a fresh look: it clears what the last action said.
            cut.Find("#dataexport-refresh").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#dataexport-error")));
        }

        [Fact]
        public void A_refused_export_request_says_so()
        {
            var cut = Render<ExportPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));

            cut.Find("#dataexport-request").Click();

            cut.WaitForAssertion(() => Assert.Equal("The export request was refused.", cut.Find("#dataexport-error").TextContent.Trim()));
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page() => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/dataexport", Name = "Data Export" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
            Action = "", // the router always sets it; the module logger reads it to name the log function

        };

        /// <summary>The key rows as the module definition seeds them - every key to Administrators.</summary>
        private static ModuleModel Module() => new()
        {
            ModuleId = 41,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(ExportPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        /// <summary>Refuses what the server refuses here: a request (an empty id) and a schedule (the server's reason).</summary>
        private sealed class FakeDataExportService : IDataExportService
        {
            public int ScheduleLoads { get; private set; }

            public Task<List<ExportSourceView>> GetSourcesAsync() =>
                Task.FromResult(new List<ExportSourceView> { new() { Key = "test.rows", SingleTable = true } });

            public Task<List<ExportJobView>> GetMyExportsAsync() => Task.FromResult(new List<ExportJobView>());
            public Task<List<ExportJobView>> GetSiteExportsAsync() => Task.FromResult(new List<ExportJobView>());
            public Task<Guid> RequestAsync(ExportRequest request) => Task.FromResult(Guid.Empty);
            public Task CancelAsync(Guid exportId) => Task.CompletedTask;
            public string DownloadUrl(Guid exportId) => "/api/DataExport/" + exportId + "/download";

            public Task<List<ExportScheduleView>> GetSchedulesAsync()
            {
                ScheduleLoads++;
                return Task.FromResult(new List<ExportScheduleView>());
            }

            public Task<ExportScheduleView> CreateScheduleAsync(ExportScheduleRequest schedule) =>
                throw new ServiceRefusalException("Runs must be at least 15 minutes apart.", HttpStatusCode.BadRequest);

            public Task PauseScheduleAsync(Guid scheduleId) => Task.CompletedTask;
            public Task ResumeScheduleAsync(Guid scheduleId) => Task.CompletedTask;
            public Task DeleteScheduleAsync(Guid scheduleId) => Task.CompletedTask;
            public Task TakeOverScheduleAsync(Guid scheduleId) => Task.CompletedTask;
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}
