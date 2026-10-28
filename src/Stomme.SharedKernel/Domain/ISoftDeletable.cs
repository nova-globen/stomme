namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Interface for entities that support soft deletion.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }
    string? DeletedBy { get; }
}
