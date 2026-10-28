#nullable enable
using System;
using System.Threading;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// One ambient tenant override. A null <see cref="TenantId"/> inside an *active* state means an explicit
    /// system scope — which is why this is a class held in a nullable <see cref="AsyncLocal{T}"/> rather than
    /// a bare <c>AsyncLocal&lt;TenantId&gt;</c>: the adapter must distinguish "no override, read the alias"
    /// (state is null) from "override says system scope" (state is present, tenant is null).
    /// </summary>
    internal sealed class AmbientTenantScope
    {
        internal AmbientTenantScope(TenantId? tenantId) => TenantId = tenantId;

        /// <summary>The overridden tenant, or null for an explicit system scope.</summary>
        internal TenantId? TenantId { get; }
    }

    /// <summary>
    /// Enters a tenant scope for code that has no alias to resolve one from — the feature-flag seeder walking
    /// every tenant at startup, the outbox processor dispatching a message under the tenant it was raised in,
    /// the bus handling an incoming message with a tenant header. Those callers know the tenant id but run
    /// outside a request, so <see cref="StommeTenantContext"/> has nothing to resolve; this supplies it.
    /// <para>
    /// The scope is <see cref="AsyncLocal{T}"/>, so it flows across awaits and into DI scopes created inside
    /// it, and it is restored on dispose (scopes nest). It is deliberately process-wide static state rather
    /// than a DI-scoped field: the whole point is to be readable by a <see cref="StommeTenantContext"/>
    /// resolved from a DI scope created *after* the tenant scope was entered.
    /// </para>
    /// <para>
    /// Registered as a SINGLETON: it holds no scoped dependency (only the AsyncLocal), so it is safe to
    /// resolve from the root provider — which matters, because its callers (a hosted service, a Rebus
    /// pipeline step) are singletons that would otherwise hit the very "scoped service from the root
    /// provider" failure this repo has already been bitten by once.
    /// </para>
    /// </summary>
    public sealed class TenantScopeFactory : ITenantScopeFactory
    {
        private static readonly AsyncLocal<AmbientTenantScope?> Ambient = new();

        /// <summary>The active override, or null when there is none (read the alias instead).</summary>
        internal static AmbientTenantScope? Current => Ambient.Value;

        /// <inheritdoc />
        public IDisposable BeginScope(Guid? tenantId)
        {
            var previous = Ambient.Value;
            Ambient.Value = new AmbientTenantScope(tenantId is null ? null : TenantId.From(tenantId.Value));
            return new ScopeHandle(previous);
        }

        /// <summary>Restores the enclosing scope, so scopes nest rather than clobber each other.</summary>
        private sealed class ScopeHandle : IDisposable
        {
            private readonly AmbientTenantScope? _previous;
            private bool _disposed;

            internal ScopeHandle(AmbientTenantScope? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Ambient.Value = _previous;
            }
        }
    }
}
