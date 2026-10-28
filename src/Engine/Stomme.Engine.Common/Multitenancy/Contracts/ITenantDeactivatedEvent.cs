namespace Stomme.Engine.Common.Multitenancy;

public interface ITenantDeactivatedEvent
{
    Task TenantDeactivatedAsync(TenantDeactivatedEventArgs args);
}