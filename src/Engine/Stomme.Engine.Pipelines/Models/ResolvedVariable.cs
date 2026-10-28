using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a variable and its value.
/// </summary>
public record ResolvedVariable(Variable Variable, object? Value);