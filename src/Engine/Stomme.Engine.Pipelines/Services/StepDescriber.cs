using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Humanizer;
using Container = Stomme.Engine.Pipelines.Steps.Container;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepDescriber(IPropertyDefaultValueResolver defaultValueResolver, IPropertyUIHandlerResolver propertyUIHandlerResolver) : IStepDescriber
{
    /// <inheritdoc />
    public async Task<StepDescriptor> DescribeStepAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default)
    {
        var stepAttr = stepType.GetCustomAttribute<StepAttribute>();
        var ns = stepAttr?.Namespace ?? StepTypeNameHelper.GenerateNamespace(stepType) ?? "Stomme";
        var friendlyName = GetFriendlyStepName(stepType);
        var typeName = stepAttr?.Type ?? friendlyName;
        var typeVersion = stepAttr?.Version ?? 1;
        var fullTypeName = StepTypeNameHelper.GenerateTypeName(stepType);
        var displayNameAttr = stepType.GetCustomAttribute<DisplayNameAttribute>();
        var displayName = displayNameAttr?.DisplayName ?? stepAttr?.DisplayName ?? friendlyName.Humanize(LetterCasing.Title);
        var categoryAttr = stepType.GetCustomAttribute<CategoryAttribute>();
        var category = categoryAttr?.Category ?? stepAttr?.Category ?? StepTypeNameHelper.GetCategoryFromNamespace(ns) ?? "Miscellaneous";
        var descriptionAttr = stepType.GetCustomAttribute<DescriptionAttribute>();
        var description = descriptionAttr?.Description ?? stepAttr?.Description;

        var embeddedPorts =
            from prop in stepType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            where typeof(IStep).IsAssignableFrom(prop.PropertyType)
            let portAttr = prop.GetCustomAttribute<PortAttribute>()
            let portBrowsableAttr = prop.GetCustomAttribute<BrowsableAttribute>()
            select new Port
            {
                Name = portAttr?.Name ?? prop.Name,
                DisplayName = portAttr?.DisplayName ?? portAttr?.Name ?? prop.Name,
                Type = PortType.Embedded,
                IsBrowsable = portAttr != null && (portBrowsableAttr == null || portBrowsableAttr.Browsable),
            };

        var flowNodeAttr = stepType.GetCustomAttribute<FlowNodeAttribute>();
        var flowPorts =
            flowNodeAttr
                ?.Outcomes.Select(x => new Port
                {
                    Type = PortType.Flow,
                    Name = x,
                    DisplayName = x,
                })
                .ToDictionary(x => x.Name) ?? new Dictionary<string, Port>();

        var allPorts = embeddedPorts.Concat(flowPorts.Values);
        var inputProperties = GetInputProperties(stepType).ToList();
        var outputProperties = GetOutputProperties(stepType).ToList();
        var isTrigger = stepType.IsAssignableTo(typeof(ITrigger));
        var browsableAttr = stepType.GetCustomAttribute<BrowsableAttribute>();
        var isTerminal = stepType.FindInterfaces((type, criteria) => type == typeof(ITerminalNode), null).Any();
        var isStart = stepType.FindInterfaces((type, criteria) => type == typeof(IStartNode), null).Any();
        var attributes = stepType.GetCustomAttributes(true).Cast<Attribute>().ToList();
        var outputAttribute = attributes.OfType<OutputAttribute>().FirstOrDefault();

        var descriptor = new StepDescriptor
        {
            TypeName = fullTypeName,
            ClrType = stepType,
            Namespace = ns,
            Name = typeName,
            Category = category,
            Description = description,
            Version = typeVersion,
            DisplayName = displayName,
            Kind = isTrigger ? StepKind.Trigger : stepAttr?.Kind ?? StepKind.Action,
            Ports = allPorts.ToList(),
            Inputs = (await DescribeInputPropertiesAsync(inputProperties, cancellationToken).ConfigureAwait(false)).ToList(),
            Outputs = (await DescribeOutputPropertiesAsync(outputProperties, cancellationToken).ConfigureAwait(false)).ToList(),
            IsContainer = typeof(Container).IsAssignableFrom(stepType),
            IsBrowsable = browsableAttr == null || browsableAttr.Browsable,
            IsStart = isStart,
            IsTerminal = isTerminal,
            Attributes = attributes,
            CustomProperties =
            {
                ["Type"] = stepType
            },
            Constructor = context =>
            {
                var stepResult = context.CreateStep(stepType);
                stepResult.Step.Type = fullTypeName;
                return stepResult;
            },
        };

        // If the step has a default output, set its IsSerializable property to the value of the OutputAttribute.IsSerializable property.
        var defaultOutputDescriptor = descriptor.Outputs.FirstOrDefault(x => x.Name == StepOutputRegister.DefaultOutputName);

        if (defaultOutputDescriptor != null)
        {
            var isResultSerializable = outputAttribute?.IsSerializable;
            defaultOutputDescriptor.IsSerializable = isResultSerializable;
        }

        return descriptor;
    }

    /// <inheritdoc />
    public IEnumerable<PropertyInfo> GetInputProperties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType) => stepType.GetProperties().Where(x => typeof(Input).IsAssignableFrom(x.PropertyType) || x.GetCustomAttribute<InputAttribute>() != null).DistinctBy(x => x.Name);

    /// <inheritdoc />
    public IEnumerable<PropertyInfo> GetOutputProperties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType) => stepType.GetProperties().Where(x => typeof(Output).IsAssignableFrom(x.PropertyType)).DistinctBy(x => x.Name).ToList();

    /// <inheritdoc />
    public Task<OutputDescriptor> DescribeOutputPropertyAsync(PropertyInfo propertyInfo, CancellationToken cancellationToken = default)
    {
        var outputAttribute = propertyInfo.GetCustomAttribute<OutputAttribute>();
        var descriptionAttribute = propertyInfo.GetCustomAttribute<DescriptionAttribute>();
        var typeArgs = propertyInfo.PropertyType.GenericTypeArguments;
        var wrappedPropertyType = typeArgs.Any() ? typeArgs[0] : typeof(object);

        return Task.FromResult(new OutputDescriptor((outputAttribute?.Name ?? propertyInfo.Name).Pascalize(), outputAttribute?.DisplayName ?? propertyInfo.Name.Humanize(LetterCasing.Title), wrappedPropertyType, propertyInfo.GetValue, propertyInfo.SetValue, propertyInfo, descriptionAttribute?.Description ?? outputAttribute?.Description, outputAttribute?.IsBrowsable ?? true, outputAttribute?.IsSerializable));
    }

    /// <inheritdoc />
    public async Task<InputDescriptor> DescribeInputPropertyAsync(PropertyInfo propertyInfo, CancellationToken cancellationToken = default)
    {
        var inputAttribute = propertyInfo.GetCustomAttribute<InputAttribute>();
        var descriptionAttribute = propertyInfo.GetCustomAttribute<DescriptionAttribute>();
        var propertyType = propertyInfo.PropertyType;
        var isWrappedProperty = typeof(Input).IsAssignableFrom(propertyType);
        var autoEvaluate = inputAttribute?.AutoEvaluate ?? true;
        var wrappedPropertyType = !isWrappedProperty ? propertyType : propertyInfo.PropertyType.GenericTypeArguments[0];

        if (wrappedPropertyType.IsNullableType())
            wrappedPropertyType = wrappedPropertyType.GetTypeOfNullable();

        var uiSpecification = await propertyUIHandlerResolver.GetUIPropertiesAsync(propertyInfo, null, cancellationToken).ConfigureAwait(false);

        return new(
            inputAttribute?.Name ?? propertyInfo.Name,
            wrappedPropertyType,
            propertyInfo.GetValue,
            propertyInfo.SetValue,
            isWrappedProperty,
            GetUIHint(wrappedPropertyType, inputAttribute),
            inputAttribute?.DisplayName ?? propertyInfo.Name.Humanize(LetterCasing.Title),
            descriptionAttribute?.Description ?? inputAttribute?.Description,
            inputAttribute?.Category,
            inputAttribute?.Order ?? 0,
            defaultValueResolver.GetDefaultValue(propertyInfo),
            inputAttribute?.DefaultSyntax,
            inputAttribute?.IsReadOnly ?? false,
            inputAttribute?.IsBrowsable ?? true,
            inputAttribute?.IsSerializable ?? true,
            false,
            autoEvaluate,
            inputAttribute?.EvaluatorType,
            null,
            propertyInfo,
            uiSpecification
        );
    }

    /// <inheritdoc />
    public async Task<IEnumerable<InputDescriptor>> DescribeInputPropertiesAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default)
    {
        var properties = GetInputProperties(stepType);
        return await DescribeInputPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<OutputDescriptor>> DescribeOutputPropertiesAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type stepType, CancellationToken cancellationToken = default)
    {
        return await DescribeOutputPropertiesAsync(GetOutputProperties(stepType), cancellationToken).ConfigureAwait(false);
    }

    public static string GetUIHint(Type wrappedPropertyType, InputAttribute? inputAttribute = null)
    {
        if (inputAttribute?.UIHint != null)
            return inputAttribute.UIHint;

        if (wrappedPropertyType == typeof(bool) || wrappedPropertyType == typeof(bool?))
            return InputUIHints.Checkbox;

        if (wrappedPropertyType == typeof(string))
            return InputUIHints.SingleLine;

        if (typeof(IEnumerable).IsAssignableFrom(wrappedPropertyType))
            return InputUIHints.DropDown;

        if (wrappedPropertyType.IsEnum || wrappedPropertyType.IsNullableType() && wrappedPropertyType.GetTypeOfNullable().IsEnum)
            return InputUIHints.DropDown;

        if (wrappedPropertyType == typeof(Variable))
            return InputUIHints.VariablePicker;

        if (wrappedPropertyType == typeof(PipelineInput))
            return InputUIHints.InputPicker;

        if (wrappedPropertyType == typeof(Type))
            return InputUIHints.TypePicker;

        return InputUIHints.SingleLine;
    }

    private static string GetFriendlyStepName(Type t)
    {
        if (!t.IsGenericType)
            return t.Name;
        var baseName = t.Name.Substring(0, t.Name.IndexOf('`'));
        var argNames = string.Join(", ", t.GetGenericArguments().Select(a => a.Name));
        return $"{baseName}<{argNames}>";
    }

    private async Task<IEnumerable<InputDescriptor>> DescribeInputPropertiesAsync(IEnumerable<PropertyInfo> properties, CancellationToken cancellationToken = default)
    {
        return await Task.WhenAll(properties.Select(async x => await DescribeInputPropertyAsync(x, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private async Task<IEnumerable<OutputDescriptor>> DescribeOutputPropertiesAsync(IEnumerable<PropertyInfo> properties, CancellationToken cancellationToken = default)
    {
        return await Task.WhenAll(properties.Select(async x => await DescribeOutputPropertyAsync(x, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }
}
