namespace Stomme.Security.Abstractions;

/// <summary>
/// A module contributes the permission keys (<c>resource:operation</c>) it enforces. The platform
/// unions all contributors to know the full permission vocabulary — for example to grant a built-in
/// Administrator role every permission. Register one per module:
/// <c>services.AddSingleton&lt;IPermissionContributor&gt;(new PermissionContributor(MyPermissions.All));</c>
/// </summary>
public interface IPermissionContributor
{
    IReadOnlyCollection<string> Permissions { get; }
}

/// <summary>A simple <see cref="IPermissionContributor"/> over a fixed set of keys.</summary>
public sealed class PermissionContributor(IReadOnlyCollection<string> permissions) : IPermissionContributor
{
    public IReadOnlyCollection<string> Permissions { get; } = permissions;
}
