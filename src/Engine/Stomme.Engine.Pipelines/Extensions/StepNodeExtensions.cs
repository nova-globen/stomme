using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for <see cref="StepNode"/>.
/// </summary>
public static class StepNodeExtensions
{
    /// <summary>
    /// Returns a flattened list of the specified node and all its descendants.
    /// </summary>
    public static IEnumerable<StepNode> Flatten(this StepNode root)
    {
        yield return root;

        foreach (var node in root.Children)
        {
            var children = node.Flatten();

            foreach (var child in children)
                yield return child;
        }
    }
}