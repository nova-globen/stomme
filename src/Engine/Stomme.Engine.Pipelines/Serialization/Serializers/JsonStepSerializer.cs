using System.Text.Json;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Pipelines.Serialization.Converters;

namespace Stomme.Engine.Pipelines.Serialization.Serializers;

/// <inheritdoc cref="IStepSerializer" />
public class JsonStepSerializer(IServiceProvider serviceProvider) : ConfigurableSerializer(serviceProvider), IStepSerializer
{
    /// <inheritdoc />
    public string Serialize(IStep step)
    {
        var options = GetOptionsInternal();
        return JsonSerializer.Serialize(step, step.GetType(), options);
    }

    /// <inheritdoc />
    public string Serialize(object value)
    {
        var options = GetOptionsInternal();
        return JsonSerializer.Serialize(value, options);
    }

    /// <inheritdoc />
    public IStep Deserialize(string serializedStep) => JsonSerializer.Deserialize<IStep>(serializedStep, GetOptions())!;

    /// <inheritdoc />
    public object Deserialize(string serializedValue, Type type) => JsonSerializer.Deserialize(serializedValue, type, GetOptions())!;

    /// <inheritdoc />
    public T Deserialize<T>(string serializedValue) => JsonSerializer.Deserialize<T>(serializedValue, GetOptions())!;

    /// <inheritdoc />
    protected override void AddConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(CreateInstance<TypeJsonConverter>());
        options.Converters.Add(CreateInstance<InputJsonConverterFactory>());
        options.Converters.Add(CreateInstance<OutputJsonConverterFactory>());
        options.Converters.Add(CreateInstance<ExpressionJsonConverterFactory>());
        options.Converters.Add(CreateInstance<FuncExpressionValueConverter>());
    }
}
