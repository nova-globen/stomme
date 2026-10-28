namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Opens the ambient tenant scope for a captured tenant id, so the outbox processor can dispatch a
/// message under the tenant it was raised in (tenant-owned handlers see the right scope). Implemented
/// by the tenancy building block; the processor treats it as optional.
/// </summary>
public interface ITenantScopeFactory
{
    /// <summary>Enters the scope for <paramref name="tenantId"/> (system scope when null) until disposed.</summary>
    IDisposable BeginScope(Guid? tenantId);
}
