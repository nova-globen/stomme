using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// Creates instances of <see cref="StepJsonConverter"/>.
/// </summary>
public class StepJsonConverterFactory : JsonConverterFactory
{
    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc />
    public StepJsonConverterFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // Notice that this factory only creates converters when the type to convert is IStep.
    // The StepJsonConverter will create concrete step objects, which then uses regular serialization
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(IStep);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => ActivatorUtilities.CreateInstance<StepJsonConverter>(_serviceProvider);
}