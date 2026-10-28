using Stomme.Engine.Pipelines.LogPersistence;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management.Options;

/// <summary>
/// Options for the management module.
/// </summary>
public class ManagementOptions
{
    /// <summary>
    /// A collection of step types that are available to the system.
    /// </summary>
    public HashSet<Type> StepTypes { get; set; } = new();

    /// <summary>
    /// A collection of types that are available to the system as variable types.
    /// </summary>
    public HashSet<VariableDescriptor> VariableDescriptors { get; set; } = new(new VariableDescriptor.VariableDescriptorComparer());

    /// <summary>
    /// The format to use for compressing pipeline state.
    /// </summary>
    public string? CompressionAlgorithm { get; set; }

    /// <summary>
    /// The default Log Persistence Mode to use for all the system
    /// </summary>
    public LogPersistenceMode LogPersistenceMode { get; set; }

    /// <summary>
    /// A mode that does not allow editing pipelines.
    /// </summary>
    public bool IsReadOnlyMode { get; set; }
}