using Stomme.Engine.Expressions.Models;

namespace Stomme.Engine.Expressions.Contracts;

/// <summary>
/// Provides descriptors for expression syntaxes.
/// </summary>
public interface IExpressionDescriptorProvider
{
    /// <summary>
    /// Gets the descriptors for the expression syntaxes supported by this provider.
    /// </summary>
    IEnumerable<ExpressionDescriptor> GetDescriptors();
}