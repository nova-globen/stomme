using Stomme.Mediator;
using Stomme.Security.Abstractions;

namespace Stomme.Sdk.Smoke;

/// <summary>
/// D99/BL-99 PROOF, packages only: a module DECLARES its gates against the SDK's authorization surface -
/// <c>[Requires("resource:operation")]</c> on the mediator command, <see cref="ICurrentUser"/> for "who is
/// acting", <see cref="IAccessGuard"/> for a question the attribute cannot express (a conditional gate).
/// The module writes NO enforcement code: the host's outermost <c>AuthorizationBehavior</c> denies the
/// command before the handler runs, from the site's Permission rows.
/// </summary>
/// <remarks>
/// If <c>Stomme.Security.Abstractions</c> ever leaves the SDK meta-package, THIS FILE stops compiling in
/// <c>examples/pack-sdk.ps1</c>'s packages-only build - which is the point of putting it here rather than in
/// a project that can see the framework source.
/// </remarks>
[Requires("smoke:manage")]
public sealed record RenameTrivialAggregate(Guid Id, string Name) : IRequest<bool>;

public sealed class RenameTrivialAggregateHandler : IRequestHandler<RenameTrivialAggregate, bool>
{
    private readonly ICurrentUser _currentUser;
    private readonly IAccessGuard _accessGuard;

    public RenameTrivialAggregateHandler(ICurrentUser currentUser, IAccessGuard accessGuard)
    {
        _currentUser = currentUser;
        _accessGuard = accessGuard;
    }

    public async Task<bool> Handle(RenameTrivialAggregate request, CancellationToken ct)
    {
        // The attribute already gated this command; a second, CONDITIONAL question goes through the guard -
        // never through a synchronous HasPermission (D33: the decision is async end to end).
        if (string.IsNullOrEmpty(request.Name) && !await _accessGuard.IsAllowedAsync(null, "smoke:clear", ct))
        {
            return false;
        }

        return _currentUser.IsAuthenticated;
    }
}