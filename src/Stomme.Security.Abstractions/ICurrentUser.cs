namespace Stomme.Security.Abstractions;

/// <summary>
/// The ambient authenticated principal for the current operation, read by application and UI code that
/// needs to know WHO is acting. Resolved from the request's identity (the auth cookie). When no one is
/// authenticated — or there is no request at all, e.g. inside a hosted job — <see cref="IsAuthenticated"/>
/// is false, <see cref="UserId"/> is null and <see cref="Roles"/> is empty.
/// <para>
/// NARROWED on purpose (D99, BL-99): the lifted contract also carried <c>Permissions</c> and a synchronous
/// <c>HasPermission(key)</c>. Stomme issues no permission claims — its decision is per-request, per-site
/// Permission rows evaluated asynchronously through the store seam — so a synchronous answer could only be
/// a sync-over-async bridge (banned, D33) or a lie. Every permission question therefore goes through
/// <see cref="IAccessGuard.IsAllowedAsync(string?, string?, CancellationToken)"/>; a lifted
/// <c>HasPermission(key)</c> becomes <c>await accessGuard.IsAllowedAsync(null, key)</c>.
/// </para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>The authenticated user's Stomme user id, or null when anonymous.</summary>
    int? UserId { get; }

    /// <summary>The authenticated user's name, or null when anonymous.</summary>
    string? UserName { get; }

    /// <summary>True when a user is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The role names carried by the principal (case-insensitive set; empty when anonymous).</summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>The site the current request resolved to, or -1 when there is no resolved site.</summary>
    int SiteId { get; }
}
