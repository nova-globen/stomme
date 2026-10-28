using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Controls;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-083 (infra backlog, confirmed live 2026-08-08): <see cref="RichTextEditor"/>'s <c>OnAfterRenderAsync</c>
    /// called <c>ITextEditor.Initialize(Content)</c> on EVERY render, not just the first. Any render pass that
    /// includes RichTextEditor in its output re-invokes <c>Initialize</c> - and this happens whenever an
    /// ancestor re-renders and repeats the SAME markup, even with an unchanged <c>Content</c> value, because
    /// Blazor calls <c>SetParametersAsync</c> again regardless of whether the parameter value differs.
    /// <see cref="QuillJSTextEditor.Initialize"/> unconditionally resets <c>_rawhtml</c>/<c>_originalrawhtml</c>
    /// back to whatever it is handed, so a redundant re-render wiped an unsaved Raw HTML edit before Save could
    /// read it - <c>GetContent()</c>'s <c>_rawhtml != _originalrawhtml</c> check could never observe the edit,
    /// and Save persisted empty content.
    ///
    /// The fix tracks what was last actually passed to <c>Initialize</c> (both the editor instance and the
    /// content) and only calls it again on first render or when <c>Content</c> has genuinely changed, so this
    /// pins the regression while <see cref="Genuine_content_change_still_reinitializes_the_editor"/> pins the
    /// Restore/reload paths (<c>Modules/HtmlText/Edit.razor</c>) that rely on the every-render call.
    /// </summary>
    public class RichTextEditorReRenderPreservesContentTests : BunitContext
    {
        private const string OriginalContent = "<p>original</p>";
        private const string UserEditedContent = "<p>user typed this before Save</p>";

        [Fact]
        public async Task Redundant_reparent_render_preserves_unsaved_raw_edit()
        {
            var cut = RenderRichTextEditor();

            // The user edits the Raw HTML tab; the textarea's @bind commits on its change (blur) event.
            cut.Find("textarea").Change(UserEditedContent);

            // Simulate the re-render the bug report describes: an ancestor re-renders and includes
            // RichTextEditor again in its output with the SAME Content value - Blazor still calls
            // SetParametersAsync -> OnParametersSet -> OnAfterRenderAsync, even though nothing changed.
            cut.Render(parameters => parameters
                .Add(p => p.Content, OriginalContent)
                .AddUnmatched("AllowRawHtml", true)
                .AddUnmatched("AllowRichText", false));

            var content = await cut.Instance.GetHtml();

            Assert.Equal(UserEditedContent, content);
        }

        [Fact]
        public async Task Genuine_content_change_still_reinitializes_the_editor()
        {
            // Guards the Restore/reload paths (Modules/HtmlText/Edit.razor Restore() -> LoadContent()): when
            // Content genuinely changes, the editor MUST still pick up the new value, discarding whatever
            // stale unsaved edit was pending.
            var cut = RenderRichTextEditor();

            cut.Find("textarea").Change(UserEditedContent);

            const string RestoredContent = "<p>restored from an earlier version</p>";
            cut.Render(parameters => parameters
                .Add(p => p.Content, RestoredContent)
                .AddUnmatched("AllowRawHtml", true)
                .AddUnmatched("AllowRichText", false));

            var content = await cut.Instance.GetHtml();

            Assert.Equal(RestoredContent, content);
        }

        // ---- render helper -------------------------------------------------------------------------

        private IRenderedComponent<RichTextEditor> RenderRichTextEditor()
        {
            // Loose mode satisfies the CSS-theme-link and script-bundle JSInterop calls QuillJSTextEditor
            // fires from OnAfterRenderAsync regardless of AllowRichText - none of it is asserted here.
            JSInterop.Mode = JSRuntimeMode.Loose;

            // The real SettingService: its only member this path exercises is the pure, synchronous
            // GetSetting(Dictionary, string, string) lookup, so a real instance is simpler than a fake.
            Services.AddSingleton<ISettingService>(new SettingService(new HttpClient(), new SiteState()));
            Services.AddSingleton<IStringLocalizer<RichTextEditor>>(new EchoLocalizer<RichTextEditor>());
            Services.AddSingleton<IStringLocalizer<QuillJSTextEditor>>(new EchoLocalizer<QuillJSTextEditor>());
            Services.AddSingleton<IStringLocalizer<SharedResources>>(new EchoLocalizer<SharedResources>());
            Services.AddSingleton<IStringLocalizerFactory>(new EchoLocalizerFactory());
            Services.AddSingleton<ILogService>(new ThrowingLogService());
            Services.AddSingleton(new SiteState());

            // ShouldRender (ModuleBase) gates every render past the first on PageState.RenderId ==
            // ModuleState.RenderId - both must carry the SAME id or the redundant re-render below is
            // skipped outright instead of exercising the bug's actual mechanism.
            var renderId = Guid.NewGuid();
            var pageState = new PageState
            {
                Alias = new Alias { BaseUrl = "" },
                Site = new Site { Settings = new Dictionary<string, string>() },
                QueryString = new Dictionary<string, string>(),
                EditMode = false,
                RenderMode = RenderModes.Interactive,
                RenderId = renderId,
            };
            var moduleState = new Module
            {
                Settings = new Dictionary<string, string>(),
                PermissionList = new List<Permission>(),
                RenderMode = RenderModes.Interactive,
                RenderId = renderId,
            };

            return Render<RichTextEditor>(parameters => parameters
                .Add(p => p.Content, OriginalContent)
                .AddUnmatched("AllowRawHtml", true)
                .AddUnmatched("AllowRichText", false)
                .AddCascadingValue(pageState)
                .AddCascadingValue(moduleState));
        }

        // ---- minimal fakes (mirrors InstallerWizardNavigationTests.cs) -----------------------------

        private sealed class EchoLocalizer<T> : IStringLocalizer<T>
        {
            public LocalizedString this[string name] => new(name, name);
            public LocalizedString this[string name, params object[] arguments] => new(name, name);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }

        private sealed class EchoLocalizerFactory : IStringLocalizerFactory
        {
            public IStringLocalizer Create(Type resourceSource) => new EchoLocalizer<object>();
            public IStringLocalizer Create(string baseName, string location) => new EchoLocalizer<object>();
        }

        private sealed class ThrowingLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => throw NotExpected();
            public Task<Log> GetLogAsync(int logId) => throw NotExpected();
            public Task DeleteLogsAsync(int siteId) => throw NotExpected();
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();

            private static NotSupportedException NotExpected() =>
                new("Not expected on the RichTextEditor re-render path - a call here means the flow under test changed.");
        }
    }
}
