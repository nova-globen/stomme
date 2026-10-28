using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Reports the status of a given task, which will result in <see cref="RunTask"/> steps being resumed.
/// </summary>
public interface ITaskReporter
{
    /// <summary>
    /// Triggers the <see cref="RunTask"/> step.
    /// </summary>
    Task ReportCompletionAsync(string taskId, object? result = default, CancellationToken cancellationToken = default);
}