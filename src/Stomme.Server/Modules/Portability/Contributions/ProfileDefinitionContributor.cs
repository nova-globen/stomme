#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Portability;
using Stomme.Repository;

namespace Stomme.Modules.Portability.Contributions
{
    /// <summary>
    /// The site's profile DEFINITIONS - the fields a user profile has, not anyone's values (BL-82, D107 item 8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Matched by name, in memory, ordinal-ignore-case, over the CURRENT site's own profiles only</b> (refutation 13):
    /// there is no normalized name column on Profile, so a store-side name comparison would answer differently per
    /// provider (the D49 divergence). Host-level profiles (<c>SiteId</c> null) are neither exported nor matched.
    /// </para>
    /// <para>
    /// <b>An import may make a field private, never public.</b> Overwriting a private field with a package's
    /// <c>isPrivate: false</c> would publish what the site chose to hide - refused at validation, before anything changes.
    /// </para>
    /// </remarks>
    public sealed class ProfileDefinitionContributor(IProfileRepository profiles) : IPortableEntityContributor
    {
        public const string Type = "profile-definition";

        public string EntityType => Type;

        public int CurrentSchemaVersion => 1;

        public IReadOnlyCollection<int> ReadableSchemaVersions => new[] { 1 };

        public async Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default)
        {
            var own = (await profiles.GetProfilesAsync(scope.SiteId)).Where(profile => profile.SiteId == scope.SiteId);
            if (scope.EntityIds is not null)
            {
                var wanted = scope.EntityIds.ToHashSet(StringComparer.Ordinal);
                own = own.Where(profile => wanted.Contains(profile.ProfileId.ToString(CultureInfo.InvariantCulture)));
            }

            return own.Select(profile => new PortableEntity(
                new PortableEntityRef(Type, profile.ProfileId.ToString(CultureInfo.InvariantCulture)),
                profile.Name ?? string.Empty,
                1,
                JsonSerializer.Serialize(Payload.From(profile)))).ToList();
        }

        public async Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            var payload = Read(entity);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
            {
                return "the profile definition has no name";
            }

            var existing = await FindAsync(context.SiteId, payload.Name);
            if (existing is not null && context.Strategy == ConflictStrategy.Overwrite && existing.IsPrivate && !payload.IsPrivate)
            {
                return $"'{existing.Name}' is private on this site; an import may make a field private, never public";
            }

            return null;
        }

        public async Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            var payload = Read(entity)!;
            var existing = await FindAsync(context.SiteId, payload.Name);
            if (existing is null)
            {
                var added = await profiles.AddProfileAsync(payload.ToProfile(new Profile { SiteId = context.SiteId }));
                return new EntityImportOutcome(EntityImportResult.Imported, added.ProfileId.ToString(CultureInfo.InvariantCulture));
            }

            switch (context.Strategy)
            {
                case ConflictStrategy.Overwrite:
                    var updated = await profiles.UpdateProfileAsync(payload.ToProfile(existing));
                    return new EntityImportOutcome(EntityImportResult.Overwritten, updated.ProfileId.ToString(CultureInfo.InvariantCulture));

                case ConflictStrategy.Rename:
                    var name = await FreeNameAsync(context.SiteId, payload.Name);
                    var renamed = await profiles.AddProfileAsync((payload with { Name = name }).ToProfile(new Profile { SiteId = context.SiteId }));
                    return new EntityImportOutcome(EntityImportResult.Renamed, renamed.ProfileId.ToString(CultureInfo.InvariantCulture), $"imported as '{name}'");

                default:
                    return new EntityImportOutcome(EntityImportResult.Skipped, existing.ProfileId.ToString(CultureInfo.InvariantCulture), "a field of that name exists");
            }
        }

        private async Task<Profile?> FindAsync(int siteId, string name) =>
            (await profiles.GetProfilesAsync(siteId))
                .FirstOrDefault(profile => profile.SiteId == siteId && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

        private async Task<string> FreeNameAsync(int siteId, string name)
        {
            var taken = (await profiles.GetProfilesAsync(siteId)).Where(profile => profile.SiteId == siteId)
                .Select(profile => profile.Name ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var n = 2; ; n++)
            {
                var candidate = $"{name}{n}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static Payload? Read(PortableEntity entity)
        {
            try
            {
                return JsonSerializer.Deserialize<Payload>(entity.PayloadJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The allowlist that travels - every definition field, no id, no site.</summary>
        public sealed record Payload(
            string Name, string? Title, string? Description, string? Category, int ViewOrder, int MaxLength, string? DefaultValue,
            bool IsRequired, bool IsPrivate, string? Options, string? Validation, int Rows, string? Autocomplete)
        {
            public static Payload From(Profile profile) => new(
                profile.Name, profile.Title, profile.Description, profile.Category, profile.ViewOrder, profile.MaxLength, profile.DefaultValue,
                profile.IsRequired, profile.IsPrivate, profile.Options, profile.Validation, profile.Rows, profile.Autocomplete);

            public Profile ToProfile(Profile target)
            {
                target.Name = Name;
                target.Title = Title ?? string.Empty;
                target.Description = Description ?? string.Empty;
                target.Category = Category ?? string.Empty;
                target.ViewOrder = ViewOrder;
                target.MaxLength = MaxLength;
                target.DefaultValue = DefaultValue ?? string.Empty;
                target.IsRequired = IsRequired;
                target.IsPrivate = IsPrivate; // never private -> public: ValidateImportAsync refuses that before any import runs
                target.Options = Options ?? string.Empty;
                target.Validation = Validation ?? string.Empty;
                target.Rows = Rows;
                target.Autocomplete = Autocomplete ?? string.Empty;
                return target;
            }
        }
    }
}
