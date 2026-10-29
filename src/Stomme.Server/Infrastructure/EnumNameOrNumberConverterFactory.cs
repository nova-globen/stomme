#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// An API enum may be sent by NAME as well as by number. The API's JSON read enums as numbers only, so a body
    /// naming the value - <c>"scope": "Resource"</c>, what a person or an agent writes - answered 400 with an unreadable
    /// body (found four times by the app-guidance writers). Reads accept a number or a name (case-insensitive); writes stay
    /// NUMBERS, so every existing client that reads them is unchanged.
    /// </summary>
    public sealed class EnumNameOrNumberConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private sealed class Converter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
        {
            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number)
                {
                    return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64());
                }

                if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } text
                    && Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var value))
                {
                    return value;
                }

                throw new JsonException($"'{(reader.TokenType == JsonTokenType.String ? reader.GetString() : reader.TokenType.ToString())}' is not a {typeof(TEnum).Name}: use one of {string.Join(", ", Enum.GetNames<TEnum>())}, or its number.");
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
                writer.WriteNumberValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
