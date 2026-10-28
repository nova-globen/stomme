namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StimulusHasher(IHasher hasher) : IStimulusHasher
{
    /// <inheritdoc />
    public string Hash(string stimulusName, object? payload = null, string? stepInstanceId = null)
    {
        return hasher.Hash(stimulusName, payload, stepInstanceId);
    }
}