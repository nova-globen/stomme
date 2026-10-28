#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.MicrosoftExcel
{
    /// <summary>
    /// Microsoft Excel 365 (BL-229, n8n's Microsoft Excel 365 node: its workbooks, sheets and tables): workbooks in a OneDrive or a
    /// SharePoint document library over Microsoft Graph (<c>https://graph.microsoft.com/v1.0</c>), app-only - an Entra ID app
    /// registration's client id and secret and the tenant's token endpoint (OAuth2 client credentials, the <c>.default</c> scope; the
    /// app granted Files.ReadWrite.All) through the shared <see cref="OAuth2TokenProvider"/>, as Outlook's is. The connection names
    /// the drive: a user's OneDrive (<c>user</c>, an address or a user id) or a document library's drive (<c>drive</c>, its id). A
    /// test reads the drive's name.
    /// </summary>
    /// <remarks>
    /// A workbook is its drive item's id or its path in the drive (<c>/Reports/Sales.xlsx</c>); a sheet its name (or its id); a
    /// table its name (or its id); a range A1 notation (<c>A1:D20</c>) - each held to its shape and escaped before it reaches the
    /// path, so nothing is sent when one is wrong. <c>workbooks</c>: the drive's workbooks a search finds. <c>worksheets</c>,
    /// <c>add_worksheet</c>, <c>delete_worksheet</c>, <c>delete_workbook</c>. <c>read_rows</c>: the sheet's used range (or a
    /// range), the first row the headers - each later row one record, its fields named by the headers (a blank one its column's
    /// letter, a repeated one numbered), <c>row_number</c> its row in the sheet; with <c>header_row</c> false each field is named by
    /// its column's letter. <c>append_rows</c>: a record or a list of records (or of lists) written under the headers by name after
    /// the last used row - an empty sheet gets the first record's fields as its headers. <c>update_rows</c> and <c>upsert_rows</c>:
    /// each record finds its row by <c>match_column</c> (its text, or the same number) - the first, or every one with
    /// <c>all_matches</c> - and only its fields change, a formula in a cell it leaves alone kept; an upsert appends what matched
    /// nothing. <c>clear</c>: a range or the whole sheet, its contents, formats or both. <c>table_rows</c>: a table's rows, page by
    /// page, keyed by its columns; <c>append_table_rows</c>, <c>lookup</c> (a column's exact text, the first or every match),
    /// <c>table_columns</c>, <c>add_table</c> (a range, or the sheet's used range), <c>delete_table</c>, <c>convert_table</c>. A field
    /// no header or column names is left out and said; the match column itself is never rewritten. A throttled call waits as Graph
    /// says; a failure is thrown with its status, Graph's code and its message.
    /// </remarks>
    public sealed class MicrosoftExcelConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com/v1.0";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string WorkbooksOperation = "workbooks";

        public const string WorksheetsOperation = "worksheets";

        public const string AddWorksheetOperation = "add_worksheet";

        public const string DeleteWorksheetOperation = "delete_worksheet";

        public const string DeleteWorkbookOperation = "delete_workbook";

        public const string ReadRowsOperation = "read_rows";

        public const string AppendRowsOperation = "append_rows";

        public const string UpdateRowsOperation = "update_rows";

        public const string UpsertRowsOperation = "upsert_rows";

        public const string ClearOperation = "clear";

        public const string TableRowsOperation = "table_rows";

        public const string AppendTableRowsOperation = "append_table_rows";

        public const string LookupOperation = "lookup";

        public const string TableColumnsOperation = "table_columns";

        public const string AddTableOperation = "add_table";

        public const string DeleteTableOperation = "delete_table";

        public const string ConvertTableOperation = "convert_table";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex ItemId = new("^[A-Za-z0-9!_-]{2,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex DriveId = new("^[A-Za-z0-9!_-]{2,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex User = new("^[^\\s/\\\\?#@]+@[^\\s/\\\\?#@]+$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        // Excel's own rule for a sheet's name: at most 31 characters, none of : \ / ? * [ ] - and no line break; or its id, {GUID}.
        private static readonly Regex Sheet = new("^([^:\\\\/?*\\[\\]\\r\\n]{1,31}|\\{[0-9A-Fa-f-]{36}\\})$", RegexOptions.CultureInvariant);

        private static readonly Regex Table = new("^(\\{[0-9A-Fa-f-]{36}\\}|[A-Za-z_][A-Za-z0-9_.]{0,254})$", RegexOptions.CultureInvariant);

        private static readonly Regex Range = new("^[A-Za-z]{1,3}[0-9]{1,7}(:[A-Za-z]{1,3}[0-9]{1,7})?$", RegexOptions.CultureInvariant);

        private static readonly Regex Cell = new("^([A-Za-z]{1,3})([0-9]{1,7})$", RegexOptions.CultureInvariant);

        // JSON as written: text, its accents and its markup, as it is.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            WorkbooksOperation, WorksheetsOperation, AddWorksheetOperation, DeleteWorksheetOperation, DeleteWorkbookOperation, ReadRowsOperation, AppendRowsOperation,
            UpdateRowsOperation, UpsertRowsOperation, ClearOperation, TableRowsOperation, AppendTableRowsOperation, LookupOperation, TableColumnsOperation, AddTableOperation,
            DeleteTableOperation, ConvertTableOperation,
        };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsoftexcel";

        public string DisplayName => "Microsoft Excel 365";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.microsoft.com/microsoft-365/excel");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/excel");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: false,
                Description: "Whose OneDrive holds the workbooks - their address (ada@contoso.com) or user id. Or name a drive below."),
            new ConnectorConfigField("drive", "Drive", ConnectorConfigFieldType.Text, Required: false,
                Description: "A SharePoint document library's drive id (b!...), in place of a user's OneDrive."),
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false,
                Description: "The token scope requested from the identity platform. Leave it as it is.") { Default = DefaultScope },
        });

        private static readonly ConnectorConfigField WorkbookField = new("workbook", "Workbook", ConnectorConfigFieldType.Text, Required: true, Description: "Its drive item's id, or its path in the drive - /Reports/Sales.xlsx.");

        private static readonly ConnectorConfigField SheetField = new("sheet", "Sheet", ConnectorConfigFieldType.Text, Required: false, Description: "The sheet's name, as on its tab.") { Default = "Sheet1" };

        private static readonly ConnectorConfigField RangeField = new("range", "Range", ConnectorConfigFieldType.Text, Required: false, Description: "Part of the sheet, in A1 notation (A1:F100). Empty: its used range.");

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "The table's name (Table1) or id.");

        private static readonly ConnectorConfigField ValuesField = new("values", "Values", ConnectorConfigFieldType.Multiline, Required: true,
            Description: "A record or a list of records as JSON - each a row under the headers by name, a field no header names left out - or a list of lists, written as they are.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] Matching(bool upsert) => new[]
        {
            WorkbookField, SheetField, RangeField,
            new ConnectorConfigField("match_column", "Column to match on", ConnectorConfigFieldType.Text, Required: true, Description: "The header whose value finds each record's row - every record gives it."),
            new ConnectorConfigField("values", "Values", ConnectorConfigFieldType.Multiline, Required: true,
                Description: upsert ? "A record or a list of records as JSON: its fields change the row it matches; one that matches none is appended." : "A record or a list of records as JSON: its fields change the row it matches."),
            new ConnectorConfigField("all_matches", "Every match", ConnectorConfigFieldType.Boolean, Required: false, Description: "Change every row that matches, not only the first.") { Default = "false" },
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(WorkbooksOperation, "Get workbooks", "The drive's workbooks a search finds - their ids, names, paths and addresses - n8n's workbook Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in the name or the content. Empty: .xlsx."), MaxField }),
            },
            new ConnectorOperation(WorksheetsOperation, "Get sheets", "A workbook's sheets - n8n's sheet Get Many.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField }) },
            new ConnectorOperation(AddWorksheetOperation, "Add a sheet", "Adds a sheet to a workbook - n8n's workbook Add Sheet.")
            {
                Parameters = new ConnectorConfigSchema(new[] { WorkbookField, new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Unique in the workbook. Empty: Excel names it.") }),
            },
            new ConnectorOperation(DeleteWorksheetOperation, "Delete a sheet", "Deletes a sheet - n8n's sheet Delete.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField, SheetField with { Required = true, Default = null } }) },
            new ConnectorOperation(DeleteWorkbookOperation, "Delete a workbook", "Deletes the workbook's file - n8n's workbook Delete.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField }) },
            new ConnectorOperation(ReadRowsOperation, "Get rows", "The sheet's rows, the first row the headers: each later row one record, its fields named by the headers, row_number its row in the sheet - n8n's sheet Get Rows.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    WorkbookField, SheetField, RangeField,
                    new ConnectorConfigField("header_row", "First row is headers", ConnectorConfigFieldType.Boolean, Required: false, Description: "Off: every row a record, its fields named by column letter (A, B...).") { Default = "true" },
                }),
            },
            new ConnectorOperation(AppendRowsOperation, "Append rows", "Writes a record, or each of a list, as a row after the sheet's last used row, under its headers by name - n8n's sheet Append.")
            {
                Parameters = new ConnectorConfigSchema(new[] { WorkbookField, SheetField, ValuesField }),
            },
            new ConnectorOperation(UpdateRowsOperation, "Update rows", "Finds each record's row by a column and changes only the record's fields - n8n's sheet Update.") { Parameters = new ConnectorConfigSchema(Matching(upsert: false)) },
            new ConnectorOperation(UpsertRowsOperation, "Append or update rows", "Changes the row each record matches, or appends it - n8n's sheet Append or Update.") { Parameters = new ConnectorConfigSchema(Matching(upsert: true)) },
            new ConnectorOperation(ClearOperation, "Clear", "Clears a range or the whole sheet - its contents, its formats or both - n8n's sheet Clear.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    WorkbookField, SheetField with { Required = true, Default = null }, RangeField with { Description = "Part of the sheet, in A1 notation (A1:F100). Empty: the whole sheet." },
                    new ConnectorConfigField("apply_to", "Clear", ConnectorConfigFieldType.Text, Required: false, Description: "All, Contents or Formats.") { Default = "All" },
                }),
            },
            new ConnectorOperation(TableRowsOperation, "Get table rows", "A table's rows, each a record keyed by its columns - n8n's table Get Rows.")
            {
                Parameters = new ConnectorConfigSchema(new[] { WorkbookField, TableField, MaxField }),
            },
            new ConnectorOperation(AppendTableRowsOperation, "Append table rows", "Adds a record, or each of a list, as a table row under its columns by name - n8n's table Append.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    WorkbookField, TableField, ValuesField with { Description = "A record or a list of records as JSON - each a row under the columns by name, a field no column names left out - or a list of lists, written as they are." },
                    new ConnectorConfigField("index", "Position", ConnectorConfigFieldType.Integer, Required: false, Description: "Where the rows go, 0 the top; the rows below move down. Empty: the end."),
                }),
            },
            new ConnectorOperation(LookupOperation, "Look up", "A table's rows whose column holds a value, exactly - the first, or every one - n8n's table Lookup.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    WorkbookField, TableField,
                    new ConnectorConfigField("column", "Column", ConnectorConfigFieldType.Text, Required: true, Description: "The column to look in."),
                    new ConnectorConfigField("value", "Value", ConnectorConfigFieldType.Text, Required: true, Description: "The value to find, exactly."),
                    new ConnectorConfigField("all_matches", "Every match", ConnectorConfigFieldType.Boolean, Required: false, Description: "Every row that matches, not only the first.") { Default = "false" },
                }),
            },
            new ConnectorOperation(TableColumnsOperation, "Get table columns", "A table's columns - n8n's table Get Columns.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField, TableField }) },
            new ConnectorOperation(AddTableOperation, "Add a table", "Makes a range of a sheet a table - n8n's table Add.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    WorkbookField, SheetField, RangeField with { Description = "The table's range, in A1 notation (A1:D20). Empty: the sheet's used range." },
                    new ConnectorConfigField("has_headers", "Has headers", ConnectorConfigFieldType.Boolean, Required: false, Description: "The range's first row names the columns.") { Default = "true" },
                }),
            },
            new ConnectorOperation(DeleteTableOperation, "Delete a table", "Deletes a table and its data - n8n's table Delete.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField, TableField }) },
            new ConnectorOperation(ConvertTableOperation, "Convert a table to a range", "Makes a table plain cells again, its data kept - n8n's table Convert to Range.") { Parameters = new ConnectorConfigSchema(new[] { WorkbookField, TableField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var drive = DriveUrl(context.Endpoint, context.Settings);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await SendAsync(client, token, HttpMethod.Get, drive + "?$select=name,driveType", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in - the drive {Text(answer["name"]) ?? "(unnamed)"}{(Text(answer["driveType"]) is { } kind ? " (" + kind + ")" : "")}.");
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
            var drive = DriveUrl(context.Endpoint, context.Settings);
            var operation = context.OperationName ?? ReadRowsOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Microsoft Excel 365 has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before a token is asked for.
            var workbook = Is(operation, WorkbooksOperation) ? null : Item(drive, Values.Get(parameters, "workbook"));
            var usesSheet = Is(operation, DeleteWorksheetOperation) || Is(operation, ReadRowsOperation) || Is(operation, AppendRowsOperation) || Is(operation, UpdateRowsOperation)
                || Is(operation, UpsertRowsOperation) || Is(operation, ClearOperation) || Is(operation, AddTableOperation);
            var sheetName = usesSheet ? SheetName(Values.Get(parameters, "sheet") ?? (Is(operation, DeleteWorksheetOperation) || Is(operation, ClearOperation) ? null : "Sheet1")) : null;
            var sheet = sheetName is null ? null : $"{workbook}/workbook/worksheets/{Uri.EscapeDataString(sheetName)}";
            var range = Values.Get(parameters, "range") is { } given ? Address(given) : null;
            var usesTable = Is(operation, TableRowsOperation) || Is(operation, AppendTableRowsOperation) || Is(operation, LookupOperation) || Is(operation, TableColumnsOperation)
                || Is(operation, DeleteTableOperation) || Is(operation, ConvertTableOperation);
            var table = usesTable ? $"{workbook}/workbook/tables/{Uri.EscapeDataString(TableName(Values.Get(parameters, "table")))}" : null;
            var max = Values.Get(parameters, "max") is { } most ? Positive(most, "max") : DefaultMax;
            var values = Is(operation, AppendRowsOperation) || Is(operation, UpdateRowsOperation) || Is(operation, UpsertRowsOperation) || Is(operation, AppendTableRowsOperation)
                ? Records(Values.Get(parameters, "values"), allowLists: !Is(operation, UpdateRowsOperation) && !Is(operation, UpsertRowsOperation))
                : null;
            var matchColumn = Is(operation, UpdateRowsOperation) || Is(operation, UpsertRowsOperation) ? Values.Get(parameters, "match_column") ?? throw new InvalidOperationException("Name the column to match on.") : null;
            if (matchColumn is not null && values!.FirstOrDefault(record => !((JsonObject)record!).ContainsKey(matchColumn)) is not null)
            {
                throw new InvalidOperationException($"A record gives no '{matchColumn}' - the column to match on.");
            }

            var allMatches = Flag(parameters, "all_matches", false);
            var headerRow = Flag(parameters, "header_row", true);
            var hasHeaders = Flag(parameters, "has_headers", true);
            var applyTo = Is(operation, ClearOperation) ? Choice(Values.Get(parameters, "apply_to") ?? "All", "apply to", "All", "Contents", "Formats") : null;
            var index = Is(operation, AppendTableRowsOperation) && Values.Get(parameters, "index") is { } at ? Position(at) : (int?)null;
            var lookupColumn = Is(operation, LookupOperation) ? Values.Get(parameters, "column") ?? throw new InvalidOperationException("Name the column to look in.") : null;
            var lookupValue = Is(operation, LookupOperation) ? (parameters.TryGetValue("value", out var wanted) ? wanted : null) ?? throw new InvalidOperationException("Give the value to look for.") : null;
            var newName = Is(operation, AddWorksheetOperation) && Values.Get(parameters, "name") is { } named ? SheetName(named) : null;

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, WorkbooksOperation))
            {
                var query = (Values.Get(parameters, "query") ?? ".xlsx").Replace("'", "''", StringComparison.Ordinal);
                var read = 0;
                string? url = $"{drive}/root/search(q='{Uri.EscapeDataString(query)}')?$top={Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture)}&$select=id,name,webUrl,size,lastModifiedDateTime,parentReference,file";
                while (url is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var item in (page["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (Text(item["name"]) is not { } name || !(name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue; // a search finds more than workbooks
                        }

                        read++;
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = Text(item["id"]),
                            ["name"] = name,
                            ["path"] = Text(item["parentReference"]?["path"]) is { } parent && parent.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? parent[(colon + 1)..].TrimEnd('/') + "/" + name : "/" + name,
                            ["url"] = Text(item["webUrl"]),
                            ["size"] = item["size"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : null,
                            ["modified"] = Text(item["lastModifiedDateTime"]),
                        });
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    // Graph's next link followed only under the drive's own address - the token never leaves with it.
                    url = Text(page["@odata.nextLink"]) is { } next && next.StartsWith(drive + "/", StringComparison.OrdinalIgnoreCase) ? next : null;
                }

                yield break;
            }

            if (Is(operation, WorksheetsOperation))
            {
                var sheets = await SendAsync(client, token, HttpMethod.Get, $"{workbook}/workbook/worksheets?$select=id,name,position,visibility", null, cancellationToken);
                foreach (var item in (sheets["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return SheetRecord(item);
                }

                yield break;
            }

            if (Is(operation, AddWorksheetOperation))
            {
                yield return SheetRecord(await SendAsync(client, token, HttpMethod.Post, $"{workbook}/workbook/worksheets/add", newName is null ? new JsonObject() : new JsonObject { ["name"] = newName }, cancellationToken));
                yield break;
            }

            if (Is(operation, DeleteWorksheetOperation) || Is(operation, DeleteWorkbookOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, sheet ?? workbook!, null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [sheet is null ? "workbook" : "sheet"] = sheetName ?? Values.Get(parameters, "workbook"), ["deleted"] = true });
                yield break;
            }

            if (Is(operation, ClearOperation))
            {
                await SendAsync(client, token, HttpMethod.Post, $"{sheet}/range{(range is null ? "" : $"(address='{range}')")}/clear", new JsonObject { ["applyTo"] = applyTo }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["sheet"] = sheetName, ["range"] = range ?? "", ["cleared"] = applyTo });
                yield break;
            }

            if (Is(operation, AddTableOperation))
            {
                // No range given: the sheet's used range, n8n's Automatically.
                var cells = range ?? (Text((await SendAsync(client, token, HttpMethod.Get, $"{sheet}/usedRange?$select=address", null, cancellationToken))["address"]) is { } address
                    ? address[(address.LastIndexOf('!') + 1)..] : throw new InvalidOperationException("Graph gave the sheet no used range."));
                var made = await SendAsync(client, token, HttpMethod.Post, $"{workbook}/workbook/tables/add",
                    new JsonObject { ["address"] = "'" + sheetName!.Replace("'", "''", StringComparison.Ordinal) + "'!" + cells, ["hasHeaders"] = hasHeaders }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(made["id"]), ["name"] = Text(made["name"]) });
                yield break;
            }

            if (Is(operation, DeleteTableOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, table!, null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = Values.Get(parameters, "table"), ["deleted"] = true });
                yield break;
            }

            if (Is(operation, ConvertTableOperation))
            {
                var plain = await SendAsync(client, token, HttpMethod.Post, $"{table}/convertToRange", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = Values.Get(parameters, "table"), ["address"] = Text(plain["address"]) });
                yield break;
            }

            if (usesTable)
            {
                var columns = (await SendAsync(client, token, HttpMethod.Get, $"{table}/columns?$select=id,name,index", null, cancellationToken))["value"] as JsonArray ?? new JsonArray();
                var names = columns.Select(column => Text(column?["name"]) ?? "").ToList();
                if (Is(operation, TableColumnsOperation))
                {
                    foreach (var column in columns.OfType<JsonObject>())
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = Text(column["id"]),
                            ["name"] = Text(column["name"]),
                            ["index"] = column["index"] is JsonValue position && position.TryGetValue<long>(out var at2) ? at2 : null,
                        });
                    }

                    yield break;
                }

                if (Is(operation, AppendTableRowsOperation))
                {
                    var (rows, leftOut) = Rows(values!, names);
                    var body = new JsonObject { ["values"] = rows };
                    if (index is not null)
                    {
                        body["index"] = index;
                    }

                    var added = await SendAsync(client, token, HttpMethod.Post, $"{table}/rows/add", body, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["table"] = Values.Get(parameters, "table"),
                        ["index"] = added["index"] is JsonValue first && first.TryGetValue<long>(out var row) ? row : null,
                        ["added"] = (long)rows.Count,
                        ["left_out"] = string.Join(", ", leftOut),
                    });
                    yield break;
                }

                var lookupAt = lookupColumn is null ? -1 : names.IndexOf(lookupColumn);
                if (lookupColumn is not null && lookupAt < 0)
                {
                    throw new InvalidOperationException($"The table has no column '{lookupColumn}' - its columns: {string.Join(", ", names)}.");
                }

                var taken = 0;
                for (var skip = 0; ; skip += PageSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = lookupColumn is null ? Math.Min(PageSize, max - taken) : PageSize;
                    var page = (await SendAsync(client, token, HttpMethod.Get, $"{table}/rows?$top={size.ToString(CultureInfo.InvariantCulture)}&$skip={skip.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken))["value"] as JsonArray ?? new JsonArray();
                    foreach (var item in page.OfType<JsonObject>())
                    {
                        var cells = (item["values"] as JsonArray)?.FirstOrDefault() as JsonArray ?? new JsonArray();
                        if (lookupColumn is not null && !string.Equals(CellText(lookupAt < cells.Count ? cells[lookupAt] : null), lookupValue, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row_index"] = item["index"] is JsonValue position && position.TryGetValue<long>(out var at3) ? at3 : null };
                        for (var column = 0; column < names.Count; column++)
                        {
                            fields[names[column]] = column < cells.Count ? Plain(cells[column]) : null;
                        }

                        yield return new ConnectorRecord(fields);
                        taken++;
                        if ((lookupColumn is not null && !allMatches) || (lookupColumn is null && taken >= max))
                        {
                            yield break;
                        }
                    }

                    if (page.Count < size)
                    {
                        yield break;
                    }
                }
            }

            // The sheet's cells: a range, or its used range - their address, values and (to write back) formulas.
            var cellsUrl = $"{sheet}/{(range is null ? "usedRange(valuesOnly=true)" : $"range(address='{range}')")}?$select=address,values{(Is(operation, UpdateRowsOperation) || Is(operation, UpsertRowsOperation) ? ",formulas" : "")}";
            var used = await SendAsync(client, token, HttpMethod.Get, cellsUrl, null, cancellationToken);
            var (firstColumn, firstRow, lastRow) = Bounds(Text(used["address"]));
            var grid = (used["values"] as JsonArray ?? new JsonArray()).Select(row => (row as JsonArray ?? new JsonArray()).ToList()).ToList();
            var empty = grid.All(row => row.All(cell => CellText(cell).Length == 0));

            if (Is(operation, ReadRowsOperation))
            {
                if (empty)
                {
                    yield break;
                }

                var headers = headerRow ? Headers(grid[0], firstColumn) : null;
                for (var index2 = headerRow ? 1 : 0; index2 < grid.Count; index2++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row_number"] = (long)(firstRow + index2) };
                    for (var column = 0; column < (headers?.Count ?? grid[index2].Count); column++)
                    {
                        fields[headers is null ? ColumnLetter(firstColumn + column) : headers[column]] = column < grid[index2].Count ? Plain(grid[index2][column]) : null;
                    }

                    yield return new ConnectorRecord(fields);
                }

                yield break;
            }

            var header = empty ? new List<string>() : grid[0].Select(cell => CellText(cell).Trim()).ToList();
            if (Is(operation, AppendRowsOperation))
            {
                var lists = values!.Count > 0 && values.All(record => record is JsonArray);
                var write = new JsonArray();
                var leftOut = new SortedSet<string>(StringComparer.Ordinal);
                if (lists)
                {
                    var width = values.Max(row => ((JsonArray)row!).Count);
                    foreach (JsonArray row in values.Cast<JsonArray>())
                    {
                        write.Add(new JsonArray(Enumerable.Range(0, width).Select(column => column < row.Count ? Value(row[column]) : JsonValue.Create("")).ToArray()));
                    }
                }
                else
                {
                    if (header.All(name => name.Length == 0))
                    {
                        // An empty sheet: the first record's fields become its headers, written above the rows.
                        header = ((JsonObject)values[0]!).Select(field => field.Key).ToList();
                        write.Add(new JsonArray(header.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()));
                    }

                    var (rows, missing) = Rows(values, header);
                    foreach (var row in rows)
                    {
                        write.Add(row?.DeepClone());
                    }

                    leftOut.UnionWith(missing);
                }

                var start = empty ? firstRow : lastRow + 1;
                var columns = Math.Max(1, write.Max(row => ((JsonArray)row!).Count));
                var address = $"{ColumnLetter(firstColumn)}{start}:{ColumnLetter(firstColumn + columns - 1)}{start + write.Count - 1}";
                var answer = await SendAsync(client, token, new HttpMethod("PATCH"), $"{sheet}/range(address='{address}')", new JsonObject { ["values"] = write }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["updated_range"] = Text(answer["address"]) ?? address,
                    ["updated_rows"] = (long)write.Count,
                    ["left_out"] = string.Join(", ", leftOut),
                });
                yield break;
            }

            // update_rows / upsert_rows: each record finds its row by the match column; only its own fields change.
            if (empty || grid.Count < 1)
            {
                throw new InvalidOperationException("The sheet has no headers to match on - append rows first.");
            }

            var matchAt = header.IndexOf(matchColumn!.Trim());
            if (matchAt < 0)
            {
                throw new InvalidOperationException($"The sheet has no column '{matchColumn}' - its headers: {string.Join(", ", header.Where(name => name.Length > 0))}.");
            }

            var formulas = (used["formulas"] as JsonArray ?? new JsonArray()).Select(row => (row as JsonArray ?? new JsonArray()).ToList()).ToList();
            var changed = new SortedDictionary<int, (List<JsonNode?> Shown, List<JsonNode?> Written)>();
            var results = new List<(int? Line, long? SheetRow, string Action, JsonObject Record)>();
            var appended = new List<JsonObject>();
            var outside = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JsonObject record in values!.Cast<JsonObject>())
            {
                outside.UnionWith(record.Select(field => field.Key).Where(key => !header.Contains(key, StringComparer.Ordinal)));
                var wantedValue = record[matchColumn!];
                var rows = Enumerable.Range(1, grid.Count - 1).Where(row => Same(matchAt < grid[row].Count ? grid[row][matchAt] : null, wantedValue)).ToList();
                if (rows.Count == 0)
                {
                    if (Is(operation, UpsertRowsOperation))
                    {
                        appended.Add(record);
                    }
                    else
                    {
                        results.Add((null, null, "not found", record));
                    }

                    continue;
                }

                foreach (var row in allMatches ? rows : rows.Take(1))
                {
                    if (!changed.TryGetValue(row, out var line))
                    {
                        line = (grid[row].Select(cell => cell?.DeepClone()).ToList(), (row < formulas.Count ? formulas[row] : grid[row]).Select(cell => cell?.DeepClone()).ToList());
                        changed[row] = line;
                    }

                    foreach (var (key, value) in record)
                    {
                        // The match column is left as it is: it matched, and "2" given as text must not turn the number 2 into text.
                        var column = header.IndexOf(key);
                        if (column >= 0 && column != matchAt && column < line.Shown.Count)
                        {
                            line.Shown[column] = Value(value);
                            line.Written[column] = Value(value);
                        }
                    }

                    results.Add((row, firstRow + row, "updated", record));
                }
            }

            foreach (var (row, line) in changed)
            {
                var number = firstRow + row;
                await SendAsync(client, token, new HttpMethod("PATCH"), $"{sheet}/range(address='{ColumnLetter(firstColumn)}{number}:{ColumnLetter(firstColumn + line.Written.Count - 1)}{number}')",
                    new JsonObject { ["formulas"] = new JsonArray(new JsonArray(line.Written.Select(cell => cell?.DeepClone()).ToArray())) }, cancellationToken);
            }

            if (appended.Count > 0)
            {
                var (rows, _) = Rows(appended.Cast<JsonNode?>().ToList(), header);
                var start = lastRow + 1;
                await SendAsync(client, token, new HttpMethod("PATCH"), $"{sheet}/range(address='{ColumnLetter(firstColumn)}{start}:{ColumnLetter(firstColumn + header.Count - 1)}{start + rows.Count - 1}')",
                    new JsonObject { ["values"] = rows }, cancellationToken);
                for (var i = 0; i < appended.Count; i++)
                {
                    results.Add((null, start + i, "appended", appended[i]));
                }
            }

            var said = string.Join(", ", outside);
            foreach (var (gridRow, sheetRow, action, record) in results)
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (gridRow is { } at4)
                {
                    var line = changed[at4].Shown;
                    for (var column = 0; column < header.Count; column++)
                    {
                        fields[header[column].Length == 0 ? ColumnLetter(firstColumn + column) : header[column]] = column < line.Count ? Plain(line[column]) : null;
                    }
                }
                else
                {
                    foreach (var (key, value) in record.Where(field => header.Contains(field.Key, StringComparer.Ordinal)))
                    {
                        fields[key] = Plain(value);
                    }
                }

                fields["row_number"] = sheetRow;
                fields["action"] = action;
                fields["left_out"] = said;
                yield return new ConnectorRecord(fields);
            }
        }

        // Records to rows under the headers (or a table's columns) by name, each cell a value Graph takes; a field none names left out.
        private static (JsonArray Rows, SortedSet<string> LeftOut) Rows(List<JsonNode?> records, List<string> names)
        {
            var rows = new JsonArray();
            var leftOut = new SortedSet<string>(StringComparer.Ordinal);
            if (records.Count > 0 && records.All(record => record is JsonArray))
            {
                foreach (JsonArray row in records.Cast<JsonArray>())
                {
                    rows.Add(new JsonArray(Enumerable.Range(0, names.Count).Select(column => column < row.Count ? Value(row[column]) : JsonValue.Create("")).ToArray()));
                }

                return (rows, leftOut);
            }

            foreach (JsonObject record in records.Cast<JsonObject>())
            {
                rows.Add(new JsonArray(names.Select(name => name.Length > 0 && record.TryGetPropertyValue(name, out var cell) ? Value(cell) : JsonValue.Create("")).ToArray()));
                leftOut.UnionWith(record.Select(field => field.Key).Where(key => !names.Contains(key, StringComparer.Ordinal)));
            }

            return (rows, leftOut);
        }

        // The values given: a record, a list of records or (where lists are written as they are) a list of lists - never a mix.
        private static List<JsonNode?> Records(string? given, bool allowLists)
        {
            if (string.IsNullOrWhiteSpace(given))
            {
                throw new InvalidOperationException("Give the values - a record or a list of records as JSON.");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The values are not JSON - a record or a list of records.");
            }

            var records = parsed switch
            {
                JsonObject one => new List<JsonNode?> { one },
                JsonArray many when many.Count > 0 => many.ToList(),
                _ => throw new InvalidOperationException("The values are not a record or a list of records."),
            };
            if (records.All(record => record is JsonObject) || (allowLists && records.All(record => record is JsonArray)))
            {
                return records;
            }

            throw new InvalidOperationException(allowLists ? "The values are all records or all lists, not both." : "The values are records - each a JSON object.");
        }

        // A cell as Graph takes it: text, a number or true/false as it is; nothing an empty cell; anything else its JSON text.
        private static JsonNode? Value(JsonNode? cell) => cell switch
        {
            null => JsonValue.Create(""),
            JsonValue value => value.DeepClone(),
            _ => JsonValue.Create(cell.ToJsonString(Wire)),
        };

        // A cell as a record holds it: text, a whole number, a number or true/false.
        private static object? Plain(JsonNode? cell) => cell switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var real) => real,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => cell.ToJsonString(Wire),
        };

        private static string CellText(JsonNode? cell) => cell switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            JsonValue value when value.TryGetValue<double>(out var real) => real.ToString(CultureInfo.InvariantCulture),
            _ => cell.ToJsonString(Wire),
        };

        // A row's cell and a record's value the same: the same text, or the same number (12 and "12.0"); an empty cell matches nothing.
        private static bool Same(JsonNode? cell, JsonNode? wanted)
        {
            var have = CellText(cell);
            var want = CellText(wanted);
            if (have.Length == 0)
            {
                return false;
            }

            return string.Equals(have, want, StringComparison.Ordinal)
                || (double.TryParse(have, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b);
        }

        // Each header as a field name: a blank one is its column's letter, a repeated one numbered, so no value is lost.
        private static List<string> Headers(List<JsonNode?> row, int firstColumn)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var headers = new List<string>();
            for (var column = 0; column < row.Count; column++)
            {
                var name = CellText(row[column]).Trim() is { Length: > 0 } text ? text : ColumnLetter(firstColumn + column);
                seen[name] = seen.TryGetValue(name, out var count) ? count + 1 : 1;
                headers.Add(seen[name] == 1 ? name : name + " (" + seen[name].ToString(CultureInfo.InvariantCulture) + ")");
            }

            return headers;
        }

        // A range's address (Sheet1!B2:E40, 'My sheet'!A1) as its first column (1 is A), its first row and its last row.
        private static (int FirstColumn, int FirstRow, int LastRow) Bounds(string? address)
        {
            var cells = (address ?? "A1")[((address ?? "A1").LastIndexOf('!') + 1)..].Split(':');
            var from = Cell.Match(cells[0]);
            var to = Cell.Match(cells[^1]);
            if (!from.Success || !to.Success)
            {
                throw new InvalidOperationException($"Graph gave a range Excel does not write - '{address}'.");
            }

            return (ColumnNumber(from.Groups[1].Value), int.Parse(from.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(to.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        private static int ColumnNumber(string letters) => letters.ToUpperInvariant().Aggregate(0, (number, letter) => (number * 26) + (letter - 'A' + 1));

        private static string ColumnLetter(int number)
        {
            var letters = "";
            for (; number > 0; number = (number - 1) / 26)
            {
                letters = (char)('A' + ((number - 1) % 26)) + letters;
            }

            return letters;
        }

        private static ConnectorRecord SheetRecord(JsonObject sheet) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(sheet["id"]),
            ["name"] = Text(sheet["name"]),
            ["position"] = sheet["position"] is JsonValue position && position.TryGetValue<long>(out var at) ? at : null,
            ["visibility"] = Text(sheet["visibility"]),
        });

        // The drive under Graph: a user's OneDrive (the user held to an address's or a user id's shape) or a library's drive by id.
        private static string DriveUrl(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            var api = Values.BaseUrl(endpoint, DefaultEndpoint);
            if (Values.Get(settings, "drive") is { } drive)
            {
                return DriveId.IsMatch(drive) ? $"{api}/drives/{Uri.EscapeDataString(drive)}" : throw new InvalidOperationException($"'{drive}' is not a drive's id.");
            }

            var user = Values.Get(settings, "user") ?? throw new InvalidOperationException("Name whose OneDrive holds the workbooks (the connection's user) or a document library's drive (its drive).");
            return User.IsMatch(user) ? $"{api}/users/{Uri.EscapeDataString(user)}/drive" : throw new InvalidOperationException($"'{user}' is not a user - an address or a user id.");
        }

        // A workbook as Graph names it: its item's id, or its path from the drive's root, each part of the path escaped.
        private static string Item(string drive, string? given)
        {
            if (given is null)
            {
                throw new InvalidOperationException("Name the workbook - its id, or its path in the drive (/Reports/Sales.xlsx).");
            }

            if (given.StartsWith('/'))
            {
                var parts = given.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(new[] { ':', '\\', '?', '#', '%', '*', '"', '<', '>', '|', '\r', '\n' }) >= 0))
                {
                    throw new InvalidOperationException($"'{given}' is not a workbook's path - /Reports/Sales.xlsx.");
                }

                return $"{drive}/root:/{string.Join("/", parts.Select(Uri.EscapeDataString))}:";
            }

            return ItemId.IsMatch(given) ? $"{drive}/items/{Uri.EscapeDataString(given)}" : throw new InvalidOperationException($"'{given}' is not a workbook - its id, or its path in the drive (/Reports/Sales.xlsx).");
        }

        private static string SheetName(string? given) =>
            given is not null && Sheet.IsMatch(given) && given.Trim().Length > 0 ? given : throw new InvalidOperationException($"'{given}' is not a sheet's name - at most 31 characters, none of : \\ / ? * [ ].");

        private static string TableName(string? given) =>
            given is not null && Table.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a table's name or id - Table1.");

        private static string Address(string given) =>
            Range.IsMatch(given) ? given.ToUpperInvariant() : throw new InvalidOperationException($"'{given}' is not a range - A1:D20 (a whole column, A:B, is not).");

        private static int Positive(string given, string what) =>
            int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : throw new InvalidOperationException($"'{given}' is not a number above 0 ({what}).");

        private static int Position(string given) =>
            int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidOperationException($"'{given}' is not a position - 0 the top.");

        private static bool Flag(IReadOnlyDictionary<string, string> parameters, string name, bool fallback) =>
            Values.Get(parameters, name) is not { } given ? fallback
                : given.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                : given.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidOperationException($"'{given}' is not true or false ({name.Replace('_', ' ')}).");

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not what to {what.Split(' ')[0]} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Graph's answer as JSON (an empty answer an empty object), a throttled call waiting as Graph says; a failure thrown with its
        // status, Graph's own code and its message.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Microsoft Excel 365 answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["error"]?["code"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
