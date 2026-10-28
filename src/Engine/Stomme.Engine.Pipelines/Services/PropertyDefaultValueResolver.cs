using System.Reflection;
using Stomme.Engine.Pipelines.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class PropertyDefaultValueResolver(IServiceScopeFactory scopeFactory) : IPropertyDefaultValueResolver
{
    /// <inheritdoc />
    public object? GetDefaultValue(PropertyInfo stepPropertyInfo)
    {
        var inputAttribute = stepPropertyInfo.GetCustomAttribute<InputAttribute>();

        if (inputAttribute == null)
            return null;

        if (inputAttribute.DefaultValueProvider == null)
            return inputAttribute.DefaultValue;

        var providerType = inputAttribute.DefaultValueProvider;
        using var scope = scopeFactory.CreateScope();
        var provider = (IStepPropertyDefaultValueProvider) ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, providerType);
        return provider.GetDefaultValue(stepPropertyInfo);
    }
}