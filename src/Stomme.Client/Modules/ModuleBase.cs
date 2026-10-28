using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;

namespace Stomme.Modules
{
    public abstract class ModuleBase : ComponentBase, IModuleControl
    {
        private Logger _logger;
        private string _urlparametersstate = string.Empty;
        private Dictionary<string, string> _urlparameters;
        private bool _scriptsloaded = false;

        public Logger logger => _logger ?? (_logger = new Logger(this));

        [Inject]
        protected ILogService LoggingService { get; set; }

        [Inject]
        protected IJSRuntime JSRuntime { get; set; }

        [Inject]
        protected SiteState SiteState { get; set; }

        [CascadingParameter]
        protected PageState PageState { get; set; }

        [CascadingParameter]
        protected Models.Module ModuleState { get; set; }

        [Parameter]
        public RenderModeBoundary RenderModeBoundary { get; set; }

        // optional interface properties
        public virtual SecurityAccessLevel SecurityAccessLevel { get { return SecurityAccessLevel.View; } set { } } // default security

        public virtual string Title { get { return ""; } }

        public virtual string Actions { get { return ""; } }

        public virtual bool UseAdminContainer { get { return true; } }

        public virtual List<Resource> Resources { get; set; }

        public virtual string RenderMode { get { return RenderModes.Interactive; } } // interactive by default

        public virtual bool? Prerender { get { return null; } } // allows the Site Prerender property to be overridden

        // url parameters
        public virtual string UrlParametersTemplate { get; set; }

        public Dictionary<string, string> UrlParameters {
            get
            {
                if (string.IsNullOrEmpty(_urlparametersstate) || _urlparametersstate != PageState.UrlParameters)
                {
                    _urlparametersstate = PageState.UrlParameters;
                    _urlparameters = GetUrlParameters(UrlParametersTemplate);
                }
                return _urlparameters;
            }
        }

        // base lifecycle method for handling JSInterop script registration

        // The page scripts (ResourceLoadBehavior.BlazorPageScript) this module imported at its first render (BL-202).
        private readonly List<string> _pageScripts = new List<string>();

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                List<Resource> resources = null;
                var type = GetType();
                if (type.IsSubclassOf(typeof(ModuleBase)))
                {
                    if (type.IsSubclassOf(typeof(ModuleControlBase)))
                    {
                        if (Resources != null)
                        {
                            resources = Resources.Where(item => item.ResourceType == ResourceType.Script).ToList();
                        }
                    }
                    else // ModuleBase
                    {
                        if (PageState.Page.Resources != null)
                        {
                            resources = PageState.Page.Resources.Where(item => item.ResourceType == ResourceType.Script && item.Level == ResourceLevel.Module && item.Namespace == type.Namespace).ToList();
                        }
                    }
                }
                if (resources != null && resources.Any())
                {
                    var interop = new Interop(JSRuntime);
                    var scripts = new List<object>();
                    var inline = 0;
                    foreach (Resource resource in resources)
                    {
                        if (string.IsNullOrEmpty(resource.RenderMode) || resource.RenderMode == RenderModes.Interactive)
                        {
                            if (!string.IsNullOrEmpty(resource.Url) && resource.LoadBehavior == ResourceLoadBehavior.BlazorPageScript)
                            {
                                // An ES module (BL-202): imported, never added as a classic <script>.
                                _pageScripts.Add((resource.Url.Contains("://")) ? resource.Url : PageState.Alias.BaseUrl + resource.Url);
                            }
                            else if (!string.IsNullOrEmpty(resource.Url))
                            {
                                var url = (resource.Url.Contains("://")) ? resource.Url : PageState.Alias.BaseUrl + resource.Url;
                                scripts.Add(new { href = url, type = resource.Type ?? "", bundle = resource.Bundle ?? "", integrity = resource.Integrity ?? "", crossorigin = resource.CrossOrigin ?? "", location = resource.Location.ToString().ToLower(), dataAttributes = resource.DataAttributes });
                            }
                            else
                            {
                                inline += 1;
                                await interop.IncludeScript(GetType().Namespace.ToLower() + inline.ToString(), "", "", "", resource.Type ?? "", resource.Content, resource.Location.ToString().ToLower());
                            }
                        }
                    }
                    if (scripts.Any())
                    {
                        await interop.IncludeScripts(scripts.ToArray());
                    }

                    foreach (var pageScript in _pageScripts)
                    {
                        await interop.IncludePageScript(pageScript);
                    }
                }
                _scriptsloaded = true;
            }
            else if (_pageScripts.Count > 0)
            {
                // A page script's onUpdate after every later render (BL-202): an interactive module renders its content when its
                // data arrives, after the first render - the static path's <page-script> never meets that, its HTML arrives whole.
                // Driven: a dashboard landing page on a WebAssembly site drew no chart, its element absent at the first render.
                var interop = new Interop(JSRuntime);
                foreach (var pageScript in _pageScripts)
                {
                    await interop.IncludePageScript(pageScript);
                }
            }

