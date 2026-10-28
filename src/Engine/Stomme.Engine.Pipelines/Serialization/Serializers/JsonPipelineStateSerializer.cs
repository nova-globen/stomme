using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Pipelines.Serialization.Converters;
using Stomme.Engine.Pipelines.Serialization.ReferenceHandlers;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Serialization.Serializers;

/// <summary>
/// Serializes and deserializes pipeline states from and to JSON.
/// </summary>
public class JsonPipelineStateSerializer : ConfigurableSerializer, IPipelineStateSerializer
{
    private readonly IWellKnownTypeRegistry _wellKnownTypeRegistry;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonPipelineStateSerializer"/> class.
    /// </summary>
    public JsonPipelineStateSerializer(IServiceProvider serviceProvider, IWellKnownTypeRegistry wellKnownTypeRegistry, ILoggerFactory loggerFactory)
        : base(serviceProvider)
    {
        _wellKnownTypeRegistry = wellKnownTypeRegistry;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version Serialize instead.")]
    public Task<string> SerializeAsync(PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Serialize(pipelineState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version SerializeToUtfBytes instead.")]
    public Task<byte[]> SerializeToUtfBytesAsync(PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SerializeToUtfBytes(pipelineState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version SerializeToElement instead.")]
    public Task<JsonElement> SerializeToElementAsync(PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SerializeToElement(pipelineState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version Serialize instead.")]
    public Task<string> SerializeAsync(object pipelineState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Serialize(pipelineState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    public Task<PipelineState> DeserializeAsync(string serializedState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Deserialize(serializedState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    public Task<PipelineState> DeserializeAsync(JsonElement serializedState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Deserialize(serializedState));
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    public Task<T> DeserializeAsync<T>(string serializedState, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Deserialize<T>(serializedState));
    }

    public string Serialize(PipelineState pipelineState)
    {
        var options = GetOptions();
        return JsonSerializer.Serialize(pipelineState, options);
    }

    public byte[] SerializeToUtfBytes(PipelineState pipelineState)
    {
        var options = GetOptions();
        return JsonSerializer.SerializeToUtf8Bytes(pipelineState, options);
    }

    public JsonElement SerializeToElement(PipelineState pipelineState)
    {
        var options = GetOptions();
        return JsonSerializer.SerializeToElement(pipelineState, options);
    }

    public string Serialize(object pipelineState)
    {
        var options = GetOptions();
        return JsonSerializer.Serialize(pipelineState, pipelineState.GetType(), options);
    }

    public PipelineState Deserialize(string serializedState)
    {
        var options = GetOptions();
        return JsonSerializer.Deserialize<PipelineState>(serializedState, options)!;
    }

    public PipelineState Deserialize(JsonElement serializedState)
    {
        var options = GetOptions();
        return serializedState.Deserialize<PipelineState>(options)!;
    }

    public T Deserialize<T>(string serializedState)
    {
        var options = GetOptions();
        return JsonSerializer.Deserialize<T>(serializedState, options)!;
    }

    /// <inheritdoc />
    public override JsonSerializerOptions GetOptions()
    {
        var options = base.GetOptions();
        return new(options) { ReferenceHandler = new CrossScopedReferenceHandler() };
    }

    /// <inheritdoc />
    protected override void AddConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(new TypeJsonConverter(_wellKnownTypeRegistry));
        options.Converters.Add(new PolymorphicObjectConverterFactory(_wellKnownTypeRegistry));
        options.Converters.Add(new VariableConverterFactory(_wellKnownTypeRegistry, _loggerFactory));
        options.Converters.Add(new FuncExpressionValueConverter());
    }
}
