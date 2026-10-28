namespace Stomme.Security.Abstractions;

/// <summary>
/// Declares the access requirements of a page, component, API endpoint, or MCP tool: an optional feature
/// flag that must be on (<see cref="Feature"/>) and an optional permission the current user must hold
/// (<see cref="Permission"/>). When both are given, both are required (AND). Multiple attributes on one
/// target are all required. Evaluated by <see cref="IAccessGuard"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresAttribute : Attribute
{
    public RequiresAttribute()
    {
    }

    /// <summary>Convenience for the common case: require a single permission.</summary>
    public RequiresAttribute(string permission) => Permission = permission;

    /// <summary>The feature flag key that must be enabled, or null to require no feature.</summary>
    public string? Feature { get; set; }

    /// <summary>The permission key (<c>resource:operation</c>) the user must hold, or null to require none.</summary>
    public string? Permission { get; set; }
}
