namespace Stomme.Persistence.RavenDb.Migrations;

/// <summary>History entry for a data-patch migration: when it ran and whether it succeeded.</summary>
public sealed class RavenMigrationRecord
{
    public string Id { get; set; } = default!;

    public DateTimeOffset AppliedUtc { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }
}
