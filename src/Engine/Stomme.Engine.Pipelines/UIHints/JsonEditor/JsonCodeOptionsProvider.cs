using System.Reflection;
using Stomme.Engine.Pipelines.UIHints.CodeEditor;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Pipelines.UIHints.JsonEditor;

internal class JsonCodeOptionsProvider : CodeEditorOptionsProviderBase
{
    protected override string GetLanguage(PropertyInfo propertyInfo, object? context) => "json";
}