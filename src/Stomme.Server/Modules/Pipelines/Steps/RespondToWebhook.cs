#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Respond to webhook (n8n's Respond to Webhook node): the answer the call that started this run gets - its
    /// status, content type and body - when the run's Webhook is set to answer from the pipeline. The run goes on after it, and
    /// the next step still reads the step before this one (it sets no result). Past the first cut, as n8n's has them: headers
    /// (CORS, a <c>Location</c> with a 3xx status, caching) - one per line, each line's value rendered on its own so a run's
    /// value cannot add a header, and the ones the site keeps for itself refused (<see cref="ReservedHeaders"/>) - and a file
    /// of the site's pipeline folder answered instead of the body, its type from its name unless Content type says, inline or
    /// as a download, bounded as every file step is (<c>Pipelines:Files:MaxFileBytes</c>). A refused header or file faults the
    /// run before anything is answered, saying why.
    /// A run no call waits for (started otherwise, or the call stopped waiting) answers nobody, and its log says so; that is
    /// never a failure.
    /// </summary>
    [Step("Stomme", "Triggers", "Answer the webhook call that started this run - its status, content type and body. The run goes on.", DisplayName = "Respond to webhook")]
    public class RespondToWebhook : Step
    {
        public const int MaxBodyLength = 1024 * 1024;

        [Input(DisplayName = "Status code", Description = "200 for OK, 201 created, 400 a bad request, and so on.", DefaultValue = 200, Order = 1)]
        public Input<int> StatusCode { get; set; } = new(new Literal<int>(200));

        [Input(DisplayName = "Content type", Description = "Empty: application/json when the body is JSON, else text/plain.", Order = 2)]
        public Input<string?>? ContentType { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "What the caller gets. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Body { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Headers the caller gets, one 'Name: value' per line - Access-Control-Allow-Origin, a Location with a 3xx status, Cache-Control. Content type is set above; cookies, the content length and the connection's are the site's own and refused.", Order = 4)]
        public Input<string?>? Headers { get; set; }

        [Input(DisplayName = "File", UIHint = PipelineInputHints.PipelineFile, Description = "A file in the site's pipeline folder to answer with instead of the body - its bytes, its type from its name unless Content type says.", Order = 5)]
        public Input<string?>? File { get; set; }

        [Input(DisplayName = "Download as", Description = "With a file: the name the caller saves it under. Empty: the file is shown, not downloaded.", Order = 6)]
        public Input<string?>? DownloadAs { get; set; }

        /// <summary>Headers an answer may not set: the site's own (cookies, its security policy), the framing's, and the type and disposition the inputs above set.</summary>
        public static readonly IReadOnlySet<string> ReservedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Set-Cookie", "Content-Type", "Content-Length", "Content-Disposition", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade",
            "Trailer", "TE", "Proxy-Authenticate", "Proxy-Authorization", "Server", "Date", "Strict-Transport-Security",
            "Content-Security-Policy", "Content-Security-Policy-Report-Only",
        };

        // No header: the answer's own empty list, so an answer without headers equals one made without saying any.
        private static IReadOnlyList<(string Name, string Value)> Kept(List<(string Name, string Value)> headers) =>
            headers.Count == 0 ? Array.Empty<(string Name, string Value)>() : headers;

        [Output(Description = "Whether a call was waiting and got this answer.")]
        public Output<bool>? Answered { get; set; }

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var body = Body?.GetOrDefault(context);
            if (string.IsNullOrEmpty(body))
            {
                body = context.GetLastResult() switch
                {
                    null => string.Empty,
                    string text => text,
                    var other => other.ToString() ?? string.Empty,
                };
            }

            if (body.Length > MaxBodyLength)
            {
                body = body[..MaxBodyLength];
            }

            var status = Math.Clamp(StatusCode.GetOrDefault(context), 200, 599);
            var (headers, refusal) = await TemplatedLines.HeadersAsync(Headers, context).ConfigureAwait(false);
            if (refusal is not null)
            {
                throw new FaultException("RESPOND_INVALID", "Pipelines", "Configuration", refusal);
            }

            foreach (var (name, _) in headers!)
            {
                if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    throw new FaultException("RESPOND_INVALID", "Pipelines", "Configuration", $"'{name}' is not a header name - letters, digits and '-'.");
                }

                if (ReservedHeaders.Contains(name))
                {
                    throw new FaultException("RESPOND_INVALID", "Pipelines", "Configuration", $"'{name}' is the site's to set, not an answer's.");
                }
            }

            var given = ContentType?.GetOrDefault(context) is { Length: > 0 } typed ? typed.Trim() : null;
            PipelineWebhookAnswer answer;
            string what;
            if (File?.GetOrDefault(context) is { Length: > 0 } path)
            {
                var full = PipelineFileSandbox.Resolve(context, path.Trim(), out var outside)
                    ?? throw new FaultException("RESPOND_INVALID", "Pipelines", "Configuration", outside!);
                if (!System.IO.File.Exists(full))
                {
                    throw new FaultException("RESPOND_INVALID", "Pipelines", "Business", $"There is no file '{path.Trim()}' in the site's pipeline folder.");
                }

                var most = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new PipelineStepOptions().Files.MaxFileBytes;
                var size = new FileInfo(full).Length;
                if (size > most)
                {
                    throw new FaultException("RESPOND_INVALID", "Pipelines", "Business", $"'{path.Trim()}' is {size} bytes; a pipeline file is at most {most}.");
                }

                var bytes = await System.IO.File.ReadAllBytesAsync(full, context.CancellationToken).ConfigureAwait(false);
                var type = given ?? (new FileExtensionContentTypeProvider().TryGetContentType(full, out var known) ? known : "application/octet-stream");
                var download = DownloadAs?.GetOrDefault(context) is { Length: > 0 } named ? Path.GetFileName(named.Trim()) : null;
                answer = new PipelineWebhookAnswer(status, type, string.Empty) { Headers = Kept(headers), File = bytes, DownloadName = download };
                what = $"the file '{path.Trim()}', {bytes.Length} bytes of {type}" + (download is null ? "" : $", to save as '{download}'");
            }
            else
            {
                var type = given ?? (body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain");
                answer = new PipelineWebhookAnswer(status, type, body) { Headers = Kept(headers) };
                what = $"{body.Length} characters of {type}";
            }

            var answered = context.TryGetPipelineInput<string>(Webhook.AnswerToInputKey, out var id)
                && context.GetService<PipelineWebhookAnswers>() is { } answers
                && answers.TryAnswer(id, answer);
            context.AddExecutionLogEntry(answered ? "Answered" : "Not answered",
                answered ? $"Answered the call: {status}, {what}" + (headers.Count == 0 ? "." : $", with {headers.Count} header(s): {string.Join(", ", headers.Select(header => header.Name))}.")
                    : "No call waits for this answer - the run was not started by a webhook set to answer from the pipeline, or the call stopped waiting.");
            context.Set(Answered, answered);
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
