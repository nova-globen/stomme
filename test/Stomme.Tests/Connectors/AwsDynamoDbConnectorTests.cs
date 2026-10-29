#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AwsDynamoDb;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Amazon DynamoDB (n8n's AWS DynamoDB node: an item written, got, deleted and read many): every call DynamoDB's
    /// JSON protocol - its target and content type - signed with AWS Signature Version 4 for DynamoDB in the connection's
    /// region; an item written as plain JSON and typed on the wire - a string S, a number N as written, a flag BOOL, null NULL,
    /// an object M, an array L - on a condition with its names and values; got, updated and deleted by its key, each its values
    /// plain again (a set an array, a map its JSON); a query on an index in either order and a scan with a filter, page by page
    /// by the last key, a page no bigger than what is left - none asked for with a filter; the tables page by page and one
    /// described; what could go wrong refused before anything is signed or sent; AWS's error type and message; a test naming
    /// the tables seen.
    /// </summary>
    public sealed class AwsDynamoDbConnectorTests
    {
        private const string Api = "https://dynamodb.eu-north-1.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDEXAMPLE", ["secret-key"] = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" };

        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-north-1" };

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static AwsDynamoDbConnector Connector() => new(new FixedClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero)));

        private sealed class Dynamo : HttpMessageHandler
        {
            public List<(string Target, string? Authorization, string? ContentType, string Body, string Url)> Requests { get; } = new();

            public Func<string, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var target = request.Headers.TryGetValues("X-Amz-Target", out var targets) ? targets.Single() : "";
                Requests.Add((target, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, request.Content?.Headers.ContentType?.ToString(), body, request.RequestUri!.AbsoluteUri));
                var (status, answer) = Answer(target, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/x-amz-json-1.0") };
            }
        }

        private static ConnectorExtractContext Context(Dynamo dynamo, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(dynamo), Api, operation, parameters, settings: Settings, credentials: Credentials);

        private const string Stored = """{"pk":{"S":"order#1042"},"sk":{"N":"1"},"total":{"N":"99.50"},"paid":{"BOOL":true},"note":{"NULL":true},"lines":{"L":[{"M":{"sku":{"S":"fika"},"qty":{"N":"2"}}}]},"tags":{"SS":["new","café"]},"sizes":{"NS":["1","2.5"]},"big":{"N":"12345678901234567890"},"huge":{"N":"123456789012345678901234567890"}}""";

        [Fact]
        public async Task An_item_is_written_typed_on_the_wire_and_got_updated_and_deleted_plain()
        {
            var dynamo = new Dynamo
            {
                Answer = (target, _) => (HttpStatusCode.OK, target switch
                {
                    "DynamoDB_20120810.GetItem" => "{\"Item\":" + Stored + "}",
                    "DynamoDB_20120810.UpdateItem" or "DynamoDB_20120810.DeleteItem" => "{\"Attributes\":" + Stored + "}",
                    _ => "{}",
                }),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(dynamo, operation, parameters));

            var written = Assert.Single(await Run(AwsDynamoDbConnector.PutItemOperation, new()
            {
                ["table"] = "Orders", ["item"] = "{\"pk\": \"order#1042\", \"total\": 99.50, \"paid\": true, \"note\": null, \"lines\": [{\"sku\": \"fika\", \"qty\": 2}], \"who\": {\"name\": \"Café\"}}",
                ["condition"] = "attribute_not_exists(#pk) OR #s = :s", ["names"] = "{\"#pk\": \"pk\", \"#s\": \"status\"}", ["values"] = "{\":s\": \"draft\"}", ["projection"] = "pk",
            }));
            var call = Assert.Single(dynamo.Requests);
            Assert.Equal(("DynamoDB_20120810.PutItem", "application/x-amz-json-1.0"), (call.Target, call.ContentType));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-north-1/dynamodb/aws4_request, SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature=", call.Authorization, StringComparison.Ordinal);
            Assert.Equal("{\"TableName\":\"Orders\",\"Item\":{\"pk\":{\"S\":\"order#1042\"},\"total\":{\"N\":\"99.50\"},\"paid\":{\"BOOL\":true},\"note\":{\"NULL\":true},\"lines\":{\"L\":[{\"M\":{\"sku\":{\"S\":\"fika\"},\"qty\":{\"N\":\"2\"}}}]},\"who\":{\"M\":{\"name\":{\"S\":\"Café\"}}}},"
                + "\"ConditionExpression\":\"attribute_not_exists(#pk) OR #s = :s\",\"ExpressionAttributeNames\":{\"#pk\":\"pk\",\"#s\":\"status\"},\"ExpressionAttributeValues\":{\":s\":{\"S\":\"draft\"}},\"ReturnValues\":\"ALL_OLD\"}", call.Body); // a number as written
            Assert.Equal(("order#1042", 99.50m, true, "[{\"sku\":\"fika\",\"qty\":2}]", "{\"name\":\"Café\"}"), ((string?)written.Fields["pk"], (decimal?)written.Fields["total"], (bool?)written.Fields["paid"], (string?)written.Fields["lines"], (string?)written.Fields["who"]));
            Assert.Null(written.Fields["note"]);

            var item = Assert.Single(await Run(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"order#1042\", \"sk\": 1}", ["projection"] = "pk, total, #s", ["names"] = "{\"#s\": \"status\"}", ["consistent"] = "true", ["values"] = "{\":x\": 1}", ["condition"] = "attribute_exists(pk)" })).Fields;
            Assert.Equal("{\"TableName\":\"Orders\",\"Key\":{\"pk\":{\"S\":\"order#1042\"},\"sk\":{\"N\":\"1\"}},\"ProjectionExpression\":\"pk, total, #s\",\"ExpressionAttributeNames\":{\"#s\":\"status\"},\"ConsistentRead\":true}", dynamo.Requests[^1].Body); // no values on a get
            Assert.Equal("[1,2.5]", (string?)item["sizes"]); // a set of numbers an array of numbers
            Assert.Equal(("order#1042", 1L, 99.50m, "[\"new\",\"café\"]", 12345678901234567890m, "123456789012345678901234567890"), ((string?)item["pk"], (long?)item["sk"], (decimal?)item["total"], (string?)item["tags"], (decimal?)item["big"], (string?)item["huge"])); // past a long a decimal, past that as written

            dynamo.Answer = (target, _) => (HttpStatusCode.OK, target == "DynamoDB_20120810.GetItem" ? "{}" : "{\"Attributes\":" + Stored + "}");
            Assert.Empty(await Run(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"none\"}" })); // no item, no record
            var updated = Assert.Single(await Run(AwsDynamoDbConnector.UpdateItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"order#1042\", \"sk\": 1}", ["update"] = "SET #s = :s ADD visits :one", ["names"] = "{\"#s\": \"status\"}", ["values"] = "{\":s\": \"paid\", \":one\": 1}" }));
            Assert.Equal("DynamoDB_20120810.UpdateItem", dynamo.Requests[^1].Target);
            Assert.Equal("{\"TableName\":\"Orders\",\"Key\":{\"pk\":{\"S\":\"order#1042\"},\"sk\":{\"N\":\"1\"}},\"UpdateExpression\":\"SET #s = :s ADD visits :one\",\"ExpressionAttributeNames\":{\"#s\":\"status\"},\"ExpressionAttributeValues\":{\":s\":{\"S\":\"paid\"},\":one\":{\"N\":\"1\"}},\"ReturnValues\":\"ALL_NEW\"}", dynamo.Requests[^1].Body);
            Assert.Equal("order#1042", (string?)updated.Fields["pk"]);
            var deleted = Assert.Single(await Run(AwsDynamoDbConnector.DeleteItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"order#1042\", \"sk\": 1}", ["condition"] = "attribute_exists(pk)" }));
            Assert.Equal(("DynamoDB_20120810.DeleteItem", "{\"TableName\":\"Orders\",\"Key\":{\"pk\":{\"S\":\"order#1042\"},\"sk\":{\"N\":\"1\"}},\"ConditionExpression\":\"attribute_exists(pk)\",\"ReturnValues\":\"ALL_OLD\"}"), (dynamo.Requests[^1].Target, dynamo.Requests[^1].Body));
            Assert.Equal(true, (bool?)deleted.Fields["paid"]); // the item as it was
        }

        [Fact]
        public async Task Items_are_queried_and_scanned_page_by_page_and_the_tables_listed_and_described()
        {
            var dynamo = new Dynamo
            {
                Answer = (target, body) => (HttpStatusCode.OK, target switch
                {
                    "DynamoDB_20120810.Query" or "DynamoDB_20120810.Scan" => body.Contains("ExclusiveStartKey", StringComparison.Ordinal)
                        ? "{\"Items\":[{\"pk\":{\"S\":\"c\"}}],\"Count\":1}"
                        : "{\"Items\":[{\"pk\":{\"S\":\"a\"}},{\"pk\":{\"S\":\"b\"}}],\"Count\":2,\"LastEvaluatedKey\":{\"pk\":{\"S\":\"b\"}}}",
                    "DynamoDB_20120810.ListTables" => body.Contains("ExclusiveStartTableName", StringComparison.Ordinal) ? "{\"TableNames\":[\"Orders\"]}" : "{\"TableNames\":[\"Customers\",\"Lines\"],\"LastEvaluatedTableName\":\"Lines\"}",
                    "DynamoDB_20120810.DescribeTable" => "{\"Table\":{\"TableName\":\"Orders\",\"TableStatus\":\"ACTIVE\",\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"},{\"AttributeName\":\"sk\",\"KeyType\":\"RANGE\"}],\"ItemCount\":1042,\"TableSizeBytes\":99500,\"TableArn\":\"arn:aws:dynamodb:eu-north-1:123456789012:table/Orders\",\"BillingModeSummary\":{\"BillingMode\":\"PAY_PER_REQUEST\"}}}",
                    _ => "{}",
                }),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(dynamo, operation, parameters));

            var found = await Run(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders", ["index"] = "by-customer", ["key_condition"] = "customer = :c", ["values"] = "{\":c\": \"ada\"}", ["descending"] = "true", ["max"] = "3" });
            Assert.Equal(new[] { "a", "b", "c" }, found.Select(record => (string?)record.Fields["pk"]));
            Assert.Equal(new[]
            {
                "{\"TableName\":\"Orders\",\"KeyConditionExpression\":\"customer = :c\",\"ExpressionAttributeValues\":{\":c\":{\"S\":\"ada\"}},\"ScanIndexForward\":false,\"IndexName\":\"by-customer\",\"Limit\":3}",
                "{\"TableName\":\"Orders\",\"KeyConditionExpression\":\"customer = :c\",\"ExpressionAttributeValues\":{\":c\":{\"S\":\"ada\"}},\"ScanIndexForward\":false,\"IndexName\":\"by-customer\",\"Limit\":1,\"ExclusiveStartKey\":{\"pk\":{\"S\":\"b\"}}}",
            }, dynamo.Requests.Select(request => request.Body)); // a page no bigger than what is left, the next from the last key
            Assert.Equal(2, (await Run(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders", ["key_condition"] = "pk = :p", ["values"] = "{\":p\": \"a\"}", ["max"] = "2" })).Count);
            Assert.Equal(3, dynamo.Requests.Count); // no page asked for past the most
            await Run(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders", ["key_condition"] = "pk = :p", ["filter"] = "total > :t", ["values"] = "{\":p\": \"a\", \":t\": 100}", ["max"] = "1" });
            Assert.Equal("{\"TableName\":\"Orders\",\"KeyConditionExpression\":\"pk = :p\",\"FilterExpression\":\"total > :t\",\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":t\":{\"N\":\"100\"}}}", dynamo.Requests[^1].Body); // a query's filter, no Limit with it

            var scanned = await Run(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["filter"] = "#s = :s", ["names"] = "{\"#s\": \"status\"}", ["values"] = "{\":s\": \"paid\"}" });
            Assert.Equal(3, scanned.Count);
            Assert.Equal(new[]
            {
                ("DynamoDB_20120810.Scan", "{\"TableName\":\"Orders\",\"FilterExpression\":\"#s = :s\",\"ExpressionAttributeNames\":{\"#s\":\"status\"},\"ExpressionAttributeValues\":{\":s\":{\"S\":\"paid\"}}}"),
                ("DynamoDB_20120810.Scan", "{\"TableName\":\"Orders\",\"FilterExpression\":\"#s = :s\",\"ExpressionAttributeNames\":{\"#s\":\"status\"},\"ExpressionAttributeValues\":{\":s\":{\"S\":\"paid\"}},\"ExclusiveStartKey\":{\"pk\":{\"S\":\"b\"}}}"),
            }, dynamo.Requests.TakeLast(2).Select(request => (request.Target, request.Body))); // a filter asks for no Limit
            await Run(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders" });
            Assert.Equal("{\"TableName\":\"Orders\",\"Limit\":1000}", dynamo.Requests[^2].Body); // 1,000 at most a page

            var tables = await Run(AwsDynamoDbConnector.TablesOperation, new());
            Assert.Equal(new[] { "Customers", "Lines", "Orders" }, tables.Select(table => (string?)table.Fields["name"]));
            Assert.Equal(new[] { "{\"Limit\":100}", "{\"Limit\":100,\"ExclusiveStartTableName\":\"Lines\"}" }, dynamo.Requests.TakeLast(2).Select(request => request.Body));
            Assert.Equal(1, (await Run(AwsDynamoDbConnector.TablesOperation, new() { ["max"] = "1" })).Count);
            Assert.Equal("{\"Limit\":1}", dynamo.Requests[^1].Body); // a page no bigger than what is left
            var described = Assert.Single(await Run(AwsDynamoDbConnector.DescribeTableOperation, new() { ["table"] = "Orders" })).Fields;
            Assert.Equal(("{\"TableName\":\"Orders\"}", "Orders", "ACTIVE", "pk", "sk", 1042L, 99500L, "PAY_PER_REQUEST"), (dynamo.Requests[^1].Body, (string?)described["name"], (string?)described["status"], (string?)described["partition_key"], (string?)described["sort_key"], (long?)described["item_count"], (long?)described["size_bytes"], (string?)described["billing"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed_or_sent_and_awss_errors_are_its_words()
        {
            var dynamo = new Dynamo();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(dynamo, operation, parameters)))).Message;

            Assert.Equal("Give the table - its name.", await Refused(AwsDynamoDbConnector.ScanOperation, new()));
            Assert.Equal("'my table' is not a table's name - letters, digits, _, - and ., 3 to 255.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "my table" }));
            Assert.Equal("Give the item - a JSON object.", await Refused(AwsDynamoDbConnector.PutItemOperation, new() { ["table"] = "Orders" }));
            Assert.Equal("The item is not a JSON object - {\"pk\": \"order#1042\"}.", await Refused(AwsDynamoDbConnector.PutItemOperation, new() { ["table"] = "Orders", ["item"] = "pk=1" }));
            Assert.Equal("Give the item's key - a JSON object.", await Refused(AwsDynamoDbConnector.DeleteItemOperation, new() { ["table"] = "Orders" }));
            Assert.Equal("A key is its partition key's value and its sort key's, if the table has one - strings or numbers.", await Refused(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"a\", \"sk\": 1, \"x\": 2}" }));
            Assert.Equal("A key is its partition key's value and its sort key's, if the table has one - strings or numbers.", await Refused(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": {\"a\": 1}}" }));
            Assert.Equal("A key is its partition key's value and its sort key's, if the table has one - strings or numbers.", await Refused(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{}" }));
            Assert.Equal("Give the update expression - SET #s = :s.", await Refused(AwsDynamoDbConnector.UpdateItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"a\"}" }));
            Assert.Equal("Give the key condition - pk = :pk.", await Refused(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders" }));
            Assert.Equal("The filter is one line of up to 4,096 characters.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["filter"] = "a = :a\nOR b = :b" }));
            Assert.Equal("The key condition is one line of up to 4,096 characters.", await Refused(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders", ["key_condition"] = new string('k', 4097) }));
            Assert.Equal("'s' is not a name's placeholder - #, then letters, digits and _.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["names"] = "{\"s\": \"status\"}" }));
            Assert.Equal("The name #s is not an attribute's name.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["names"] = "{\"#s\": 1}" }));
            Assert.Equal("'s' is not a value's placeholder - :, then letters, digits and _.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["values"] = "{\"s\": \"paid\"}" }));
            Assert.Equal("The values are not a JSON object - {\":s\": \"paid\"}.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["values"] = "[1]" }));
            Assert.Equal("'yes' is not true or false (consistent).", await Refused(AwsDynamoDbConnector.GetItemOperation, new() { ["table"] = "Orders", ["key"] = "{\"pk\": \"a\"}", ["consistent"] = "yes" }));
            Assert.Equal("'down' is not true or false (descending).", await Refused(AwsDynamoDbConnector.QueryOperation, new() { ["table"] = "Orders", ["key_condition"] = "pk = :p", ["descending"] = "down" }));
            Assert.Equal("'by customer' is not an index's name - letters, digits, _, - and ., 3 to 255.", await Refused(AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders", ["index"] = "by customer" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(AwsDynamoDbConnector.TablesOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Amazon DynamoDB has no operation 'create_table' - put_item, get_item,", await Refused("create_table", new()));
            Assert.Empty(dynamo.Requests); // nothing signed or sent

            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(dynamo), Api, AwsDynamoDbConnector.TablesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["access-key"] = "AKIDEXAMPLE" })));
            Assert.Equal("The Amazon DynamoDB connection has no secret key.", keyless.Message);
            var nameless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(dynamo), Api, AwsDynamoDbConnector.TablesOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["secret-key"] = "s" })));
            Assert.Equal("The Amazon DynamoDB connection has no access key.", nameless.Message);
            var noRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(dynamo), Api, AwsDynamoDbConnector.TablesOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string> { ["region"] = "Stockholm" }, credentials: Credentials)));
            Assert.Equal("'Stockholm' is not an AWS region - eu-north-1.", noRegion.Message);
            Assert.Empty(dynamo.Requests);

            var failed = new Dynamo { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"__type":"com.amazonaws.dynamodb.v20120810#ConditionalCheckFailedException","message":"The conditional request failed"}""") };
            Assert.Equal("HTTP 400 ConditionalCheckFailedException: The conditional request failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(failed, AwsDynamoDbConnector.PutItemOperation, new() { ["table"] = "Orders", ["item"] = "{\"pk\": \"a\"}", ["condition"] = "attribute_not_exists(pk)" })))).Message);
            var capital = new Dynamo { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"__type":"com.amazon.coral.validate#ValidationException","Message":"One or more parameter values were invalid"}""") };
            Assert.Equal("HTTP 400 ValidationException: One or more parameter values were invalid", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(capital, AwsDynamoDbConnector.ScanOperation, new() { ["table"] = "Orders" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_tables_seen()
        {
            var dynamo = new Dynamo { Answer = (_, _) => (HttpStatusCode.OK, "{\"TableNames\":[\"Customers\",\"Orders\"]}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(dynamo), "", settings: Settings, credentials: Credentials));
            Assert.Equal((true, "DynamoDB in eu-north-1: 2 tables seen."), (tested.Success, tested.Message));
            Assert.Equal(("DynamoDB_20120810.ListTables", "{\"Limit\":100}", "https://dynamodb.eu-north-1.amazonaws.com/"), (dynamo.Requests[0].Target, dynamo.Requests[0].Body, dynamo.Requests[0].Url)); // the region's own address when none is given
            var one = new Dynamo { Answer = (_, _) => (HttpStatusCode.OK, "{\"TableNames\":[\"Orders\"]}") };
            Assert.Equal("DynamoDB in eu-north-1: one table seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", settings: Settings, credentials: Credentials))).Message);
            var many = new Dynamo { Answer = (_, _) => (HttpStatusCode.OK, "{\"TableNames\":[\"Orders\"],\"LastEvaluatedTableName\":\"Orders\"}") };
            Assert.Equal("DynamoDB in eu-north-1: 1 or more tables seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(many), "", settings: Settings, credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "awsdynamodb"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(8, Connector().Operations.Count);
        }
    }
}
