using System.Collections.Generic;
using System.Linq;
using Bunit;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// On an interactive page a page script is IMPORTED as an ES module, never added as a classic script.
    /// </summary>
    /// <remarks>
    /// A page script (<see cref="ResourceLoadBehavior.BlazorPageScript"/>) exports onLoad/onUpdate/onDispose; the static path's
    /// &lt;page-script&gt; imports it. The interactive path handed every script resource to <c>includeScripts</c> as a classic
    /// &lt;script&gt;, so the Landing module's page script threw "Unexpected token 'export'" on every interactive home page
    /// (found on UAT 10.5.1, and in the pipeline editor walk) and a dashboard landing page drew no chart on a WebAssembly site.
    /// </remarks>
    public sealed class PageScriptInteractiveTests : BunitContext
    {
        public PageScriptInteractiveTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
        }

        [Fact]
        public void A_page_script_is_imported_and_a_classic_script_is_still_included()
        {
            Render<PageScriptControl>(parameters => parameters
                .AddCascadingValue(Page())
                .AddCascadingValue(new ModuleModel { ModuleId = 1, SiteId = 1, PermissionList = new List<Permission>() }));

            var imported = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Stomme.Interop.includePageScript");
            Assert.StartsWith("http://localhost:5000/Modules/Test/page.js", (string)imported.Arguments.Single());

            var included = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Stomme.Interop.includeScripts");
            var hrefs = ((object[])included.Arguments.Single()).Select(script => (string)script.GetType().GetProperty("href")!.GetValue(script)!).ToList();
            Assert.StartsWith("http://localhost:5000/Modules/Test/classic.js", Assert.Single(hrefs));
        }

        [Fact]
        public void A_later_render_calls_the_page_script_again_so_content_that_arrived_with_data_is_seen()
        {
            // Driven: a dashboard landing page on a WebAssembly site drew no chart - its element arrived with the data, after
            // the first render, and nothing called the page script again. The static path's HTML arrives whole and never needed it.
            var cut = Render<PageScriptControl>(parameters => parameters
                .AddCascadingValue(Page())
                .AddCascadingValue(new ModuleModel { ModuleId = 1, SiteId = 1, PermissionList = new List<Permission>() }));
            Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Stomme.Interop.includePageScript");

            cut.Render();

            Assert.Equal(2, JSInterop.Invocations.Count(invocation => invocation.Identifier == "Stomme.Interop.includePageScript"));
            Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Stomme.Interop.includeScripts");
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost", BaseUrl = "http://localhost:5000" };

        private static PageState Page() => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "", Name = "Home", Resources = new List<Resource>() },
            User = null,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        /// <summary>A control declaring one page script and one classic script, as the Landing module's control does.</summary>
        public sealed class PageScriptControl : ModuleControlBase
        {
            public override List<Resource> Resources => new()
            {
                new Resource { ResourceType = ResourceType.Script, Url = "Modules/Test/page.js", LoadBehavior = ResourceLoadBehavior.BlazorPageScript },
                new Resource { ResourceType = ResourceType.Script, Url = "Modules/Test/classic.js" },
            };
        }

        private sealed class NoOpLogService : ILogService
        {
            public System.Threading.Tasks.Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => System.Threading.Tasks.Task.FromResult(new List<Log>());
            public System.Threading.Tasks.Task<Log> GetLogAsync(int logId) => System.Threading.Tasks.Task.FromResult<Log>(null!);
            public System.Threading.Tasks.Task DeleteLogsAsync(int siteId) => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, LogFunction function, LogLevel level, System.Exception exception, string message, params object[] args) => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, LogFunction function, LogLevel level, System.Exception exception, string message, params object[] args) => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, LogFunction function, LogLevel level, System.Exception exception, string message, params object[] args) => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
