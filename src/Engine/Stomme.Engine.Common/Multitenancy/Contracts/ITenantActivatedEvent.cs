namespace Stomme.Engine.Common.Multitenancy;

public interface ITenantActivatedEvent
{
    Task TenantActivatedAsync(TenantActivatedEventArgs args);
}