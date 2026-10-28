using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Multitenancy;

public interface ITenantScope
{
    public IServiceScope ServiceScope { get; }
    IServiceProvider ServiceProvider { get; }
}