using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Serializes and deserializes pipeline states.
/// </summary>
public interface IPipelineStateSerializer
{
    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version Serialize instead.")]
    Task<string> SerializeAsync(PipelineState pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    string Serialize(PipelineState pipelineState);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version SerializeToUtfBytes instead.")]
    Task<byte[]> SerializeToUtfBytesAsync(PipelineState pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    byte[] SerializeToUtfBytes(PipelineState pipelineState);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version SerializeToElement instead.")]
    Task<JsonElement> SerializeToElementAsync(PipelineState pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    JsonElement SerializeToElement(PipelineState pipelineState);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    [Obsolete("Use the non-async version Serialize instead.")]
    Task<string> SerializeAsync(object pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to serialize.</param>
    /// <returns>The serialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The serialization process may require access to the type.")]
    string Serialize(object pipelineState);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    Task<PipelineState> DeserializeAsync(string serializedState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    PipelineState Deserialize(string serializedState);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    Task<PipelineState> DeserializeAsync(JsonElement serializedState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    PipelineState Deserialize(JsonElement serializedState);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    [Obsolete("Use the non-async version Deserialize instead.")]
    Task<T> DeserializeAsync<T>(string serializedState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes the specified serialized state.
    /// </summary>
    /// <param name="serializedState">The serialized state.</param>
    /// <returns>The deserialized pipeline state.</returns>
    [RequiresUnreferencedCode("The type 'T' may be trimmed from the output. The deserialization process may require access to the type.")]
    T Deserialize<T>(string serializedState);
}