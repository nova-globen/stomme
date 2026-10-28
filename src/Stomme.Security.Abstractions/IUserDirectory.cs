namespace Stomme.Security.Abstractions;

/// <summary>A user entry for admin pickers: the id to target and a human-readable label.</summary>
public sealed record UserDirectoryEntry(Guid Id, string DisplayName);

/// <summary>
/// A read-only directory of the current tenant's users for admin pickers (e.g. per-user feature-flag
/// overrides). It lets a feature module present a user picker without referencing the Identity module — the
/// Identity module implements it over its user store. Returns an empty list when no directory is wired.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyList<UserDirectoryEntry>> ListUsersAsync(CancellationToken ct = default);
}
