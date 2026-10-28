using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Services;
using Stomme.Engine.Pipelines.Serialization.Converters;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Serialization.Serializers;

/// <inheritdoc cref="ISafeSerializer" />
public class SafeSerializer : ConfigurableSerializer, ISafeSerializer
{
    /// <inheritdoc />
    public SafeSerializer(IServiceProvider serviceProvider)
        : base(serviceProvider) { }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type T may be trimmed.")]
    public ValueTask<string> SerializeAsync(object? value, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Serialize(value));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type T may be trimmed.")]
    public ValueTask<JsonElement> SerializeToElementAsync(object? value, CancellationToken cancellationToken = default)
    {
        return new(SerializeToElement(value));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type T may be trimmed.")]
    public ValueTask<T> DeserializeAsync<T>(string json, CancellationToken cancellationToken = default)
    {
        return new(Deserialize<T>(json));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type T may be trimmed.")]
    public ValueTask<T> DeserializeAsync<T>(JsonElement element, CancellationToken cancellationToken = default)
    {
        return new(Deserialize<T>(element));
    }

    public string Serialize(object? value)
    {
        var options = GetOptions();
        return JsonSerializer.Serialize(value, options);
    }

    public JsonElement SerializeToElement(object? value)
    {
        var options = GetOptions();
        return JsonSerializer.SerializeToElement(value, options);
    }

    public T Deserialize<T>(string json)
    {
        var options = GetOptions();
        return JsonSerializer.Deserialize<T>(json, options)!;
    }

    public T Deserialize<T>(JsonElement element)
    {
        var options = GetOptions();
        return element.Deserialize<T>(options)!;
    }

    /// <inheritdoc />
    protected override void AddConverters(JsonSerializerOptions options)
    {
        var expressionDescriptorRegistry = ServiceProvider.GetRequiredService<IExpressionDescriptorRegistry>();

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new TypeJsonConverter(WellKnownTypeRegistry.CreateDefault()));
        options.Converters.Add(new SafeValueConverterFactory());
        options.Converters.Add(new ExpressionJsonConverterFactory(expressionDescriptorRegistry));
        options.Converters.Add(new FuncExpressionValueConverter());
    }
}
