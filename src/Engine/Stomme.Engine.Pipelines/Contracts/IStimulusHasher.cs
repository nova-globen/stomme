namespace Stomme.Engine.Pipelines;

/// <summary>
/// Computes a hash for a given step type name and bookmark payload.
/// </summary>
public interface IStimulusHasher
{
    /// <summary>
    /// Produces a hash from the specified step type name, payload and step instance ID.
    /// </summary>
    string Hash(string stimulusName, object? payload = null, string? stepInstanceId = null);
}