using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Authorization;

/// <summary>
/// Composition of the authorization bridge (D99, BL-99): the three ports a gated context consumes, plus the
/// <see cref="AuthorizationBehavior{TRequest,TResponse}"/> that enforces <c>[Requires]</c> on every mediator
/// <c>Send</c>.
/// </summary>
/// <remarks>
/// <para>
/// ORDER IS THE CONTRACT. <c>Stomme.Mediator.Mediator.Send</c> resolves the <c>IPipelineBehavior</c>
/// registrations, REVERSES them, and wraps the handler once per behavior — so the FIRST registered behavior
/// ends up OUTERMOST. The host must therefore call this BEFORE anything else registers a behavior (before
/// <c>AddStommeAssemblies()</c>, whose module startups register the exception/logging/performance/
/// validation chain), and unconditionally: a gate that is registered only when "a lifted module exists"
/// is a gate that a later module can find missing (the spike's F4).
/// </para>
/// <para>
/// This method never calls <c>AddMediator(assemblies)</c>: that call is ADDITIVE, not idempotent (F12) — a
/// second call over the same assembly registers every handler and behavior twice. The behavior alone is
/// added here, through <c>AddMediatorBehavior</c>; <c>IMediator</c> itself is TryAdd'd so a host with no
/// module startup still has a dispatcher, and a module's own <c>AddMediator()</c> stays harmless.
/// </para>
/// <para>
/// The implementations are type parameters because they live in the host (they need <c>HttpContext</c>,
/// Stomme's <c>IUserPermissions</c> and the FeatureManagement evaluator), which this building block
/// must not reference. All three are TryAdd: a test or a headless host may pre-register its own.
/// </para>
/// </remarks>
public static class AuthorizationRegistration
{
    public static IServiceCollection AddStommeAuthorization<TAccessGuard, TCurrentUser, TFeatureEvaluator>(
        this IServiceCollection services)
        where TAccessGuard : class, IAccessGuard
        where TCurrentUser : class, ICurrentUser
        where TFeatureEvaluator : class, IFeatureEvaluator
    {
        services.TryAddScoped<IAccessGuard, TAccessGuard>();
        services.TryAddScoped<ICurrentUser, TCurrentUser>();
        services.TryAddScoped<IFeatureEvaluator, TFeatureEvaluator>();
        services.TryAddScoped<IMediator, Mediator.Mediator>();
        services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
        return services;
    }
}
