#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.GoogleSheets
{
    /// <summary>
    /// Google Sheets (BL-229, n8n's most used node: its Get Rows and Append Row): a spreadsheet read as records and a record
    /// written as a row, over the Sheets API (<c>https://sheets.googleapis.com/v4</c>). The OAuth2 refresh-token scheme - a
    /// Google Cloud OAuth client's id and secret and a refresh token granted the spreadsheets scope - is exchanged at Google's
    /// token endpoint (the <c>token_url</c> setting; it is there to point a test elsewhere) through the shared
    /// <see cref="OAuth2TokenProvider"/>, so the access token is cached and every call goes through the HOST's egress. A test signs
    /// in and, when the connection names a spreadsheet, reads its title.
    /// </summary>
    /// <remarks>
    /// <c>read</c>: the sheet's rows (or a range of it), the first row the headers - each later row one record, its fields named by
    /// the headers and <c>row_number</c> the sheet's own row number, n8n's Get Rows. With <c>header_row</c> false each field is
    /// named by its column's letter. <c>append</c>: <c>values</c>, a record or a list of records as JSON, each written as a row
    /// under the sheet's headers by name (a field no header names is left out, and said); a list of lists written as it is; an
    /// empty sheet gets the first record's fields as its headers first - n8n's Append Row. The spreadsheet's id is written into
    /// the path, so it is held to the characters Google issues; the sheet's name and range are escaped. A failure is thrown, so
    /// the extraction port answers it in its own words (D110).
    /// </remarks>
    public sealed class GoogleSheetsConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://sheets.googleapis.com/v4";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string ReadOperation = "read";

        public const string AppendOperation = "append";

        private static readonly Regex SpreadsheetId = new("^[A-Za-z0-9_-]{10,200}$", RegexOptions.CultureInvariant);

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlesheets";

        public string DisplayName => "Google Sheets";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://workspace.google.com/products/sheets/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/workspace/sheets/api/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("spreadsheet", "Spreadsheet", ConnectorConfigFieldType.Text, Required: false,
                Description: "A spreadsheet's id - from its address, docs.google.com/spreadsheets/d/{id}/edit - which a test reads the title of and an operation naming none uses."),
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField SpreadsheetParameter = new("spreadsheet", "Spreadsheet", ConnectorConfigFieldType.Text, Required: false,
            Description: "The spreadsheet's id, from its address. Empty: the connection's.");

        private static readonly ConnectorConfigField SheetParameter = new("sheet", "Sheet", ConnectorConfigFieldType.Text, Required: false,
            Description: "The sheet's name, as on its tab.") { Default = "Sheet1" };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ReadOperation, "Get rows", "The sheet's rows, the first row the headers: each later row one record, its fields named by the headers, row_number its row in the sheet - n8n's Get Rows.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    SpreadsheetParameter,
                    SheetParameter,
                    new ConnectorConfigField("range", "Range", ConnectorConfigFieldType.Text, Required: false, Description: "Part of the sheet, in A1 notation (A1:F100). Empty: all of it."),
                    new ConnectorConfigField("header_row", "First row is headers", ConnectorConfigFieldType.Boolean, Required: false, Description: "Off: every row a record, its fields named by column letter (A, B...).") { Default = "true" },
                }),
            },
            new ConnectorOperation(AppendOperation, "Append rows", "Writes a record, or each of a list of records, as a row at the end of the sheet, under its headers by name - n8n's Append Row; one record says where they went.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    SpreadsheetParameter,
                    SheetParameter,
                    new ConnectorConfigField("values", "Values", ConnectorConfigFieldType.Multiline, Required: true,
                        Description: "A record or a list of records as JSON - each a row under the headers by name, a field no header names left out - or a list of lists, written as they are. An empty sheet gets the first record's fields as its headers."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                if (Values.Get(context.Settings, "spreadsheet") is not { } spreadsheet)
                {
                    return ConnectionTestResult.Ok("Signed in to Google.");
                }

                var sheet = await GetJsonAsync(client, token, $"{baseUrl}/spreadsheets/{Spreadsheet(spreadsheet)}?fields=properties.title", cancellationToken);
                return ConnectionTestResult.Ok($"Signed in; the spreadsheet is '{sheet["properties"]?["title"]?.GetValue<string>()}'.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var spreadsheet = Spreadsheet(Values.Get(context.Parameters, "spreadsheet") ?? Values.Get(context.Settings, "spreadsheet")
                ?? throw new InvalidOperationException("Name the spreadsheet - on the operation or the connection."));
            var sheet = Values.GetOrDefault(context.Parameters, "sheet", "Sheet1");
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);

            if (string.Equals(context.OperationName, AppendOperation, StringComparison.OrdinalIgnoreCase))
            {
                yield return await AppendAsync(client, token, baseUrl, spreadsheet, sheet, Values.Get(context.Parameters, "values"), cancellationToken);
                yield break;
            }

            var range = Values.Get(context.Parameters, "range");
            var answer = await GetJsonAsync(client, token, $"{baseUrl}/spreadsheets/{spreadsheet}/values/{Uri.EscapeDataString(A1(sheet, range))}?majorDimension=ROWS", cancellationToken);
            var rows = (answer["values"] as JsonArray ?? new JsonArray()).Select(Cells).ToList();
            var firstRow = FirstRow(answer["range"]?.GetValue<string>());
            var firstColumn = FirstColumn(answer["range"]?.GetValue<string>());
            if (!Values.GetBool(context.Parameters, "header_row", true))
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row_number"] = (long)(firstRow + index) };
                    for (var column = 0; column < rows[index].Count; column++)
                    {
                        fields[ColumnLetter(firstColumn + column)] = rows[index][column];
                    }

                    yield return new ConnectorRecord(fields);
                }

                yield break;
            }

            if (rows.Count == 0)
            {
                yield break;
            }

            var headers = Headers(rows[0], firstColumn);
            for (var index = 1; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row_number"] = (long)(firstRow + index) };
                for (var column = 0; column < headers.Count; column++)
                {
                    fields[headers[column]] = column < rows[index].Count ? rows[index][column] : null;
                }

                yield return new ConnectorRecord(fields);
            }
        }

        // n8n's Append Row: the sheet's headers read, each record's values put under them by name, appended after the last row.
        private static async Task<ConnectorRecord> AppendAsync(HttpClient client, string token, string baseUrl, string spreadsheet, string sheet, string? values, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(values))
            {
                throw new InvalidOperationException("Give the values to append.");
            }

            JsonNode? given;
            try
            {
                given = JsonNode.Parse(values);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The values are not JSON - a record, a list of records or a list of lists.");
            }

            var records = given switch
            {
                JsonObject one => new List<JsonNode?> { one },
                JsonArray many => many.ToList(),
                _ => throw new InvalidOperationException("The values are not a record, a list of records or a list of lists."),
            };

            var rows = new JsonArray();
            var leftOut = new SortedSet<string>(StringComparer.Ordinal);
            if (records.Count > 0 && records.All(record => record is JsonArray))
            {
                foreach (var row in records)
                {
                    rows.Add(new JsonArray(((JsonArray)row!).Select(cell => (JsonNode?)JsonValue.Create(Cell(cell))).ToArray()));
                }
            }
            else
            {
                if (records.Any(record => record is not JsonObject))
                {
                    throw new InvalidOperationException("A list to append is all records or all lists, not both.");
                }

                var top = await GetJsonAsync(client, token, $"{baseUrl}/spreadsheets/{spreadsheet}/values/{Uri.EscapeDataString(A1(sheet, "1:1"))}?majorDimension=ROWS", cancellationToken);
                var headers = (top["values"] as JsonArray)?.FirstOrDefault() is JsonArray first ? Cells(first) : new List<string>();
                if (headers.All(string.IsNullOrWhiteSpace))
                {
                    // An empty sheet: the first record's fields become its headers, written above the rows.
                    headers = records.Count > 0 ? ((JsonObject)records[0]!).Select(field => field.Key).ToList() : new List<string>();
                    rows.Add(new JsonArray(headers.Select(header => (JsonNode?)JsonValue.Create(header)).ToArray()));
                }

                foreach (JsonObject record in records.Cast<JsonObject>())
                {
                    rows.Add(new JsonArray(headers.Select(header => (JsonNode?)JsonValue.Create(record.TryGetPropertyValue(header, out var cell) ? Cell(cell) : "")).ToArray()));
                    foreach (var field in record.Select(field => field.Key).Where(key => !headers.Contains(key, StringComparer.Ordinal)))
                    {
                        leftOut.Add(field);
                    }
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/spreadsheets/{spreadsheet}/values/{Uri.EscapeDataString(A1(sheet, null))}:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS")
            {
                Content = new StringContent(new JsonObject { ["values"] = rows }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            var answer = await SendAsync(client, token, request, cancellationToken);
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["updated_range"] = answer["updates"]?["updatedRange"]?.GetValue<string>(),
                ["updated_rows"] = answer["updates"]?["updatedRows"] is JsonValue updated && updated.TryGetValue<long>(out var count) ? count : 0L,
                ["left_out"] = string.Join(", ", leftOut),
            });
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        private static Task<JsonObject> GetJsonAsync(HttpClient client, string token, string url, CancellationToken cancellationToken) =>
            SendAsync(client, token, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        // Google's answer as JSON; a failure thrown with its status and Google's own short status word (NOT_FOUND, PERMISSION_DENIED).
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (request)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Google Sheets answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = json?["error"]?["status"]?.GetValue<string>();
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}.");
                }

                return json as JsonObject ?? throw new InvalidOperationException("Google answered with no JSON.");
            }
        }

        private static string Spreadsheet(string id) =>
            SpreadsheetId.IsMatch(id.Trim()) ? id.Trim() : throw new InvalidOperationException($"'{id}' is not a spreadsheet id - it is in the spreadsheet's address, docs.google.com/spreadsheets/d/{{id}}/edit.");

        // A1 notation: a sheet alone, or a range of it - the sheet's name quoted, its quotes doubled, as Google reads it.
        private static string A1(string sheet, string? range) =>
            string.IsNullOrWhiteSpace(range) ? sheet : "'" + sheet.Replace("'", "''", StringComparison.Ordinal) + "'!" + range.Trim();

        private static List<string> Cells(JsonNode? row) => (row as JsonArray ?? new JsonArray()).Select(Cell).ToList();

        private static string Cell(JsonNode? cell) => cell switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value => value.ToJsonString(),
            _ => cell.ToJsonString(),
        };

        // Each header as a field name: a blank one is its column's letter, a repeated one numbered, so no value is lost.
        private static List<string> Headers(List<string> row, int firstColumn)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var headers = new List<string>();
            for (var column = 0; column < row.Count; column++)
            {
                var name = string.IsNullOrWhiteSpace(row[column]) ? ColumnLetter(firstColumn + column) : row[column].Trim();
                seen[name] = seen.TryGetValue(name, out var count) ? count + 1 : 1;
                headers.Add(seen[name] == 1 ? name : name + " (" + seen[name].ToString(CultureInfo.InvariantCulture) + ")");
            }

            return headers;
        }

        // The answer's range ("'Sheet 1'!B3:D9", "Sheet1!A1:Z1000") says where its first cell is.
        private static int FirstRow(string? range)
        {
            var cell = FirstCell(range);
            var digits = new string(cell.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) && row > 0 ? row : 1;
        }

        private static int FirstColumn(string? range)
        {
            var letters = new string(FirstCell(range).TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            var column = 0;
            foreach (var letter in letters)
            {
                column = column * 26 + (letter - 'A' + 1);
            }

            return Math.Max(column, 1);
        }

        private static string FirstCell(string? range)
        {
            var afterSheet = range is null ? "" : range[(range.LastIndexOf('!') + 1)..];
            return afterSheet.Split(':')[0];
        }

        private static string ColumnLetter(int column)
        {
            var letters = "";
            for (var value = column; value > 0; value = (value - 1) / 26)
            {
                letters = (char)('A' + (value - 1) % 26) + letters;
            }

            return letters;
        }
    }
}
