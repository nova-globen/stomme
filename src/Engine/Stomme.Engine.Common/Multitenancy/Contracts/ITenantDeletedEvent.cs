namespace Stomme.Engine.Common.Multitenancy;

public interface ITenantDeletedEvent
{
    Task TenantDeletedAsync(TenantDeletedEventArgs args);
}