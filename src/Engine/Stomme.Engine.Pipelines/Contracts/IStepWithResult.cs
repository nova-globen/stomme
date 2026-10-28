using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Contract for custom steps that return a result.
/// </summary>
/// <typeparam name="T">The type of the result.</typeparam>
public interface IStepWithResult<T> : IStepWithResult
{
    /// <summary>
    /// The result of the step.
    /// </summary>
    new Output<T>? Result { get; set; }
}

/// <summary>
/// Contract for custom steps that return a result.
/// </summary>
public interface IStepWithResult
{
    /// <summary>
    /// The result of the step.
    /// </summary>
    Output? Result { get; set; }
}