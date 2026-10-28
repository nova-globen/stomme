using Stomme.Mediator;
using Stomme.Security.Abstractions;

namespace Stomme.Authorization;

/// <summary>
/// Enforces declarative <see cref="RequiresAttribute"/> requirements on mediator requests: before a command
/// or query reaches its handler, the <see cref="IAccessGuard"/> checks every <c>[Requires]</c> on the
/// request type (feature on AND permission held), throwing <see cref="AccessDeniedException"/> otherwise.
/// Requests with no <c>[Requires]</c> are unaffected. Registered as the outermost behavior so an
/// unauthorized caller is denied before validation, caching, or the handler run. Lives in a host-agnostic
/// building block so both the Web host and the headless CLI enforce the same gate over the same seam.
/// </summary>
public sealed class AuthorizationBehavior<TRequest, TResponse>(IAccessGuard accessGuard)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!await accessGuard.IsAllowedAsync(request.GetType(), ct))
        {
            throw new AccessDeniedException($"Access denied for '{typeof(TRequest).Name}'.");
        }

        return await next();
    }
}
