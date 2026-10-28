using System.Reflection;

namespace Stomme.Engine.Pipelines;

public abstract class PropertyUIHandlerBase : IPropertyUIHandler
{
    public virtual float Priority => 0;
    public abstract ValueTask<IDictionary<string, object>> GetUIPropertiesAsync(PropertyInfo propertyInfo, object? context, CancellationToken cancellationToken = default);
}