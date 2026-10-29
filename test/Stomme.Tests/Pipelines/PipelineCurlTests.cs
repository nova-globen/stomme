#nullable enable
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's "Import cURL": a curl command read into the HTTP request step's inputs - as API documentation writes one,
    /// as a browser's "Copy as cURL" writes one (bash and Windows), and what the step cannot do the same way said.
    /// </summary>
    public sealed class PipelineCurlTests
    {
        [Fact]
        public void An_api_documentations_command_is_read_into_the_steps_inputs()
        {
            var request = PipelineCurl.Parse("""
                curl -X POST https://api.example.com/v1/orders \
                  -H "Content-Type: application/json" \
                  -H 'Authorization: Bearer sk_test_123' \
                  -H "X-Request-Id: 42" \
                  -d '{"item": "book", "qty": 2}' --max-time 15
                """, out var error)!;

            Assert.Null(error);
            Assert.Equal(("https://api.example.com/v1/orders", "POST", "application/json", "Bearer sk_test_123", 15), (request.Url, request.Method, request.ContentType, request.Authorization, request.TimeoutSeconds));
            Assert.Equal(new[] { "X-Request-Id: 42" }, request.Headers);
            Assert.Equal("""{"item": "book", "qty": 2}""", request.Body);
            Assert.Empty(request.Notes);
        }

        [Theory]
        [InlineData("curl https://example.com/a", "GET", null, null)]
        [InlineData("curl -d 'name=Ada' -d 'age=36' example.com/form", "POST", "name=Ada&age=36", "application/x-www-form-urlencoded")]
        [InlineData("curl -G -d 'q=books' --data-urlencode 'by=Ada Lovelace' https://example.com/search", "GET", null, null)]
        [InlineData("curl --json '{\"a\":1}' https://example.com/j", "POST", "{\"a\":1}", "application/json")]
        [InlineData("curl -I https://example.com/h", "HEAD", null, null)]
        [InlineData("curl --request=delete --url=https://example.com/x/1", "DELETE", null, null)]
        public void The_method_body_and_content_type_follow_curls_own_rules(string command, string method, string? body, string? contentType)
        {
            var request = PipelineCurl.Parse(command, out var error)!;
            Assert.Null(error);
            Assert.Equal((method, body, contentType), (request.Method, request.Body, request.ContentType));
        }

        [Fact]
        public void A_query_from_data_a_bare_host_basic_auth_and_a_browsers_windows_copy_are_read()
        {
            Assert.Equal("https://example.com/search?q=books&by=Ada%20Lovelace", PipelineCurl.Parse("curl -G -d 'q=books' --data-urlencode 'by=Ada Lovelace' https://example.com/search", out _)!.Url);
            Assert.Equal("https://example.com/form", PipelineCurl.Parse("curl -d x=1 example.com/form", out _)!.Url);
            Assert.Equal("Basic YWRhOnNlY3JldA==", PipelineCurl.Parse("curl -u ada:secret https://example.com", out _)!.Authorization);

            // Chrome's "Copy as cURL (cmd)" - carets continue the lines, doubled quotes.
            var windows = PipelineCurl.Parse("curl \"https://example.com/api\" ^\r\n  -H \"accept: application/json\" ^\r\n  -H \"user-agent: Test\" ^\r\n  --compressed", out var error)!;
            Assert.Null(error);
            Assert.Equal(("https://example.com/api", "GET"), (windows.Url, windows.Method));
            Assert.Equal(new[] { "accept: application/json", "user-agent: Test" }, windows.Headers);
        }

        [Fact]
        public void What_the_step_does_not_do_the_same_way_is_said_and_a_file_is_sent_from_the_pipeline_folder()
        {
            var upload = PipelineCurl.Parse("curl -k -F 'file=@invoices/march.pdf;type=application/pdf' -F 'note=March' -o out.json https://example.com/upload", out _)!;
            Assert.Equal(("POST", "invoices/march.pdf", "file"), (upload.Method, upload.SendFile, upload.FileField));
            Assert.Contains(upload.Notes, note => note.Contains("checks it"));
            Assert.Contains(upload.Notes, note => note.Contains("'Save the response to'"));
            Assert.Contains(upload.Notes, note => note.Contains("other fields are not sent"));
            Assert.Contains(PipelineCurl.Parse("curl --frobnicate 3 https://example.com", out _)!.Notes, note => note.Contains("'--frobnicate' is not read"));
        }

        [Theory]
        [InlineData("wget https://example.com", "Paste a command that starts with curl.")]
        [InlineData("curl -H 'Accept: x'", "The command names no URL.")]
        [InlineData("curl 'https://example.com", "A quote is not closed.")]
        [InlineData("curl https://example.com -X", "'-X' needs a value after it.")]
        public void Text_that_is_no_curl_command_is_refused_saying_why(string command, string expected)
        {
            Assert.Null(PipelineCurl.Parse(command, out var error));
            Assert.Equal(expected, error);
        }
    }
}
