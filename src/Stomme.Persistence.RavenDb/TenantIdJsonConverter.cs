using Stomme.Tenancy.Domain;
using Newtonsoft.Json;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Serializes <see cref="TenantId"/> as a scalar GUID rather than a nested <c>{ "Value": ... }</c>
/// object. The value object exposes only a private constructor, so without this the default
/// reflection-based deserializer cannot reconstruct it. Mirrors the EF Core value converter so a
/// tenant-owned aggregate persists identically across the document and relational stores.
/// </summary>
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override void WriteJson(JsonWriter writer, TenantId? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(value.Value);
    }

    public override TenantId? ReadJson(JsonReader reader, Type objectType, TenantId? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var raw = reader.Value;
        return raw switch
        {
            null => null,
            Guid guid => TenantId.From(guid),
            _ => TenantId.From(Guid.Parse(raw.ToString()!)),
        };
    }
}
