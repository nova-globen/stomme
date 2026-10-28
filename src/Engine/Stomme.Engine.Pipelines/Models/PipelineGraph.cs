using System.Security.Cryptography;
using System.Text;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Hold a reference to a <see cref="Pipeline"/> and a collection of <see cref="StepNode"/> instances representing the pipeline.
/// </summary>
public record PipelineGraph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineGraph"/> class.
    /// </summary>
    public PipelineGraph(Pipeline pipeline, StepNode root, IEnumerable<StepNode> nodes)
    {
        using var hashAlgorithm = SHA256.Create();
        Pipeline = pipeline;
        Root = root;
        Nodes = nodes.ToList();
        NodeIdLookup = Nodes.ToDictionary(x => x.NodeId);
        NodeHashLookup = Nodes.ToDictionary(x => Hash(hashAlgorithm, x.NodeId));
        NodeStepLookup = Nodes.ToDictionary(x => x.Step);
    }

    /// <summary>
    /// Gets the pipeline.
    /// </summary>
    public Pipeline Pipeline { get; }

    /// <summary>
    /// Gets the root node.
    /// </summary>
    public StepNode Root { get; }

    /// <summary>
    /// Gets a flat collection of all nodes in the pipeline.
    /// </summary>
    public ICollection<StepNode> Nodes { get; }

    /// <summary>
    /// Gets a lookup of nodes by their step.
    /// </summary>
    public IDictionary<IStep, StepNode> NodeStepLookup { get; }

    /// <summary>
    /// Gets a lookup of nodes by their hash.
    /// </summary>
    public IDictionary<string, StepNode> NodeHashLookup { get; }

    /// <summary>
    /// Gets a lookup of nodes by their ID.
    /// </summary>
    public IDictionary<string, StepNode> NodeIdLookup { get; }

    /// <summary>
    /// Finds the step based on the provided <paramref name="handle"/>.
    /// </summary>
    /// <param name="handle">The handle containing the identification parameters for the step.</param>
    /// <returns>The step found based on the handle, or null if no step is found.</returns>
    public IStep? FindStep(StepHandle handle)
    {
        return handle.StepId != null
            ? FindStepById(handle.StepId)
            : handle.StepNodeId != null
                ? FindStepByNodeId(handle.StepNodeId)
                : handle.StepHash != null
                    ? FindStepByHash(handle.StepHash)
                    : null;
    }

    /// <summary>
    /// Returns the <see cref="StepNode"/> with the specified step ID from the pipeline graph.
    /// </summary>
    public StepNode? FindNodeById(string nodeId) => NodeIdLookup.TryGetValue(nodeId, out var node) ? node : null;

    /// <summary>
    /// Returns the <see cref="StepNode"/> with the specified hash of the step node ID from the pipeline graph.
    /// </summary>
    /// <param name="hash">The hash of the step node ID.</param>
    /// <returns>The <see cref="StepNode"/> with the specified hash of the step node ID.</returns>
    public StepNode? FindNodeByHash(string hash) => NodeHashLookup.TryGetValue(hash, out var node) ? node : null;

    /// Returns the <see cref="StepNode"/> containing the specified step from the pipeline graph.
    public StepNode? FindNodeByStep(IStep step)
    {
        return NodeStepLookup.TryGetValue(step, out var node) ? node : null;
    }

    /// Returns the <see cref="StepNode"/> associated with the specified step ID.
    public StepNode? FindNodeByStepId(string stepId) => Nodes.FirstOrDefault(x => x.Step.Id == stepId);

    /// Returns the <see cref="IStep"/> with the specified ID from the pipeline graph.
    public IStep? FindStepByNodeId(string nodeId) => FindNodeById(nodeId)?.Step;

    /// Returns the <see cref="IStep"/> with the specified ID from the pipeline graph.
    public IStep? FindStepById(string stepId) => FindNodeById(NodeIdLookup.SingleOrDefault(n => n.Key.EndsWith(stepId)).Value.NodeId)?.Step;

    /// Returns the <see cref="IStep"/> with the specified hash of the step node ID from the pipeline graph.
    /// <param name="hash">The hash of the step node ID.</param>
    /// <returns>The <see cref="IStep"/> with the specified hash of the step node ID.</returns>
    public IStep? FindStepByHash(string hash) => FindNodeByHash(hash)?.Step;

    private static string Hash(HashAlgorithm hashAlgorithm, string input)
    {
        var data = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(data);
    }
}