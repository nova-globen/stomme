using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Threading.Tasks;

namespace Stomme.Modules.Controls
{
    public class QuillJSTextEditorInterop
    {
        private readonly IJSRuntime _jsRuntime;

        public QuillJSTextEditorInterop(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task CreateEditor(
            ElementReference quillElement,
            ElementReference toolbar,
            bool readOnly,
            string placeholder,
            string theme,
            string debugLevel)
        {
            try
            {
                await _jsRuntime.InvokeAsync<object>(
                    "Stomme.RichTextEditor.createQuill",
                    quillElement, toolbar, readOnly, placeholder, theme, debugLevel);
                return;
            }
            catch
            {
                // handle exception
            }
        }

        public ValueTask<string> GetText(ElementReference quillElement)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>(
                    "Stomme.RichTextEditor.getQuillText",
                    quillElement);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public ValueTask<string> GetHtml(ElementReference quillElement)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>(
                    "Stomme.RichTextEditor.getQuillHTML",
                    quillElement);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public ValueTask<string> GetContent(ElementReference quillElement)
        {
            try
            {
                return _jsRuntime.InvokeAsync<string>(
                    "Stomme.RichTextEditor.getQuillContent",
                    quillElement);
            }
            catch
            {
                return new ValueTask<string>(Task.FromResult(string.Empty));
            }
        }

        public Task LoadEditorContent(ElementReference quillElement, string content)
        {
            try
            {
                _jsRuntime.InvokeAsync<object>(
                    "Stomme.RichTextEditor.loadQuillContent",
                    quillElement, content);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task EnableEditor(ElementReference quillElement, bool mode)
        {
            try
            {
                _jsRuntime.InvokeAsync<object>(
                    "Stomme.RichTextEditor.enableQuillEditor", quillElement, mode);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public ValueTask<int> GetCurrentCursor(ElementReference quillElement)
        {
            try
            {
                return _jsRuntime.InvokeAsync<int>("Stomme.RichTextEditor.getCurrentCursor", quillElement);
            }
            catch
            {
                return new ValueTask<int>(Task.FromResult(0));
            }
        }

        public Task InsertImage(ElementReference quillElement, string imageUrl, string altText, int editorIndex)
        {
            try
            {
                _jsRuntime.InvokeAsync<object>(
                    "Stomme.RichTextEditor.insertQuillImage",
                    quillElement, imageUrl, altText, editorIndex);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }
    }
}