            if (_bringIntoView != null)
            {
                var elementId = _bringIntoView;
                _bringIntoView = null;
                await new Interop(JSRuntime).ScrollToId(elementId);
            }
        }

        // An element to scroll into view once the next render has put it on the page (BL-22's depth-2 pass): a page that
        // opens an editor or a detail section below a list opened it off-screen once the list was long, so Edit looked like
        // it did nothing - measured on Formats, its editor 427 pixels below a 900-pixel viewport.
        private string _bringIntoView;

        /// <summary>
        /// Scrolls the element with this id into view after the next render. Call it once the state that renders the element
        /// is set - at the end of an async handler, after its awaits - because a render before that has no such element.
        /// </summary>
        protected void BringIntoView(string elementId)
        {
            _bringIntoView = elementId;
        }

        protected override bool ShouldRender()
        {
            return PageState?.RenderId == ModuleState?.RenderId;
        }

        public bool ScriptsLoaded
        {
            get
            {
                return _scriptsloaded;
            }
        }

        // path methods

        public string ModulePath()
        {
            return PageState?.Alias.BaseUrl + "/Modules/" + GetType().Namespace + "/";
        }

        public string StaticAssetPath
        {
            get
            {
                // requires module to have implemented IModule
                return PageState?.Alias.BaseUrl + "_content/" + ModuleState.ModuleDefinition?.PackageName + "/";
            }
        }

        // fingerprint hash code for static assets

        public string Fingerprint
        {
            get
            {
                return ModuleState.ModuleDefinition.Fingerprint;
            }
        }

        // authorization methods

        public bool IsAuthorizedRole(string roleName)
        {
            return UserSecurity.IsAuthorized(PageState.User, roleName);
        }

        /// <summary>
        /// True when the current user holds <paramref name="permissionName"/> on this module instance.
        /// Accepts BOTH vocabularies: a plain permission name (<c>View</c>, <c>Edit</c> - the module's own
        /// rows) and a lifted <c>resource:operation</c> permission KEY (D99 point 8, BL-99).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The key form BRANCHES INSIDE this method rather than taking an overload or a new name (F14): the
        /// signature is already taken, and a second method name would leave a lifted page silently ungated
        /// whenever an author reached for the wrong one. A plain permission name can never contain ':', so
        /// the branch is unambiguous and the legacy answer is bit-for-bit what it was.
        /// </para>
        /// <para>
        /// A key is answered from the module instance's own <c>PermissionList</c>, which already carries the
        /// foreign <c>EntityId = -1</c> key rows - <c>PageModuleRepository</c> merges every 3-segment entry of
        /// the module definition's <c>PermissionNames</c> into it. So this is a pure, client-side decision in
        /// EVERY render mode: no claim (Stomme issues none), no HTTP call, no server round trip. It is a
        /// PRE-CHECK for the UI only; the server decides again per request through the same rows
        /// (<c>PermissionRowAccessGuard</c> / <c>PermissionKeyHandler</c>).
        /// </para>
        /// <para>
        /// Unlike the legacy path - which filters on <c>PermissionName</c> ALONE - the key form matches BOTH
        /// halves, because a key's operation ('view', 'manage') routinely collides with a framework
        /// permission name and with another resource's operation.
        /// </para>
        /// </remarks>
        public bool IsAuthorizedPermission(string permissionName)
        {
            if (permissionName != null && permissionName.Contains(PermissionKeys.Separator))
            {
                if (!PermissionKeys.TrySplit(permissionName, out var resource, out var operation))
                {
                    return false; // malformed key: denied, never silently re-read as a plain permission name
                }

                var rows = ModuleState.PermissionList?
                    .Where(item => string.Equals(item.EntityName, resource, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.PermissionName, operation, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return UserSecurity.IsAuthorized(PageState.User, rows);
            }

            return UserSecurity.IsAuthorized(PageState.User, permissionName, ModuleState.PermissionList);
        }

        // url methods

        // navigate url
        public string NavigateUrl()
        {
            return NavigateUrl(PageState.Page.Path);
        }

        public string NavigateUrl(string path)
        {
            return NavigateUrl(path, "");
        }

        public string NavigateUrl(bool refresh)
        {
            return NavigateUrl(PageState.Page.Path, refresh);
        }

        public string NavigateUrl(string path, string querystring)
        {
            return Utilities.NavigateUrl(PageState.Alias.Path, path, querystring);
        }

        public string NavigateUrl(string path, Dictionary<string, string> querystring)
        {
            return NavigateUrl(path, Utilities.CreateQueryString(querystring));
        }

        public string NavigateUrl(string path, bool refresh)
        {
            return NavigateUrl(path, refresh ? "refresh" : "");
        }

        public string NavigateUrl(int moduleId, string action)
        {
            return EditUrl(PageState.Page.Path, moduleId, action, "");
        }

        public string NavigateUrl(int moduleId, string action, string querystring)
        {
            return EditUrl(PageState.Page.Path, moduleId, action, querystring);
        }

        public string NavigateUrl(int moduleId, string action, Dictionary<string, string> querystring)
        {
            return EditUrl(PageState.Page.Path, moduleId, action, querystring);
        }

        public string NavigateUrl(string path, int moduleId, string action)
        {
            return EditUrl(path, moduleId, action, "");
        }

        public string NavigateUrl(string path, int moduleId, string action, string querystring)
        {
            return EditUrl(path, moduleId, action, querystring);
        }

        public string NavigateUrl(string path, int moduleId, string action, Dictionary<string, string> querystring)
        {
            return EditUrl(path, moduleId, action, querystring);
        }

        // edit url
        public string EditUrl(string action)
        {
            return EditUrl(ModuleState.ModuleId, action);
        }

        public string EditUrl(string action, string querystring)
        {
            return EditUrl(ModuleState.ModuleId, action, querystring);
        }

        public string EditUrl(string action, Dictionary<string, string> querystring)
        {
            return EditUrl(ModuleState.ModuleId, action, querystring);
        }

        public string EditUrl(int moduleId, string action)
        {
            return EditUrl(moduleId, action, "");
        }

        public string EditUrl(int moduleId, string action, string querystring)
        {
            return EditUrl(PageState.Page.Path, moduleId, action, querystring);
        }

        public string EditUrl(int moduleId, string action, Dictionary<string, string> querystring)
        {
            return EditUrl(PageState.Page.Path, moduleId, action, querystring);
        }

        public string EditUrl(string path, int moduleid, string action, string querystring)
        {
            return Utilities.EditUrl(PageState.Alias.Path, path, moduleid, action, querystring);
        }

        public string EditUrl(string path, int moduleid, string action, Dictionary<string, string> querystring)
        {
            return EditUrl(path, moduleid, action, Utilities.CreateQueryString(querystring));
        }

        // file url
        public string FileUrl(string folderpath, string filename)
        {
            return FileUrl(folderpath, filename, false);
        }

        public string FileUrl(string folderpath, string filename, bool download)
        {
            return Utilities.FileUrl(PageState.Alias, folderpath, filename, download);
        }
        public string FileUrl(int fileid)
        {
            return FileUrl(fileid, false);
        }

        public string FileUrl(int fileid, bool download)
        {
            return Utilities.FileUrl(PageState.Alias, fileid, download);
        }

        // image url

        public string ImageUrl(int fileid, int width, int height)
        {
            return ImageUrl(fileid, width, height, "");
        }

        public string ImageUrl(int fileid, int width, int height, string mode)
        {
            return ImageUrl(fileid, width, height, mode, "", "", 0, false);
        }

        public string ImageUrl(int fileid, int width, int height, string mode, string position, string background, int rotate, bool recreate)
        {
            return Utilities.ImageUrl(PageState.Alias, fileid, width, height, mode, position, background, rotate, recreate);
        }

        public string AddUrlParameters(params object[] parameters)
        {
            return Utilities.AddUrlParameters(parameters);
        }

        // template is in the form of a standard route template ie. "/{id}/{name}" and produces dictionary of key/value pairs
        // if url parameters belong to a specific module you should embed a unique key into the route (ie. /!/blog/1) and validate the url parameter key in the module
        public virtual Dictionary<string, string> GetUrlParameters(string template = "")
        {
            var urlParameters = new Dictionary<string, string>();
            var parameters = _urlparametersstate.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (string.IsNullOrEmpty(template))
            {
                // no template will populate dictionary with generic "parameter#" keys
                for (int i = 0; i < parameters.Length; i++)
                {
                    urlParameters.TryAdd("parameter" + i, parameters[i]);
                }
            }
            else
            {
                var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string key;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < segments.Length)
                    {
                        key = segments[i];
                        if (key.StartsWith("{") && key.EndsWith("}"))
                        {
                            // dynamic segment
                            key = key.Substring(1, key.Length - 2);
                        }
                        else
                        {
                            // static segments use generic "parameter#" keys
                            key = "parameter" + i.ToString();
                        }
                    }
                    else // unspecified segments use generic "parameter#" keys
                    {
                        key = "parameter" + i.ToString();
                    }
                    urlParameters.TryAdd(key, parameters[i]);
                }
            }

            return urlParameters;
        }

        // UI methods
        private static readonly string RenderModeBoundaryErrorMessage =
            "RenderModeBoundary is not available. This method requires a RenderModeBoundary parameter. " +
            "If you are using child components, ensure you pass the RenderModeBoundary property to the child component: " +
            "<ChildComponent RenderModeBoundary=\"RenderModeBoundary\" />";

        public void AddModuleMessage(string message, MessageType type)
        {
            AddModuleMessage(message, type, "top");
        }

        public void AddModuleMessage(string message, MessageType type, string position)
        {
            AddModuleMessage(message, type, position, MessageStyle.Alert);
        }

        public void AddModuleMessage(string message, MessageType type, MessageStyle style)
        {
            AddModuleMessage(message, type, "top", style);
        }

        public void AddModuleMessage(string message, MessageType type, string position, MessageStyle style)
        {
            if (RenderModeBoundary == null)
            {
                throw new InvalidOperationException(RenderModeBoundaryErrorMessage);
            }
            RenderModeBoundary.AddModuleMessage(message, type, position, style);
        }

        public void ClearModuleMessage()
        {
            if (RenderModeBoundary == null)
            {
                throw new InvalidOperationException(RenderModeBoundaryErrorMessage);
            }
            RenderModeBoundary.AddModuleMessage("", MessageType.Undefined);
        }

        public void ShowProgressIndicator()
        {
            if (RenderModeBoundary == null)
            {
                throw new InvalidOperationException(RenderModeBoundaryErrorMessage);
            }
            RenderModeBoundary.ShowProgressIndicator();
        }

        public void HideProgressIndicator()
        {
            if (RenderModeBoundary == null)
            {
                throw new InvalidOperationException(RenderModeBoundaryErrorMessage);
            }
            RenderModeBoundary.HideProgressIndicator();
        }

        public void SetModuleTitle(string title)
        {
            dynamic obj = new ExpandoObject();
            obj.PageModuleId = ModuleState.PageModuleId;
            obj.Title = title;
            SiteState.Properties.ModuleTitle = obj;
        }

        public void SetModuleVisibility(bool visible)
        {
            dynamic obj = new ExpandoObject();
            obj.PageModuleId = ModuleState.PageModuleId;
            obj.Visible = visible;
            SiteState.Properties.ModuleVisibility = obj;
        }

        public void SetPageTitle(string title)
        {
            SiteState.Properties.PageTitle = title;
        }

        // note - only supports links and meta tags - not scripts
        public void AddHeadContent(string content)
        {
            SiteState.AppendHeadContent(content);
        }

        public void AddScript(Resource resource)
        {
            resource.ResourceType = ResourceType.Script;
            if (Resources == null) Resources = new List<Resource>();
            if (!Resources.Any(item => (!string.IsNullOrEmpty(resource.Url) && item.Url == resource.Url) || (!string.IsNullOrEmpty(resource.Content) && item.Content == resource.Content)))
            {
                Resources.Add(resource);
            }
        }

        public async Task ScrollToPageTop()
        {
            var interop = new Interop(JSRuntime);
            await interop.ScrollTo(0, 0, "smooth");
        }


        // token replace methods

        public string ReplaceTokens(string content)
        {
            return ReplaceTokens(content, null);
        }

        public string ReplaceTokens(string content, object obj)
        {
            // check for null or empty content
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }
            // Using StringBuilder avoids the performance penalty of repeated string allocations
            // that occur with string.Replace or string concatenation inside loops.
            var sb = new StringBuilder();
            var cache = new Dictionary<string, string>(); // Cache to store resolved tokens
            int index = 0;

            // Loop through content to find and replace all tokens
            while (index < content.Length)
            {
                int start = content.IndexOf('[', index); // Find start of token
                if (start == -1)
                {
                    sb.Append(content, index, content.Length - index); // Append remaining content
                    break;
                }

                int end = content.IndexOf(']', start); // Find end of token
                if (end == -1)
                {
                    sb.Append(content, index, content.Length - index); // Append unmatched content
                    break;
                }

                sb.Append(content, index, start - index); // Append content before token

                string token = content.Substring(start + 1, end - start - 1); // Extract token without brackets
                string[] parts = token.Split('|', 2); // Separate default fallback if present
                string key = parts[0];
                string fallback = parts.Length == 2 ? parts[1] : null;

                if (!cache.TryGetValue(token, out string replacement)) // Check cache first
                {
                    replacement = "[" + token + "]"; // Default replacement is original token
                    string[] segments = key.Split(':');

                    if (segments.Length >= 2)
                    {
                        object current = GetTarget(segments[0], obj); // Start from root object
                        for (int i = 1; i < segments.Length && current != null; i++)
                        {
                            var type = current.GetType();
                            var prop = type.GetProperty(segments[i]);
                            current = prop?.GetValue(current);
                        }

                        if (current != null)
                        {
                            replacement = current.ToString();
                        }
                        else if (fallback != null)
                        {
                            replacement = fallback; // Use fallback if available
                        }
                    }
                    cache[token] = replacement; // Store in cache
                }

                sb.Append(replacement); // Append replacement value
                index = end + 1; // Move index past token
            }

            return sb.ToString();
        }

        // Resolve the object instance for a given object name
        // Easy to extend with additional object types
        private object GetTarget(string name, object obj)
        {
            return name switch
            {
                "ModuleState" => ModuleState,
                "PageState" => PageState,
                _ => (obj != null && obj.GetType().Name == name) ? obj : null // Fallback to obj
            };
        }

        // date conversion methods

        public DateTime? UtcToLocal(DateTime? datetime)
        {
            // Early return if input is null
            if (datetime == null || datetime.Value == DateTime.MinValue || datetime.Value == DateTime.MaxValue)
                return datetime;

            string timezoneId = null;

            if (PageState.User != null && !string.IsNullOrEmpty(PageState.User.TimeZoneId))
            {
                timezoneId = PageState.User.TimeZoneId;
            }
            else if (!string.IsNullOrEmpty(PageState.Site.TimeZoneId))
            {
                timezoneId = PageState.Site.TimeZoneId;
            }

            return Utilities.UtcAsLocalDateTime(datetime, timezoneId);
        }

        public DateTime? LocalToUtc(DateTime? datetime)
        {
            // Early return if input is null
            if (datetime == null || datetime.Value == DateTime.MinValue || datetime.Value == DateTime.MaxValue)
                return datetime;

            string timezoneId = null;

            if (PageState.User != null && !string.IsNullOrEmpty(PageState.User.TimeZoneId))
            {
                timezoneId = PageState.User.TimeZoneId;
            }
            else if (!string.IsNullOrEmpty(PageState.Site.TimeZoneId))
            {
                timezoneId = PageState.Site.TimeZoneId;
            }

            return Utilities.LocalDateAndTimeAsUtc(datetime, timezoneId);
        }

        // logging methods
        public async Task Log(Alias alias, LogLevel level, string function, Exception exception, string message, params object[] args)
        {
            LogFunction logFunction;
            if (string.IsNullOrEmpty(function))
            {
                // try to infer from page action
                function = PageState.Action;
            }
            if (!Enum.TryParse(function, out logFunction))
            {
                switch (function.ToLower())
                {
                    case "add":
                        logFunction = LogFunction.Create;
                        break;
                    case "edit":
                        logFunction = LogFunction.Update;
                        break;
                    case "delete":
                        logFunction = LogFunction.Delete;
                        break;
                    case "":
                        logFunction = LogFunction.Read;
                        break;
                    default:
                        logFunction = LogFunction.Other;
                        break;
                }
            }
            await Log(alias, level, logFunction, exception, message, args);
        }

        public async Task Log(Alias alias, LogLevel level, LogFunction function, Exception exception, string message, params object[] args)
        {
            string category = GetType().AssemblyQualifiedName;
            string feature = Utilities.GetTypeNameLastSegment(category, 1);

            await LoggingService.Log(alias, ModuleState.PageId, ModuleState.ModuleId, PageState.User?.UserId, category, feature, PageState.RemoteIPAddress, function, level, exception, message, args);
        }

        public class Logger
        {
            private readonly ModuleBase _moduleBase;

            public Logger(ModuleBase moduleBase)
            {
                _moduleBase = moduleBase;
            }

            public async Task LogTrace(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Trace, "", null, message, args);
            }

            public async Task LogTrace(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Trace, function, null, message, args);
            }

            public async Task LogTrace(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Trace, "", exception, message, args);
            }

            public async Task LogDebug(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Debug, "", null, message, args);
            }

            public async Task LogDebug(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Debug, function, null, message, args);
            }

            public async Task LogDebug(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Debug, "", exception, message, args);
            }

            public async Task LogInformation(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Information, "", null, message, args);
            }

            public async Task LogInformation(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Information, function, null, message, args);
            }

            public async Task LogInformation(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Information, "", exception, message, args);
            }

            public async Task LogWarning(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Warning, "", null, message, args);
            }

            public async Task LogWarning(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Warning, function, null, message, args);
            }

            public async Task LogWarning(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Warning, "", exception, message, args);
            }

            public async Task LogError(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Error, "", null, message, args);
            }

            public async Task LogError(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Error, function, null, message, args);
            }

            public async Task LogError(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Error, "", exception, message, args);
            }

            public async Task LogCritical(string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Critical, "", null, message, args);
            }

            public async Task LogCritical(LogFunction function, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Critical, function, null, message, args);
            }

            public async Task LogCritical(Exception exception, string message, params object[] args)
            {
                await _moduleBase.Log(null, LogLevel.Critical, "", exception, message, args);
            }
        }

        [Obsolete("ContentUrl(int fileId) is deprecated. Use FileUrl(int fileId) instead.", false)]
        public string ContentUrl(int fileid)
        {
            return ContentUrl(fileid, false);
        }

        [Obsolete("ContentUrl(int fileId, bool asAttachment) is deprecated. Use FileUrl(int fileId, bool download) instead.", false)]
        public string ContentUrl(int fileid, bool asAttachment)
        {
            return Utilities.FileUrl(PageState.Alias, fileid, asAttachment);
        }

        // Referencing ModuleInstance methods from ModuleBase is deprecated. Use the ModuleBase methods instead
        public ModuleInstance ModuleInstance { get { return new ModuleInstance(); } }
    }
}
