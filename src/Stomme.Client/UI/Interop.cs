using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Stomme.UI
{
    public class Interop
    {
        private readonly IJSRuntime _jsRuntime;

        public Interop(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task SetCookie(string name, string value, int days)
        {
            await SetCookie(name, value, days, true, "Lax");
        }

        public Task SetCookie(string name, string value, int days, bool secure, string sameSite)
        {
            return SetCookie(name, value, days, secure, sameSite, "/");
        }

        /// <summary>
        /// Sets a cookie for <paramref name="path"/> - a site in a folder passes its own (<c>/didin</c>), so its cookie is not
        /// the root site's (M2 row 8).
        /// </summary>
        public Task SetCookie(string name, string value, int days, bool secure, string sameSite, string path)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.setCookie",
                    name, value, days, secure, sameSite, string.IsNullOrWhiteSpace(path) ? "/" : path);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task SetCookieString(string cookieString)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync("Stomme.Interop.setCookieString", cookieString);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<string> GetCookie(string name)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>(
                    "Stomme.Interop.getCookie",
                    name);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public Task UpdateTitle(string title)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.updateTitle",
                    title);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task IncludeMeta(string id, string attribute, string name, string content)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.includeMeta",
                    id, attribute, name, content);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task IncludeLink(string id, string rel, string href, string type, string integrity, string crossorigin, string includebefore)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.includeLink",
                    id, rel, href, type, integrity, crossorigin, includebefore);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task IncludeLinks(object[] links)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.includeLinks",
                    (object)links);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        // external scripts need to specify src, inline scripts need to specify id and content
        public Task IncludeScript(string id, string src, string integrity, string crossorigin, string content, string location)
        {
            return IncludeScript(id, src, integrity, crossorigin, "", content, location);
        }

        public Task IncludeScript(string id, string src, string integrity, string crossorigin, string type, string content, string location)
        {
            return IncludeScript(id, src, integrity, crossorigin, type, content, location, null);
        }

        public Task IncludeScript(string id, string src, string integrity, string crossorigin, string type, string content, string location, Dictionary<string, string> dataAttributes)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.includeScript",
                    id, src, integrity, crossorigin, type, content, location, dataAttributes);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Imports a page script (<c>ResourceLoadBehavior.BlazorPageScript</c>) as an ES module and calls its <c>onLoad</c> (first
        /// time) and <c>onUpdate</c> - the interactive twin of the static path's <c>&lt;page-script&gt;</c>.
        /// </summary>
        public async Task IncludePageScript(string src)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("Stomme.Interop.includePageScript", src);
            }
            catch
            {
                // ignore exception, as IncludeScripts does
            }
        }

        public async Task IncludeScripts(object[] scripts)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.includeScripts",
                    (object)scripts);
            }
            catch
            {
                // ignore exception
            }
        }

        public Task RemoveElementsById(string prefix, string first, string last)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.removeElementsById",
                    prefix, first, last);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<string> GetElementByName(string name)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>(
                    "Stomme.Interop.getElementByName",
                    name);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public Task SubmitForm(string path, object fields)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.submitForm",
                    path, fields);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<string[]> GetFiles(string id)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string[]>(
                    "Stomme.Interop.getFiles",
                    id);
            }
            catch
            {
                return new ValueTask<string[]>(Task.FromResult(new string[0]));
            }
        }

        public Task UploadFiles(string posturl, string folder, string id, string antiforgerytoken, string jwt)
        {
            UploadFiles(posturl, folder, id, antiforgerytoken, jwt, 1, false);
            return Task.CompletedTask;
        }

        public ValueTask<bool> UploadFiles(string posturl, string folder, string id, string antiforgerytoken, string jwt, int chunksize, bool anonymizeuploadfilenames, CancellationToken cancellationToken = default)
        {
            try
            {
                return _jsRuntime.InvokeAsync<bool>(
                    "Stomme.Interop.uploadFiles", cancellationToken,
                    posturl, folder, id, antiforgerytoken, jwt, chunksize, anonymizeuploadfilenames);
            }
            catch
            {
                return new ValueTask<bool>(Task.FromResult(false));
            }
        }

        public Task RefreshBrowser(bool force, int wait)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.refreshBrowser",
                    force, wait);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task RedirectBrowser(string url, int wait)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.redirectBrowser",
                    url, wait);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<bool> FormValid(ElementReference form)
        {
            try
            {
                return _jsRuntime.InvokeAsync<bool>(
                    "Stomme.Interop.formValid",
                    form);
            }
            catch
            {
                return new ValueTask<bool>(Task.FromResult(false));
            }
        }

        public Task SetElementAttribute(string id, string attribute, string value)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.setElementAttribute",
                    id, attribute, value);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task ScrollTo(int top, int left, string behavior)
        {
            try
            {
                if (string.IsNullOrEmpty(behavior)) behavior = "smooth";
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.scrollTo",
                    top, left, behavior);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task ScrollToId(string id)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.scrollToId",
                    id);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<int> GetCaretPosition(string id)
        {
            try
            {
                return _jsRuntime.InvokeAsync<int>(
                    "Stomme.Interop.getCaretPosition",
                    id);
            }
            catch
            {
                return new ValueTask<int>(-1);
            }
        }

        public Task SetIndexedDBItem(string key, object value)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.manageIndexedDBItems",
                    "put", key, value);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public async Task<T> GetIndexedDBItem<T>(string key)
        {
            try
            {
                return await _jsRuntime.InvokeAsync<T>(
                    "Stomme.Interop.manageIndexedDBItems",
                    "get", key, null);
            }
            catch
            {
                return default(T);
            }
        }

        public async Task<List<string>> GetIndexedDBKeys()
        {
            return await GetIndexedDBKeys("");
        }

        public async Task<List<string>> GetIndexedDBKeys(string contains)
        {
            try
            {
                var items = await _jsRuntime.InvokeAsync<JsonDocument>(
                    "Stomme.Interop.manageIndexedDBItems",
                    "getallkeys", null, null);
                if (!string.IsNullOrEmpty(contains))
                {
                    return items.Deserialize<List<string>>()
                        .Where(item => item.Contains(contains)).ToList();
                }
                else
                {
                    return items.Deserialize<List<string>>();
                }
            }
            catch
            {
                return new List<string>();
            }
        }

        public Task RemoveIndexedDBItem(string key)
        {
            try
            {
                _jsRuntime.InvokeVoidAsync(
                    "Stomme.Interop.manageIndexedDBItems",
                    "delete", key, null);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<string> CreateCredential(string optionsResponse)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>("Stomme.Interop.createCredential", optionsResponse);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public ValueTask<string> RequestCredential(string optionsResponse)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>("Stomme.Interop.requestCredential", optionsResponse);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

    }
}
