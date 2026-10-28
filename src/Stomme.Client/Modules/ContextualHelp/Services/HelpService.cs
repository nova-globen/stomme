using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Models.Help;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.ContextualHelp.Services
{
    /// <summary>
    /// The client/server contract of contextual help's UI (BL-84, D103 item 1). Controls depend on this interface, never
    /// on a repository: the browser binds it to <see cref="HelpService"/> (HTTP to <c>api/Help</c>, decided by the
    /// action's <c>[RequiresPermissionKey]</c> AND the request's <c>[Requires]</c>), the server to a mediator dispatch.
    /// </summary>
    /// <remarks>
    /// No method takes a site, a tenant or a user - all three are resolved server-side. A refused or failed call answers
    /// null, an empty list or <see cref="Guid.Empty"/>, and the controls pre-check with
    /// <c>ModuleBase.IsAuthorizedPermission</c> so nobody is offered an action the server would refuse. <b>No Markdig
    /// type is referenced from this project</b>: a reader receives server-rendered <c>SafeHtml</c>, and the editor's
    /// preview is rendered by the server too.
    /// </remarks>
    public interface IHelpService
    {
        /// <summary>The published topic for an anchor, through the server's locale chain - or null.</summary>
        Task<HelpTopicView> GetTopicAsync(string anchor, string locale = null);

        Task<List<HelpTopicSummary>> SearchAsync(string query);

        /// <summary>Every topic of this site, drafts included (<c>help:manage</c>).</summary>
        Task<List<HelpTopicSummary>> GetTopicsAsync();

        Task<List<HelpConflictView>> GetConflictsAsync();

        Task<HelpTopicEditView> GetTopicForEditAsync(Guid id);

        /// <summary>The saved topic's id, or <see cref="Guid.Empty"/> when the server refused the input.</summary>
        Task<Guid> SaveTopicAsync(HelpTopicSaveInput input);

        Task SetPublishedAsync(Guid id, bool published);

        /// <summary>Unsaved Markdown rendered by the SERVER, exactly as a reader would receive it - or null when refused.</summary>
        Task<string> PreviewAsync(string markdown);

        Task DeleteTopicAsync(Guid id);

        Task ResetTopicAsync(Guid id);
    }

    /// <summary>The BROWSER side: HTTP to the module's controller. The server decides every call again.</summary>
    public class HelpService : ServiceBase, IHelpService, IClientService
    {
        public HelpService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Help");

        /// <summary>
        /// Set once the server has said help is OFF on this install. The launcher asks for every anchor on every page
        /// view, and in a WebAssembly runtime each ask is an HTTP request - so without this a stock install (help off)
        /// would pay one 404 per anchor per page, forever. "Off" is a 404 from the router, which is distinct from "no
        /// topic" (204). An INSTANCE field, not a static: in a WebAssembly runtime a scoped service already lives as long
        /// as the app, so a static would add nothing but process-wide state. A tab left open while an operator switches
        /// help on (a server restart) shows no launcher until it is reloaded - a small cost against a request per anchor
        /// per page.
        /// </summary>
        private volatile bool _helpIsOff;

        public async Task<HelpTopicView> GetTopicAsync(string anchor, string locale = null)
        {
            if (_helpIsOff)
            {
                return null;
            }

            var uri = $"{ApiUrl}/topic?anchor={Uri.EscapeDataString(anchor ?? string.Empty)}" +
                      (string.IsNullOrEmpty(locale) ? string.Empty : $"&locale={Uri.EscapeDataString(locale)}");
            using var response = await GetHttpClient().GetAsync(uri);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _helpIsOff = true;
                return null;
            }

            return response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent
                ? await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<HelpTopicView>(response.Content)
                : null;
        }

        public async Task<List<HelpTopicSummary>> SearchAsync(string query) =>
            await GetJsonAsync<List<HelpTopicSummary>>($"{ApiUrl}/search?q={Uri.EscapeDataString(query ?? string.Empty)}") ?? new List<HelpTopicSummary>();

        public async Task<List<HelpTopicSummary>> GetTopicsAsync() =>
            await GetJsonAsync<List<HelpTopicSummary>>(ApiUrl) ?? new List<HelpTopicSummary>();

        public async Task<List<HelpConflictView>> GetConflictsAsync() =>
            await GetJsonAsync<List<HelpConflictView>>($"{ApiUrl}/conflicts") ?? new List<HelpConflictView>();

        public Task<HelpTopicEditView> GetTopicForEditAsync(Guid id) =>
            GetJsonAsync<HelpTopicEditView>($"{ApiUrl}/{id}");

        public Task<Guid> SaveTopicAsync(HelpTopicSaveInput input) =>
            PostJsonAsync<HelpTopicSaveInput, Guid>(ApiUrl, input);

        public Task SetPublishedAsync(Guid id, bool published) =>
            PostAsync($"{ApiUrl}/{id}/publish?published={(published ? "true" : "false")}");

        public async Task<string> PreviewAsync(string markdown) =>
            (await PostJsonAsync<HelpPreviewInput, HelpPreviewResult>($"{ApiUrl}/preview", new HelpPreviewInput(markdown ?? string.Empty)))?.SafeHtml;

        public Task DeleteTopicAsync(Guid id) =>
            DeleteAsync($"{ApiUrl}/{id}");

        public Task ResetTopicAsync(Guid id) =>
            PostAsync($"{ApiUrl}/{id}/reset");
    }
}
