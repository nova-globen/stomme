using System.Reflection;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Pipelines.Management.Attributes;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Default parameter binding implementation for host method steps.
/// </summary>
public class DefaultHostMethodParameterValueProvider : IHostMethodParameterValueProvider
{
    public ValueTask<HostMethodParameterValueProviderResult> GetValueAsync(HostMethodParameterValueProviderContext context)
    {
        var parameter = context.Parameter;

        // Provided by runtime.
        if (parameter.ParameterType == typeof(CancellationToken))
            return ValueTask.FromResult(HostMethodParameterValueProviderResult.HandledValue(context.CancellationToken));

        if (parameter.ParameterType == typeof(StepExecutionContext))
            return ValueTask.FromResult(HostMethodParameterValueProviderResult.HandledValue(context.StepExecutionContext));

        // Resolve from DI if explicitly requested.
        if (parameter.GetCustomAttribute<FromServicesAttribute>() != null)
        {
            var service = context.ServiceProvider.GetRequiredService(parameter.ParameterType);
            return ValueTask.FromResult(HostMethodParameterValueProviderResult.HandledValue(service));
        }

        // Resolve from pipeline inputs (default).
        var inputDescriptor = context.InputDescriptors.FirstOrDefault(x => string.Equals(x.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
        if (inputDescriptor == null)
            return ValueTask.FromResult(HostMethodParameterValueProviderResult.Unhandled);

        var input = (Input?)inputDescriptor.ValueGetter(context.Step);
        var inputValue = input != null ? context.StepExecutionContext.Get(input.MemoryBlockReference()) : null;

        if (inputValue is System.Dynamic.ExpandoObject expandoObject)
            inputValue = expandoObject.ConvertTo(parameter.ParameterType);

        return ValueTask.FromResult(HostMethodParameterValueProviderResult.HandledValue(inputValue));
    }
}
