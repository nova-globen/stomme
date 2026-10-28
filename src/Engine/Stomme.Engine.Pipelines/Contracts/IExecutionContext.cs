using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides a common interface to access the current execution context.
/// </summary>
public interface IExecutionContext
{
    /// <summary>
    /// The unique ID of this execution context.
    /// </summary>
    string Id { get; }
    
    /// <summary>
    /// The step that is currently being executed.
    /// </summary>
    IStep Step { get; }
    
    /// <summary>
    /// The expression execution context.
    /// </summary>
    ExpressionExecutionContext ExpressionExecutionContext { get; }
    
    /// <summary>
    /// Returns variables declared in the current execution context.
    /// </summary>
    IEnumerable<Variable> Variables { get; }

    /// <summary>
    /// A dictionary of values that can be associated with this step execution context.
    /// </summary>
    public IDictionary<string, object> Properties { get; }
}