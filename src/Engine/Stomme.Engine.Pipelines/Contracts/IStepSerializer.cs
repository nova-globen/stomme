namespace Stomme.Engine.Pipelines;

/// <summary>
/// Serializes and deserializes steps.
/// </summary>
public interface IStepSerializer
{
    /// <summary>
    /// Serializes the specified step.
    /// </summary>
    /// <param name="step">The step to serialize.</param>
    /// <returns>The serialized step.</returns>
    string Serialize(IStep step);
    
    /// <summary>
    /// Serializes the specified value.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The serialized value.</returns>
    string Serialize(object value);
    
    /// <summary>
    /// Deserializes the specified serialized step.
    /// </summary>
    /// <param name="serializedStep">The serialized step.</param>
    /// <returns>The deserialized step.</returns>
    IStep Deserialize(string serializedStep);
    
    /// <summary>
    /// Deserializes the specified serialized value.
    /// </summary>
    /// <param name="serializedValue">The serialized value.</param>
    /// <param name="type">The type of the value to deserialize.</param>
    /// <returns>The deserialized value.</returns>
    object Deserialize(string serializedValue, Type type);
    
    /// <summary>
    /// Deserializes the specified serialized value.
    /// </summary>
    /// <param name="serializedValue">The serialized value.</param>
    /// <returns>The deserialized value.</returns>
    T Deserialize<T>(string serializedValue);
}