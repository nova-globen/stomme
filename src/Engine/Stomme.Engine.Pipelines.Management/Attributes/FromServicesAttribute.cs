namespace Stomme.Engine.Pipelines.Management.Attributes;

/// <summary>
/// Indicates that a host method parameter should be resolved from the service provider instead of from pipeline inputs.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromServicesAttribute : Attribute
{
}

