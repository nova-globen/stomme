using System.Reflection;

namespace Stomme.Security.Abstractions;

/// <summary>
/// Evaluates declarative <see cref="RequiresAttribute"/> access requirements — the single place pages,
/// components, API endpoints, and MCP tools consult to decide whether the current principal may use a
/// gated capability (the required feature is on AND the required permission is held).
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// True when the feature (if given) is on and the permission (if given) is held. A null/empty
    /// feature or permission imposes no requirement on that axis; null/null is always allowed.
    /// </summary>
    Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default);

    /// <summary>
    /// True when every <see cref="RequiresAttribute"/> declared on <paramref name="target"/> is
    /// satisfied. For a method, the declaring type's requirements are folded in as well, so a
    /// class-level gate and a method-level gate both apply (AND).
    /// </summary>
    Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a gated operation (a request carrying <see cref="RequiresAttribute"/>) is invoked by a
/// principal that does not satisfy its feature/permission requirements — e.g. the mediator authorization
/// behavior denying a command or query.
/// </summary>
public sealed class AccessDeniedException(string message) : Exception(message);
