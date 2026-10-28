using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides context for storage drivers.
/// </summary>
public record StorageDriverContext(IExecutionContext ExecutionContext, Variable Variable, CancellationToken CancellationToken);