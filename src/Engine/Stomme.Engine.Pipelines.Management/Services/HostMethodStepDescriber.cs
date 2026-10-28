using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management.Steps.HostMethod;
using Stomme.Engine.Pipelines.Management.Attributes;
using Stomme.Engine.Pipelines.Models;
using Humanizer;

namespace Stomme.Engine.Pipelines.Management.Services;

public class HostMethodStepDescriber(IStepDescriber stepDescriber) : IHostMethodStepDescriber
{
    public async Task<IEnumerable<StepDescriptor>> DescribeAsync(string key, Type hostType, CancellationToken cancellationToken = default)
    {
        var methods = hostType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        var descriptors = new List<StepDescriptor>(methods.Count);
        foreach (var method in methods)
        {
            var descriptor = await DescribeMethodAsync(key, hostType, method, cancellationToken).ConfigureAwait(false);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    public async Task<StepDescriptor> DescribeMethodAsync(string key, Type hostType, MethodInfo method, CancellationToken cancellationToken = default)
    {
        var descriptor = await stepDescriber.DescribeStepAsync(typeof(HostMethodStep), cancellationToken).ConfigureAwait(false);
        var stepAttribute = hostType.GetCustomAttribute<StepAttribute>() ?? method.GetCustomAttribute<StepAttribute>();

        var methodName = method.Name;
        var stepTypeName = BuildStepTypeName(key, method, stepAttribute);

        var displayAttribute = method.GetCustomAttribute<DisplayAttribute>();
        var typeDisplayName = stepAttribute?.DisplayName ?? hostType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
        var methodNameWithoutAsync = StripAsyncSuffix(methodName);
        var methodDisplayName = displayAttribute?.Name ?? methodNameWithoutAsync.Humanize().Transform(To.TitleCase);
        var displayName = !string.IsNullOrWhiteSpace(typeDisplayName) ? typeDisplayName : methodDisplayName;
        if (!string.IsNullOrWhiteSpace(stepAttribute?.DisplayName))
            displayName = stepAttribute.DisplayName!;

        descriptor.Name = methodName;
        descriptor.TypeName = stepTypeName;
        descriptor.DisplayName = displayName;
        descriptor.Description = stepAttribute?.Description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? hostType.GetCustomAttribute<DescriptionAttribute>()?.Description;
        descriptor.Category = stepAttribute?.Category ?? hostType.Name.Humanize().Transform(To.TitleCase);
        descriptor.Kind = stepAttribute?.Kind ?? StepKind.Task;
        descriptor.RunAsynchronously = stepAttribute?.RunAsynchronously ?? false;
        descriptor.IsBrowsable = true;
        descriptor.ClrType = typeof(HostMethodStep);

        descriptor.Constructor = context =>
        {
            var stepResult = context.CreateStep<HostMethodStep>();
            var step = stepResult.Step;
            step.Type = stepTypeName;
            step.HostType = hostType;
            step.MethodName = methodName;
            step.RunAsynchronously ??= descriptor.RunAsynchronously;
            return stepResult;
        };

        descriptor.Inputs.Clear();
        foreach (var prop in hostType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!IsInputProperty(prop))
                continue;

            var inputDescriptor = CreatePropertyInputDescriptor(prop);
            descriptor.Inputs.Add(inputDescriptor);
        }

        foreach (var parameter in method.GetParameters())
        {
            if (IsSpecialParameter(parameter))
                continue;

            // If FromServices is used, the parameter is not a pipeline input unless explicitly forced via [Input].
            var isFromServices = parameter.GetCustomAttribute<FromServicesAttribute>() != null;
            var isExplicitInput = parameter.GetCustomAttribute<InputAttribute>() != null;
            if (isFromServices && !isExplicitInput)
                continue;

            var inputDescriptor = CreateParameterInputDescriptor(parameter);
            descriptor.Inputs.Add(inputDescriptor);
        }

        descriptor.Outputs.Clear();
        var outputDescriptor = CreateOutputDescriptor(method);
        if (outputDescriptor != null)
            descriptor.Outputs.Add(outputDescriptor);

        return descriptor;
    }

    private string BuildStepTypeName(string key, MethodInfo method, StepAttribute? stepAttribute)
    {
        var methodName = StripAsyncSuffix(method.Name);

        if (stepAttribute != null && !string.IsNullOrWhiteSpace(stepAttribute.Namespace))
        {
            var typeSegment = stepAttribute.Type ?? methodName;
            return $"{stepAttribute.Namespace}.{typeSegment}";
        }

        return $"Stomme.Dynamic.HostMethod.{key.Pascalize()}.{methodName}";
    }

    private static string StripAsyncSuffix(string name)
    {
        return name.EndsWith("Async", StringComparison.Ordinal)
            ? name[..^5]
            : name;
    }

    private InputDescriptor CreatePropertyInputDescriptor(PropertyInfo prop)
    {
        var inputAttribute = prop.GetCustomAttribute<InputAttribute>();
        var displayNameAttribute = prop.GetCustomAttribute<DisplayNameAttribute>();
        var descriptionAttribute = prop.GetCustomAttribute<DescriptionAttribute>();

        var inputName = inputAttribute?.Name ?? prop.Name;
        var displayName = inputAttribute?.DisplayName ?? displayNameAttribute?.DisplayName ?? prop.Name.Humanize();
        var description = inputAttribute?.Description ?? descriptionAttribute?.Description;
        var nakedInputType = prop.PropertyType;

        return new()
        {
            Name = inputName,
            DisplayName = displayName,
            Description = description,
            Type = nakedInputType,
            ValueGetter = step => step.SyntheticProperties.GetValueOrDefault(inputName),
            ValueSetter = (step, value) => step.SyntheticProperties[inputName] = value!,
            IsSynthetic = true,
            IsWrapped = true,
            UIHint = inputAttribute?.UIHint ?? StepDescriber.GetUIHint(nakedInputType),
            Category = inputAttribute?.Category,
            DefaultValue = inputAttribute?.DefaultValue,
            Order = inputAttribute?.Order ?? 0,
            IsBrowsable = inputAttribute?.IsBrowsable ?? true,
            AutoEvaluate = inputAttribute?.AutoEvaluate ?? true,
            IsSerializable = inputAttribute?.IsSerializable ?? true
        };
    }

    private InputDescriptor CreateParameterInputDescriptor(ParameterInfo parameter)
    {
        var inputAttribute = parameter.GetCustomAttribute<InputAttribute>();
        var displayNameAttribute = parameter.GetCustomAttribute<DisplayNameAttribute>();

        var inputName = inputAttribute?.Name ?? parameter.Name ?? "input";
        var displayName = inputAttribute?.DisplayName ?? displayNameAttribute?.DisplayName ?? inputName.Humanize();
        var description = inputAttribute?.Description;
        var nakedInputType = parameter.ParameterType;

        return new()
        {
            Name = inputName,
            DisplayName = displayName,
            Description = description,
            Type = nakedInputType,
            ValueGetter = step => step.SyntheticProperties.GetValueOrDefault(inputName),
            ValueSetter = (step, value) => step.SyntheticProperties[inputName] = value!,
            IsSynthetic = true,
            IsWrapped = true,
            UIHint = inputAttribute?.UIHint ?? StepDescriber.GetUIHint(nakedInputType),
            Category = inputAttribute?.Category,
            DefaultValue = inputAttribute?.DefaultValue,
            Order = inputAttribute?.Order ?? 0,
            IsBrowsable = inputAttribute?.IsBrowsable ?? true,
            AutoEvaluate = inputAttribute?.AutoEvaluate ?? true,
            IsSerializable = inputAttribute?.IsSerializable ?? true
        };
    }

    private OutputDescriptor? CreateOutputDescriptor(MethodInfo method)
    {
        var returnType = method.ReturnType;

        // No output for void or Task.
        if (returnType == typeof(void) || returnType == typeof(Task))
            return null;

        // Determine the "real" return type.
        Type actualReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            actualReturnType = returnType.GetGenericArguments()[0];
        else if (typeof(Task).IsAssignableFrom(returnType))
            return null;
        else
            actualReturnType = returnType;

        var outputAttribute = method.ReturnParameter.GetCustomAttribute<OutputAttribute>() ??
                              method.GetCustomAttribute<OutputAttribute>() ??
                              method.DeclaringType?.GetCustomAttribute<OutputAttribute>();

        var displayNameAttribute = method.ReturnParameter.GetCustomAttribute<DisplayNameAttribute>();
        var outputName = outputAttribute?.Name ?? "Output";
        var displayName = outputAttribute?.DisplayName ?? displayNameAttribute?.DisplayName ?? outputName.Humanize();
        var description = outputAttribute?.Description ?? "The method output.";
        var nakedOutputType = actualReturnType;

        return new()
        {
            Name = outputName,
            DisplayName = displayName,
            Description = description,
            Type = nakedOutputType,
            IsSynthetic = true,
            ValueGetter = step => step.SyntheticProperties.GetValueOrDefault(outputName),
            ValueSetter = (step, value) => step.SyntheticProperties[outputName] = value!,
            IsBrowsable = outputAttribute?.IsBrowsable ?? true,
            IsSerializable = outputAttribute?.IsSerializable ?? true
        };
    }

    private static bool IsSpecialParameter(ParameterInfo parameter)
    {
        // These parameters are supplied by the runtime and should not become input descriptors.
        if (parameter.ParameterType == typeof(CancellationToken))
            return true;

        if (parameter.ParameterType == typeof(StepExecutionContext))
            return true;

        return false;
    }

    private static bool IsInputProperty(PropertyInfo prop)
    {
        if (!prop.CanRead || !prop.CanWrite)
            return false;

        if (prop.GetIndexParameters().Length > 0)
            return false;

        return true;
    }
}