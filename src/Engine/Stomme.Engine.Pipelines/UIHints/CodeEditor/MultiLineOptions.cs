using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.UIHints.CodeEditor;

/// <summary>
/// Options for the multi-line editor component.
/// </summary>
/// <param name="EditorHeight">The height of the editor.</param>
[PublicAPI]
public record MultiLineOptions(EditorHeight EditorHeight);