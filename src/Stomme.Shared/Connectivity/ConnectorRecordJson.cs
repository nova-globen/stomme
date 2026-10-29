#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Stomme.Connectivity
{
    /// <summary>
    /// Serialises canonical <see cref="ConnectorRecord"/>s to the JSON-array wire shape the Datasets registrar and the
    /// pipeline steps consume: each field boxed as its
    /// JSON-native value - a long stays a number, a bool a bool, null null - and anything else as its text.
    /// </summary>
    public static class ConnectorRecordJson
    {
        public static string Serialize(IReadOnlyList<ConnectorRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            var array = new JsonArray();
            foreach (var record in records)
            {
                var row = new JsonObject();
                foreach (var (name, value) in record.Fields)
                {
                    row[name] = ToNode(value);
                }

                array.Add(row);
            }

            return array.ToJsonString();
        }

        private static JsonNode? ToNode(object? value) => value switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            long l => JsonValue.Create(l),
            int i => JsonValue.Create(i),
            short s => JsonValue.Create(s),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            decimal m => JsonValue.Create(m),
            string s => JsonValue.Create(s),
            DateTime dt => JsonValue.Create(dt.ToString("o")),
            DateTimeOffset dto => JsonValue.Create(dto.ToString("o")),
            Guid g => JsonValue.Create(g.ToString()),
            _ => JsonValue.Create(value.ToString()),
        };
    }
}
