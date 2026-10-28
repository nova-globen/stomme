namespace Stomme.Engine.Pipelines;

/// <summary>
/// Generates a unique identifier using <see cref="ShortGuid"/>.
/// </summary>
public class ShortGuidIdentityGenerator : IIdentityGenerator
{
    /// <inheritdoc />
    public string GenerateId() => ShortGuid.Encode(Guid.NewGuid());
}
