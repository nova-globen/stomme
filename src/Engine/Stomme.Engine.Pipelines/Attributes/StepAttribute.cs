namespace Stomme.Engine.Pipelines.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method)]
public class StepAttribute : Attribute
{
    public StepAttribute()
    {
        // Default constructor.
    }
    
    public StepAttribute(string @namespace, string? category, string? description = null)
    {
        Namespace = @namespace;
        Description = description;
        Category = category;
    }

    public StepAttribute(string @namespace, string? description = null)
    {
        Namespace = @namespace;
        Description = description;
        Category = @namespace;
    }

    public StepAttribute(string @namespace, string? type, int version = 1, string? description = null, string? category = null)
    {
        Namespace = @namespace;
        Type = type;
        Version = version;  
        Description = description;
        Category = category;
    }

    public string? Namespace { get; set; }
    public string? Type { get; set; }
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public string? DisplayName { get; set; }
    public string? Category { get; set; }
    public StepKind Kind { get; set; } = StepKind.Action;
    public bool RunAsynchronously { get; set; }
}