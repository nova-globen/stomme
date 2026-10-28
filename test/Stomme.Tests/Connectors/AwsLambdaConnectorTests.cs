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
using Stomme.Modules.Connectors.AwsLambda;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - AWS Lambda (n8n's AWS Lambda node: a function invoked): every call signed with AWS Signature Version 4 for Lambda
    /// in the connection's region; a function invoked by its name or its ARN - its path escaped - with a version or an alias and
    /// a JSON payload, waited for: its answer an object's fields or a value as result, a text not JSON as itself, a function that
    /// failed thrown with its error; one queued as an event; the functions page by page by Lambda's marker and one's
    /// configuration, its time in UTC; what could go wrong refused before anything is signed or sent; AWS's error type and
    /// message; a test naming the functions seen.
    /// </summary>
    public sealed class AwsLambdaConnectorTests
    {
        private const string Api = "https://lambda.eu-north-1.example.test";

        private const string Arn = "arn:aws:lambda:eu-north-1:123456789012:function:resize-images";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDEXAMPLE", ["secret-key"] = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" };

        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-north-1" };

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static AwsLambdaConnector Connector() => new(new FixedClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero)));

        private sealed class Lambda : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Invocation, string? Body, string? ContentType)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string, string?)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            public string? ErrorType { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, request.Headers.TryGetValues("X-Amz-Invocation-Type", out var type) ? type.Single() : null, body, request.Content?.Headers.ContentType?.ToString()));
                var (status, answer, failure) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (failure is not null)
                {
                    response.Headers.TryAddWithoutValidation("X-Amz-Function-Error", failure);
                }

                if (ErrorType is not null)
                {
                    response.Headers.TryAddWithoutValidation("x-amzn-ErrorType", ErrorType);
                }

                return response;
            }
        }

        private static ConnectorExtractContext Context(Lambda lambda, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(lambda), Api, operation, parameters, settings: Settings, credentials: Credentials);

        [Fact]
        public async Task A_function_is_invoked_signed_and_its_answer_read_or_queued_as_an_event()
        {
            var lambda = new Lambda { Answer = (_, _) => (HttpStatusCode.OK, """{"width":800,"ratio":1.5,"ok":true,"name":"photo.jpg","sizes":[1,2],"meta":{"a":1},"none":null}""", null) };
            var answered = Assert.Single(await Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = Arn, ["qualifier"] = "$LATEST", ["payload"] = "{\"image\": \"café.jpg\"}" }))).Fields;

            var call = Assert.Single(lambda.Requests);
            Assert.Equal(("POST", $"{Api}/2015-03-31/functions/arn%3Aaws%3Alambda%3Aeu-north-1%3A123456789012%3Afunction%3Aresize-images/invocations?Qualifier=%24LATEST", "RequestResponse", "{\"image\": \"café.jpg\"}"), (call.Method.Method, call.Url, call.Invocation, call.Body)); // the ARN escaped in the path, the payload as written
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-north-1/lambda/aws4_request, SignedHeaders=content-type;host;x-amz-date;x-amz-invocation-type, Signature=", call.Authorization, StringComparison.Ordinal);
            Assert.Equal("application/json", call.ContentType);
            Assert.Equal((800L, 1.5, true, "photo.jpg", "[1,2]", "{\"a\":1}"), ((long?)answered["width"], (double?)answered["ratio"], (bool?)answered["ok"], (string?)answered["name"], (string?)answered["sizes"], (string?)answered["meta"]));
            Assert.Null(answered["none"]);

            lambda.Answer = (_, _) => (HttpStatusCode.OK, "[1,2,3]", null);
            Assert.Equal("[1,2,3]", (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images" }))).Fields["result"]); // a value its result
            Assert.Equal(($"{Api}/2015-03-31/functions/resize-images/invocations", "{}"), (lambda.Requests[^1].Url, lambda.Requests[^1].Body)); // no payload sends {}
            lambda.Answer = (_, _) => (HttpStatusCode.OK, "\"done\"", null);
            Assert.Equal("done", (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images" }))).Fields["result"]);
            lambda.Answer = (_, _) => (HttpStatusCode.OK, "resized 3 images", null);
            Assert.Equal("resized 3 images", (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images" }))).Fields["result"]); // a text not JSON as itself

            lambda.Answer = (_, _) => (HttpStatusCode.Accepted, "", null);
            var queued = Assert.Single(await Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images", ["qualifier"] = "live", ["event"] = "true" }))).Fields;
            Assert.Equal(("Event", $"{Api}/2015-03-31/functions/resize-images/invocations?Qualifier=live"), (lambda.Requests[^1].Invocation, lambda.Requests[^1].Url));
            Assert.Equal(("resize-images", true), ((string?)queued["function"], (bool?)queued["queued"]));

            lambda.Answer = (_, _) => (HttpStatusCode.OK, """{"errorMessage":"Image too large","errorType":"Error","trace":[]}""", "Unhandled");
            Assert.Equal("The function failed (Unhandled): Image too large", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images" })))).Message);
            lambda.Answer = (_, _) => (HttpStatusCode.OK, "", "Handled");
            Assert.Equal("The function failed (Handled).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(lambda, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize-images" })))).Message);
        }

        [Fact]
        public async Task Functions_are_listed_page_by_page_and_one_read()
        {
            const string Configuration = """{"FunctionName":"resize-images","FunctionArn":"arn:aws:lambda:eu-north-1:123456789012:function:resize-images","Runtime":"nodejs20.x","Handler":"index.handler","Description":"Resizes","MemorySize":512,"Timeout":30,"CodeSize":1042,"Version":"$LATEST","State":"Active","PackageType":"Zip","LastModified":"2026-10-01T12:00:00.000+0200"}""";
            var lambda = new Lambda
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("/functions/?", StringComparison.Ordinal)
                    ? url.Contains("Marker=", StringComparison.Ordinal) ? "{\"Functions\":[" + Configuration.Replace("resize-images", "c", StringComparison.Ordinal) + "]}" : "{\"Functions\":[" + Configuration + "," + Configuration.Replace("resize-images", "b", StringComparison.Ordinal) + "],\"NextMarker\":\"m+1/\"}"
                    : "{\"Configuration\":" + Configuration + ",\"Code\":{\"Location\":\"https://signed.example.test/code.zip\"}}", null),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(lambda, operation, parameters));

            var functions = await Run(AwsLambdaConnector.FunctionsOperation, new() { ["max"] = "3" });
            Assert.Equal(new[] { "resize-images", "b", "c" }, functions.Select(function => (string?)function.Fields["name"]));
            Assert.Equal(new[] { $"{Api}/2015-03-31/functions/?MaxItems=3", $"{Api}/2015-03-31/functions/?MaxItems=1&Marker=m%2B1%2F" }, lambda.Requests.Select(request => request.Url)); // a page no bigger than what is left
            Assert.Equal(2, (await Run(AwsLambdaConnector.FunctionsOperation, new() { ["max"] = "2" })).Count);
            Assert.Equal(3, lambda.Requests.Count); // no page asked for past the most
            await Run(AwsLambdaConnector.FunctionsOperation, new());
            Assert.Equal($"{Api}/2015-03-31/functions/?MaxItems=50", lambda.Requests[^2].Url); // 50 a page

            var function = Assert.Single(await Run(AwsLambdaConnector.FunctionOperation, new() { ["function"] = "resize-images", ["qualifier"] = "3" })).Fields;
            Assert.Equal(("GET", $"{Api}/2015-03-31/functions/resize-images?Qualifier=3"), (lambda.Requests[^1].Method.Method, lambda.Requests[^1].Url));
            Assert.Equal(("resize-images", "nodejs20.x", "index.handler", 512L, 30L, 1042L, "Active", "2026-10-01T10:00:00Z"), ((string?)function["name"], (string?)function["runtime"], (string?)function["handler"], (long?)function["memory_mb"], (long?)function["timeout_seconds"], (long?)function["code_size"], (string?)function["state"], (string?)function["last_modified"])); // its time in UTC
            Assert.False(function.ContainsKey("location")); // the code's signed address never read into a run
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed_or_sent_and_awss_errors_are_its_words()
        {
            var lambda = new Lambda();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(lambda, operation, parameters)))).Message;

            Assert.Equal("Give the function - its name or ARN.", await Refused(AwsLambdaConnector.InvokeOperation, new()));
            Assert.Equal("'resize images' is not a function - its name, letters, digits, - and _, or its ARN.", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "resize images" }));
            Assert.Equal("'../admin' is not a function - its name, letters, digits, - and _, or its ARN.", await Refused(AwsLambdaConnector.FunctionOperation, new() { ["function"] = "../admin" }));
            Assert.Equal("'v 1' is not a version or an alias - $LATEST, 3 or live.", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f", ["qualifier"] = "v 1" }));
            Assert.Equal("The payload is not JSON - {\"image\": \"photo.jpg\"}.", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f", ["payload"] = "image=photo.jpg" }));
            Assert.Equal("The payload is larger than 6 MB.", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f", ["payload"] = "\"" + new string('p', 6 * 1024 * 1024) + "\"" }));
            Assert.Equal("The payload is larger than 256 KB, an event's most.", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f", ["event"] = "true", ["payload"] = "\"" + new string('p', 256 * 1024) + "\"" }));
            Assert.Equal("'later' is not true or false (event).", await Refused(AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f", ["event"] = "later" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(AwsLambdaConnector.FunctionsOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("AWS Lambda has no operation 'delete_function' - invoke, functions or function.", await Refused("delete_function", new()));
            Assert.Empty(lambda.Requests); // nothing signed or sent

            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(lambda), Api, AwsLambdaConnector.FunctionsOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["access-key"] = "AKIDEXAMPLE" })));
            Assert.Equal("The AWS Lambda connection has no secret key.", keyless.Message);
            var noRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(lambda), Api, AwsLambdaConnector.FunctionsOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string>(), credentials: Credentials)));
            Assert.Equal("Give the AWS region - eu-north-1.", noRegion.Message);
            var wrongRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(lambda), Api, AwsLambdaConnector.FunctionsOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string> { ["region"] = "Stockholm" }, credentials: Credentials)));
            Assert.Equal("'Stockholm' is not an AWS region - eu-north-1.", wrongRegion.Message);
            Assert.Empty(lambda.Requests);

            var missing = new Lambda { Answer = (_, _) => (HttpStatusCode.NotFound, """{"Type":"User","message":"Function not found: arn:aws:lambda:eu-north-1:123456789012:function:gone"}""", null) };
            Assert.Equal("HTTP 404 User: Function not found: arn:aws:lambda:eu-north-1:123456789012:function:gone", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "gone" })))).Message);
            var invalid = new Lambda { ErrorType = "InvalidRequestContentException:http://internal.amazon.com/coral/com.amazonaws.lambda/", Answer = (_, _) => (HttpStatusCode.BadRequest, """{"Type":"User","message":"Could not parse request body into json"}""", null) };
            Assert.Equal("HTTP 400 InvalidRequestContentException: Could not parse request body into json", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(invalid, AwsLambdaConnector.InvokeOperation, new() { ["function"] = "f" })))).Message); // AWS's error type before the body's
            var silent = new Lambda { Answer = (_, _) => (HttpStatusCode.Forbidden, "", null) };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, AwsLambdaConnector.FunctionsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_functions_seen()
        {
            var lambda = new Lambda { Answer = (_, _) => (HttpStatusCode.OK, "{\"Functions\":[{\"FunctionName\":\"a\"},{\"FunctionName\":\"b\"}]}", null) };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(lambda), "", settings: Settings, credentials: Credentials));
            Assert.Equal((true, "Lambda in eu-north-1: 2 functions seen."), (tested.Success, tested.Message));
            Assert.Equal("https://lambda.eu-north-1.amazonaws.com/2015-03-31/functions/?MaxItems=50", lambda.Requests[0].Url); // the region's own address when none is given
            var many = new Lambda { Answer = (_, _) => (HttpStatusCode.OK, "{\"Functions\":[{\"FunctionName\":\"a\"}],\"NextMarker\":\"m\"}", null) };
            Assert.Equal("Lambda in eu-north-1: 1 or more functions seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(many), "", settings: Settings, credentials: Credentials))).Message);
            var one = new Lambda { Answer = (_, _) => (HttpStatusCode.OK, "{\"Functions\":[{\"FunctionName\":\"a\"}]}", null) };
            Assert.Equal("Lambda in eu-north-1: one function seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", settings: Settings, credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "awslambda"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(3, Connector().Operations.Count);
        }
    }
}
