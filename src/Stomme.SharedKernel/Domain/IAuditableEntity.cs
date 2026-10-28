namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Interface for entities that track creation and modification metadata.
/// </summary>
public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; }
    string? CreatedBy { get; }
    DateTimeOffset? LastModifiedAt { get; }
    string? LastModifiedBy { get; }
}
