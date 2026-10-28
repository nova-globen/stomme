namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a step in the context of an hierarchical tree structure, providing access to its siblings, parents and children.
/// </summary>
public class StepNode
{
    private readonly List<StepNode> _parents = new();
    private readonly List<StepNode> _children = new();
    private string? _nodeId;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepNode"/> class.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="port">The port to which the step belongs.</param>
    public StepNode(IStep step, string port)
    {
        Step = step;
        Port = port;
    }

    /// <summary>
    /// Gets the node ID.
    /// </summary>
    public string NodeId
    {
        get
        {
            if (_nodeId == null)
            {
                var ancestorIds = Ancestors().Reverse().Select(x => x.Step.Id).ToList();
                _nodeId = ancestorIds.Any() ? $"{string.Join(":", ancestorIds)}:{Step.Id}" : Step.Id;
            }

            return _nodeId;
        }
    }

    /// <summary>
    /// Gets the step.
    /// </summary>
    public IStep Step { get; }

    /// <summary>
    /// Gets the port to which the step belongs.
    /// </summary>
    public string Port { get; }

    /// <summary>
    /// Gets the parents of this node.
    /// </summary>
    public IReadOnlyCollection<StepNode> Parents => _parents.AsReadOnly();

    /// <summary>
    /// Gets the children of this node.
    /// </summary>
    public ICollection<StepNode> Children => _children.AsReadOnly();

    public void AddParent(StepNode parent)
    {
        _parents.Add(parent);
        _nodeId = null;
    }
    
    public void AddChild(StepNode child)
    {
        _children.Add(child);
    }

    /// <summary>
    /// Gets the descendants of this node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<StepNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;

            var descendants = child.Descendants();

            foreach (var descendant in descendants)
                yield return descendant;
        }
    }

    /// <summary>
    /// Gets the ancestors of this node.
    /// </summary>
    public IEnumerable<StepNode> Ancestors()
    {
        foreach (var parent in Parents)
        {
            yield return parent;

            var ancestors = parent.Ancestors();

            foreach (var ancestor in ancestors)
                yield return ancestor;
        }
    }

    /// <summary>
    /// Gets the siblings of this node.
    /// </summary>
    public IEnumerable<StepNode> Siblings() => Parents.SelectMany(parent => parent.Children);

    /// <summary>
    /// Gets the siblings and cousins of this node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<StepNode> SiblingsAndCousins() => Parents.SelectMany(parent => parent.Descendants());
}