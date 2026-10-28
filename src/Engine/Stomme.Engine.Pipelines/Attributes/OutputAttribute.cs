namespace Stomme.Engine.Pipelines.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.ReturnValue)]
public class OutputAttribute : Attribute
{
    /// <summary>
    /// The technical name of the step property.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// The user-friendly name of the step property.
    /// </summary>
    public string? DisplayName { get; set; }
        
    /// <summary>
    /// A brief description about this property for pipeline tooling to use when displaying step editors.
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// A value indicating whether this property should be visible.
    /// </summary>
    public bool IsBrowsable { get; set; } = true;

    /// <summary>
    /// A value indicating whether this output can be serialized as part of the pipeline instance,
    /// </summary>
    public bool IsSerializable { get; set; } = true;
}