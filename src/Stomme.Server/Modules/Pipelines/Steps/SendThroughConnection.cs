#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Tenancy;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// An HTTP request through one of the site's CONNECTIONS.
    /// The HTTP request step carries its Authorization value IN the pipeline, so a pipeline calling an
    /// authenticated API kept the secret in its definition; here the author names a connection and the credentials stay in
    /// the site's secret store. The request goes through the extraction port (<see cref="IConnectorExtractionService"/>) as
    /// the connector's <c>send</c> operation - the REST connector's - so it is the connection's base URL, its credentials and
    /// headers, and the HOST's egress, exactly as an extraction is. The response body is the result, the status an output.
    /// </summary>
    /// <remarks>
    /// A refusal - no such connection on the site, a connector without <c>send</c>, missing credentials, a denied egress, a
    /// non-2xx answer - is the port's CATEGORY, <c>EXTRACTION_&lt;OUTCOME&gt;</c> with the port's words, never the remote's,
    /// honouring <c>On failure</c>.
    /// </remarks>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "HTTP", "Send an HTTP request through one of the site's connections; its credentials stay in the site's secret store.", DisplayName = "Send through connection")]
    public class SendThroughConnection : DataStep
    {
        /// <summary>The operation a connector answers a send with (the REST connector's).</summary>
        public const string SendOperation = "send";

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "The id of one of the site's connections (Admin > Connections), a REST API one.", Order = 1)]
        public Input<string>? Connection { get; set; }

        [Input(DefaultValue = HttpRequestMethod.POST, Order = 2)]
        public Input<HttpRequestMethod> Method { get; set; } = new(new Literal<HttpRequestMethod>(HttpRequestMethod.POST));

        [Input(Description = "A path appended to the connection's endpoint, for example /v1/events.", Order = 3)]
        public Input<string?>? Path { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "The request body (not sent with GET or HEAD). Empty: the previous step's result when the method carries a body.", Order = 4)]
        public Input<string?>? Body { get; set; }

        [Input(DisplayName = "Content type", Description = "The body's media type.", DefaultValue = "application/json", Order = 5)]
        public Input<string> ContentType { get; set; } = new(new Literal<string>("application/json"));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Extra request headers, one 'Name: value' per line, over the connection's own.", Order = 6)]
        public Input<string?>? Headers { get; set; }

        [Output(DisplayName = "Status code")]
        public Output<int>? StatusCode { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var connection = Required(context, Connection, "Connection");
            if (!Guid.TryParse(connection.Trim(), out var connectionId))
            {
                await FailAsync(context, "CONNECTION_INVALID", $"'{connection}' is not a connection id.").ConfigureAwait(false);
                return;
            }

            var method = Method.GetOrDefault(context);
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["method"] = method.ToString() };
            if (Path?.GetOrDefault(context) is { Length: > 0 } path)
            {
                parameters["path"] = path.Trim();
            }

            if (method is not (HttpRequestMethod.GET or HttpRequestMethod.HEAD) && TextOrLastResult(context, Body) is { Length: > 0 } body)
            {
                parameters["body"] = body;
                parameters["content_type"] = ContentType.GetOrDefault(context) is { Length: > 0 } contentType ? contentType : "application/json";
            }

            // Each header's value rendered on its own (TemplatedLines) and refused holding a line break, then handed on one per line:
            // the connector splits the text into lines, so a run's value must not be able to add one (an Authorization of its own).
            var (headers, headerRefusal) = await TemplatedLines.HeadersAsync(Headers, context).ConfigureAwait(false);
            if (headers is null)
            {
                await FailAsync(context, "HEADER_INVALID", headerRefusal!).ConfigureAwait(false);
                return;
            }

            if (headers.Count > 0)
            {
                parameters["headers"] = string.Join("\n", headers.Select(header => header.Name + ": " + header.Value));
            }

            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                await FailAsync(context, "SITE_UNAVAILABLE", "The run has no site whose connections it could use.").ConfigureAwait(false);
                return;
            }

            var extraction = context.GetService<IConnectorExtractionService>();
            if (extraction is null)
            {
                await FailAsync(context, "CONNECTIVITY_UNAVAILABLE", "The Connectivity module is not composed in this host.").ConfigureAwait(false);
                return;
            }

            var result = await extraction.ExtractAsync(connectionId, SendOperation, parameters, maxRecords: 1, context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                await FailAsync(context, "EXTRACTION_" + result.Outcome.ToString().ToUpperInvariant(), result.Message).ConfigureAwait(false);
                return;
            }

            if (result.Records.Count != 1 || !result.Records[0].Fields.TryGetValue("status", out var status) || status is not long code)
            {
                await FailAsync(context, "SEND_UNSUPPORTED", "The connection's connector did not answer a send with a status and a body.").ConfigureAwait(false);
                return;
            }

            context.Set(StatusCode, (int)code);
            await SucceedAsync(context, result.Records[0].Fields.TryGetValue("body", out var responseBody) ? responseBody as string : null).ConfigureAwait(false);
        }
    }
}
