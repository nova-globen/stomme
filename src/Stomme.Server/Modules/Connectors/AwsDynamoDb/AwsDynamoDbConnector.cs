#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.AwsDynamoDb
{
    /// <summary>
    /// Amazon DynamoDB (n8n's AWS DynamoDB node: an item written, got, deleted and read many): an account's tables over
    /// DynamoDB's JSON protocol (<c>https://dynamodb.{region}.amazonaws.com</c>, <c>X-Amz-Target: DynamoDB_20120810.*</c>), every
    /// call signed with AWS Signature Version 4 by an IAM user's access key and secret, over the host's egress. Items are plain
    /// JSON both ways - a string a string, a number a number, an object a map - typed as DynamoDB keeps them only on the wire. A
    /// test names how many tables the key sees.
    /// </summary>
    /// <remarks>
    /// <c>put_item</c> (an item, replacing one with its key - on a condition), <c>get_item</c> (by its key, consistent or not,
    /// some attributes), <c>update_item</c> (an update expression on a condition - the item as it is after), <c>delete_item</c>
    /// (the item as it was), <c>query</c> (a key condition on a table or an index, a filter, either order) and <c>scan</c> (a
    /// filter) page by page up to <c>max</c>; <c>tables</c> and <c>describe_table</c>. An expression's names (#name) and values
    /// (:value) are JSON objects. Every value held to its shape before anything is signed or sent. A throttled call waits as AWS
    /// says; a failure is thrown with its status, AWS's error type and its message.
    /// </remarks>
    public sealed class AwsDynamoDbConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string PutItemOperation = "put_item";

        public const string GetItemOperation = "get_item";

        public const string UpdateItemOperation = "update_item";

        public const string DeleteItemOperation = "delete_item";

        public const string QueryOperation = "query";

        public const string ScanOperation = "scan";

        public const string TablesOperation = "tables";

        public const string DescribeTableOperation = "describe_table";

        public const int DefaultMax = 1000;

        private static readonly Regex Region = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex TableName = new("^[A-Za-z0-9_.-]{3,255}$", RegexOptions.CultureInvariant);

        private static readonly Regex NamePlaceholder = new("^#[A-Za-z0-9_]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly Regex ValuePlaceholder = new("^:[A-Za-z0-9_]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { PutItemOperation, GetItemOperation, UpdateItemOperation, DeleteItemOperation, QueryOperation, ScanOperation, TablesOperation, DescribeTableOperation };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awsdynamodb";

        public string DisplayName => "Amazon DynamoDB";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://dynamodb.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/dynamodb/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region of the tables - eu-north-1. The address is DynamoDB's in it when left empty."),
        });

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "Its name.");

        private static readonly ConnectorConfigField KeyField = new("key", "Key", ConnectorConfigFieldType.Text, Required: true, Description: "The item's key, a JSON object - {\"pk\": \"order#1042\", \"sk\": 1}.");

        private static readonly ConnectorConfigField ConditionField = new("condition", "Condition", ConnectorConfigFieldType.Text, Required: false, Description: "A condition expression - attribute_not_exists(pk).");

        private static readonly ConnectorConfigField NamesField = new("names", "Names", ConnectorConfigFieldType.Text, Required: false, Description: "The expressions' names, a JSON object - {\"#s\": \"status\"}.");

        private static readonly ConnectorConfigField ValuesField = new("values", "Values", ConnectorConfigFieldType.Text, Required: false, Description: "The expressions' values, a JSON object - {\":s\": \"paid\", \":n\": 3}.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PutItemOperation, "Put an item", "Writes an item, replacing one with its key - n8n's item Create or Update.")
            {
                Parameters = Schema(TableField, new ConnectorConfigField("item", "Item", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object - {\"pk\": \"order#1042\", \"total\": 99.5, \"paid\": true}."), ConditionField, NamesField, ValuesField),
            },
            new ConnectorOperation(GetItemOperation, "Get an item", "One item by its key - n8n's item Get.")
            {
                Parameters = Schema(
                    TableField, KeyField,
                    new ConnectorConfigField("projection", "Attributes", ConnectorConfigFieldType.Text, Required: false, Description: "A projection expression - pk, total, #s."),
                    NamesField,
                    new ConnectorConfigField("consistent", "Consistent read", ConnectorConfigFieldType.Boolean, Required: false, Description: "Read what was last written.")),
            },
            new ConnectorOperation(UpdateItemOperation, "Update an item", "Changes an item by an update expression - the item as it is after.")
            {
                Parameters = Schema(TableField, KeyField, new ConnectorConfigField("update", "Update", ConnectorConfigFieldType.Text, Required: true, Description: "An update expression - SET #s = :s ADD visits :one."), ConditionField, NamesField, ValuesField),
            },
            new ConnectorOperation(DeleteItemOperation, "Delete an item", "Deletes an item by its key - the item as it was; n8n's item Delete.")
            {
                Parameters = Schema(TableField, KeyField, ConditionField, NamesField, ValuesField),
            },
            new ConnectorOperation(QueryOperation, "Query", "The items one partition key holds, on a table or an index - n8n's item Get Many.")
            {
                Parameters = Schema(
                    TableField,
                    new ConnectorConfigField("key_condition", "Key condition", ConnectorConfigFieldType.Text, Required: true, Description: "A key condition expression - pk = :pk AND begins_with(sk, :day)."),
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "A filter expression on what the key found."),
                    new ConnectorConfigField("index", "Index", ConnectorConfigFieldType.Text, Required: false, Description: "A secondary index's name."),
                    new ConnectorConfigField("projection", "Attributes", ConnectorConfigFieldType.Text, Required: false, Description: "A projection expression."),
                    NamesField, ValuesField,
                    new ConnectorConfigField("descending", "Descending", ConnectorConfigFieldType.Boolean, Required: false, Description: "The sort key's highest first."),
                    Max()),
            },
            new ConnectorOperation(ScanOperation, "Scan", "Every item of a table or an index, filtered - n8n's item Get Many with Scan.")
            {
                Parameters = Schema(
                    TableField,
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "A filter expression - #s = :s."),
                    new ConnectorConfigField("index", "Index", ConnectorConfigFieldType.Text, Required: false, Description: "A secondary index's name."),
                    new ConnectorConfigField("projection", "Attributes", ConnectorConfigFieldType.Text, Required: false, Description: "A projection expression."),
                    NamesField, ValuesField, Max()),
            },
            new ConnectorOperation(TablesOperation, "Get tables", "The tables the key sees.") { Parameters = Schema(Max()) },
            new ConnectorOperation(DescribeTableOperation, "Describe a table", "A table's status, key, item count and size.") { Parameters = Schema(TableField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var found = await session.CallAsync("ListTables", new JsonObject { ["Limit"] = 100 }, cancellationToken) as JsonObject ?? new JsonObject();
                var count = (found["TableNames"] as JsonArray ?? new JsonArray()).Count;
                var more = Text(found["LastEvaluatedTableName"]) is not null;
                return ConnectionTestResult.Ok($"DynamoDB in {session.Region}: {(more ? $"{count} or more tables" : count == 1 ? "one table" : $"{count} tables")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? GetItemOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Amazon DynamoDB has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var request = new JsonObject();
            if (operation != TablesOperation)
            {
                request["TableName"] = Values.Get(p, "table") is { } table ? TableName.IsMatch(table) ? table : throw new InvalidOperationException($"'{table}' is not a table's name - letters, digits, _, - and ., 3 to 255.") : throw new InvalidOperationException("Give the table - its name.");
            }

            switch (operation)
            {
                case PutItemOperation:
                    request["Item"] = TypedItem(ObjectOf(Values.Get(p, "item") ?? throw new InvalidOperationException("Give the item - a JSON object."), "item is", "{\"pk\": \"order#1042\"}"));
                    break;
                case GetItemOperation:
                case UpdateItemOperation:
                case DeleteItemOperation:
                    {
                        var key = ObjectOf(Values.Get(p, "key") ?? throw new InvalidOperationException("Give the item's key - a JSON object."), "key is", "{\"pk\": \"order#1042\"}");
                        if (key.Count is 0 or > 2 || key.Any(part => part.Value is not JsonValue))
                        {
                            throw new InvalidOperationException("A key is its partition key's value and its sort key's, if the table has one - strings or numbers.");
                        }

                        request["Key"] = TypedItem(key);
                        break;
                    }
            }

            foreach (var (name, key) in new[] { ("condition", "ConditionExpression"), ("update", "UpdateExpression"), ("key_condition", "KeyConditionExpression"), ("filter", "FilterExpression"), ("projection", "ProjectionExpression") })
            {
                var applies = name switch
                {
                    "condition" => operation is PutItemOperation or UpdateItemOperation or DeleteItemOperation,
                    "update" => operation == UpdateItemOperation,
                    "key_condition" => operation == QueryOperation,
                    "filter" => operation is QueryOperation or ScanOperation,
                    _ => operation is GetItemOperation or QueryOperation or ScanOperation,
                };
                if (!applies)
                {
                    continue;
                }

                if (Values.Get(p, name) is { } expression)
                {
                    request[key] = expression.Length <= 4096 && !expression.Any(char.IsControl) ? expression : throw new InvalidOperationException($"The {name.Replace('_', ' ')} is one line of up to 4,096 characters.");
                }
                else if (name == "update" || name == "key_condition")
                {
                    throw new InvalidOperationException($"Give the {(name == "update" ? "update expression - SET #s = :s" : "key condition - pk = :pk")}.");
                }
            }

            if (operation is not (TablesOperation or DescribeTableOperation))
            {
                if (Values.Get(p, "names") is { } names)
                {
                    var given = ObjectOf(names, "names are", "{\"#s\": \"status\"}");
                    request["ExpressionAttributeNames"] = new JsonObject(given.Select(pair => KeyValuePair.Create(
                        NamePlaceholder.IsMatch(pair.Key) ? pair.Key : throw new InvalidOperationException($"'{pair.Key}' is not a name's placeholder - #, then letters, digits and _."),
                        (JsonNode?)(pair.Value is JsonValue value && value.TryGetValue<string>(out var attribute) && attribute.Length > 0 ? attribute : throw new InvalidOperationException($"The name {pair.Key} is not an attribute's name.")))));
                }

                if (Values.Get(p, "values") is { } values && operation != GetItemOperation)
                {
                    var given = ObjectOf(values, "values are", "{\":s\": \"paid\"}");
                    foreach (var pair in given)
                    {
                        if (!ValuePlaceholder.IsMatch(pair.Key))
                        {
                            throw new InvalidOperationException($"'{pair.Key}' is not a value's placeholder - :, then letters, digits and _.");
                        }
                    }

                    request["ExpressionAttributeValues"] = TypedItem(given);
                }
            }

            if (operation == GetItemOperation && Values.Get(p, "consistent") is { } consistent)
            {
                request["ConsistentRead"] = bool.TryParse(consistent, out var on) ? on : throw new InvalidOperationException($"'{consistent}' is not true or false (consistent).");
            }

            if (operation == QueryOperation && Values.Get(p, "descending") is { } descending)
            {
                request["ScanIndexForward"] = bool.TryParse(descending, out var down) ? !down : throw new InvalidOperationException($"'{descending}' is not true or false (descending).");
            }

            if (operation is QueryOperation or ScanOperation && Values.Get(p, "index") is { } index)
            {
                request["IndexName"] = TableName.IsMatch(index) ? index : throw new InvalidOperationException($"'{index}' is not an index's name - letters, digits, _, - and ., 3 to 255.");
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            switch (operation)
            {
                case PutItemOperation:
                    request["ReturnValues"] = "ALL_OLD";
                    await session.CallAsync("PutItem", request, cancellationToken);
                    yield return Record(request["Item"] as JsonObject);
                    yield break;
                case GetItemOperation:
                    {
                        var found = (await session.CallAsync("GetItem", request, cancellationToken))?["Item"] as JsonObject;
                        if (found is not null)
                        {
                            yield return Record(found);
                        }

                        yield break;
                    }

                case UpdateItemOperation:
                case DeleteItemOperation:
                    {
                        request["ReturnValues"] = operation == UpdateItemOperation ? "ALL_NEW" : "ALL_OLD";
                        var answer = await session.CallAsync(operation == UpdateItemOperation ? "UpdateItem" : "DeleteItem", request, cancellationToken);
                        if (answer?["Attributes"] is JsonObject attributes)
                        {
                            yield return Record(attributes);
                        }

                        yield break;
                    }

                case QueryOperation:
                case ScanOperation:
                    {
                        var read = 0;
                        JsonNode? next = null;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (request["FilterExpression"] is null)
                            {
                                request["Limit"] = Math.Min(1000, max - read); // a filter leaves Limit unset: it counts what is read, not what is kept
                            }

                            if (next is not null)
                            {
                                request["ExclusiveStartKey"] = next.DeepClone();
                            }

                            var page = await session.CallAsync(operation == QueryOperation ? "Query" : "Scan", request, cancellationToken);
                            foreach (var item in (page?["Items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = page?["LastEvaluatedKey"] is JsonObject { Count: > 0 } last ? last : null;
                        }
                        while (next is not null);

                        yield break;
                    }

                case TablesOperation:
                    {
                        var read = 0;
                        string? next = null;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var asked = new JsonObject { ["Limit"] = Math.Min(100, max - read) };
                            if (next is not null)
                            {
                                asked["ExclusiveStartTableName"] = next;
                            }

                            var page = await session.CallAsync("ListTables", asked, cancellationToken);
                            foreach (var name in (page?["TableNames"] as JsonArray ?? new JsonArray()).Select(Text).OfType<string>())
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = name });
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["LastEvaluatedTableName"]) is { Length: > 0 } more ? more : null;
                        }
                        while (next is not null);

                        yield break;
                    }

                default:
                    {
                        var described = (await session.CallAsync("DescribeTable", request, cancellationToken))?["Table"] as JsonObject ?? new JsonObject();
                        var keys = (described["KeySchema"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                        string? Key(string type) => keys.Where(part => Text(part["KeyType"]) == type).Select(part => Text(part["AttributeName"])).FirstOrDefault();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = Text(described["TableName"]),
                            ["status"] = Text(described["TableStatus"]),
                            ["partition_key"] = Key("HASH"),
                            ["sort_key"] = Key("RANGE"),
                            ["item_count"] = described["ItemCount"] is JsonValue count && count.TryGetValue<long>(out var items) ? items : null,
                            ["size_bytes"] = described["TableSizeBytes"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : null,
                            ["arn"] = Text(described["TableArn"]),
                            ["billing"] = Text(described["BillingModeSummary"]?["BillingMode"]) ?? "PROVISIONED",
                        });
                        yield break;
                    }
            }
        }

        // A plain JSON object as DynamoDB's attribute values: a string S, a number N (as written), true or false BOOL, null NULL,
        // an object M, an array L.
        private static JsonObject TypedItem(JsonObject plain) => new(plain.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)Typed(pair.Value))));

        private static JsonObject Typed(JsonNode? value) => value switch
        {
            null => new JsonObject { ["NULL"] = true },
            JsonObject map => new JsonObject { ["M"] = TypedItem(map) },
            JsonArray list => new JsonObject { ["L"] = new JsonArray(list.Select(item => (JsonNode?)Typed(item)).ToArray()) },
            JsonValue text when text.TryGetValue<string>(out var s) => new JsonObject { ["S"] = s },
            JsonValue flag when flag.TryGetValue<bool>(out var b) => new JsonObject { ["BOOL"] = b },
            _ => new JsonObject { ["N"] = value.ToJsonString() },
        };

        // DynamoDB's attribute value as plain JSON: a number a number (whole, or as written when it is not), a set an array, binary
        // its base64.
        private static JsonNode? Plain(JsonNode? typed)
        {
            if (typed is not JsonObject { Count: 1 } one)
            {
                return null;
            }

            var (type, value) = one.First();
            return type switch
            {
                "S" or "B" => (JsonNode?)Text(value),
                "N" => Number(Text(value)),
                "BOOL" => value is JsonValue flag && flag.TryGetValue<bool>(out var b) ? JsonValue.Create(b) : null,
                "NULL" => null,
                "M" => new JsonObject((value as JsonObject ?? new JsonObject()).Select(pair => KeyValuePair.Create(pair.Key, Plain(pair.Value)))),
                "L" => new JsonArray((value as JsonArray ?? new JsonArray()).Select(Plain).ToArray()),
                "SS" or "BS" => new JsonArray((value as JsonArray ?? new JsonArray()).Select(item => (JsonNode?)Text(item)).ToArray()),
                "NS" => new JsonArray((value as JsonArray ?? new JsonArray()).Select(item => Number(Text(item))).ToArray()),
                _ => null,
            };
        }

        private static JsonNode? Number(string? text) =>
            text is null ? null
            : long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole)
            : decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? JsonValue.Create(real)
            : JsonValue.Create(text);

        // An item as a record: its attributes by their names, a map or a list as its JSON.
        private static ConnectorRecord Record(JsonObject? item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, typed) in item ?? new JsonObject())
            {
                fields[name] = Plain(typed) switch
                {
                    null => null,
                    JsonValue value when value.TryGetValue<string>(out var s) => s,
                    JsonValue value when value.TryGetValue<bool>(out var b) => b,
                    JsonValue value when value.TryGetValue<long>(out var l) => l,
                    JsonValue value when value.TryGetValue<decimal>(out var d) => d,
                    { } other => other.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} not a JSON object - {example}.");
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: DynamoDB's address in the region, its JSON protocol's target and content type, every request
        // signed; a failure thrown with its status, AWS's error type and its message.
        private sealed class Session
        {
            private readonly string api;
            private readonly string accessKey;
            private readonly string secretKey;
            private readonly TimeProvider time;

            private Session(string api, string region, string accessKey, string secretKey, TimeProvider time, HttpClient client)
            {
                this.api = api;
                Region = region;
                this.accessKey = accessKey;
                this.secretKey = secretKey;
                this.time = time;
                Client = client;
            }

            public string Region { get; }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
            {
                var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the AWS region - eu-north-1.");
                if (!AwsDynamoDbConnector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not an AWS region - eu-north-1.");
                }

                var api = Values.BaseUrl(endpoint, $"https://dynamodb.{region}.amazonaws.com");
                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Amazon DynamoDB connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Amazon DynamoDB connection has no secret key.");
                return new Session(api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> CallAsync(string action, JsonObject body, CancellationToken cancellationToken)
            {
                var payload = Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, api + "/") { Content = new ByteArrayContent(payload) };
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.0");
                    request.Headers.TryAddWithoutValidation("X-Amz-Target", "DynamoDB_20120810." + action);
                    AwsSigV4.Sign(request, payload, Region, "dynamodb", accessKey, secretKey, null, time.GetUtcNow());
                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Amazon DynamoDB answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var type = Text(json?["__type"]) is { } named ? named[(named.LastIndexOf('#') + 1)..] : null;
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["Message"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
