#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - n8n's "Fetch test event" for the Email received trigger: the newest message in a mailbox of one of the site's
    /// connections, shaped as a run gets it, for the editor to test the next steps with real mail. Read through the connection,
    /// as the trigger reads (the password stays in the site's secret store); nothing is marked read and no attachment is kept -
    /// each is named. For the pipelines' authors only: a message is what the mailbox holds.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record FetchNewestEmailQuery(string? Connection, string? Mailbox) : IQuery<PipelineTestEventView>;

    public sealed class FetchNewestEmailQueryHandler(IServiceProvider services) : IQueryHandler<FetchNewestEmailQuery, PipelineTestEventView>
    {
        /// <summary>How many of the newest UIDs a fetch reads for the newest message there - one deleted is passed over.</summary>
        public const int Lookback = 10;

        public async Task<PipelineTestEventView> Handle(FetchNewestEmailQuery request, CancellationToken ct)
        {
            if (EmailReceived.ConnectionProblem(request.Connection) is { } problem)
            {
                return new PipelineTestEventView { Problem = problem };
            }

            if (services.GetService(typeof(IConnectorExtractionService)) is not IConnectorExtractionService extraction)
            {
                return new PipelineTestEventView { Problem = "The Connectivity module is not composed in this host." };
            }

            var connection = Guid.Parse(request.Connection!.Trim());
            var mailbox = string.IsNullOrWhiteSpace(request.Mailbox) ? PipelineEmailEvent.Inbox : request.Mailbox.Trim();

            // A connector that can say its newest message (Gmail, whose history is not numbered message by message) is asked that;
            // the rest are read back from their next UID.
            var asked = await extraction.ExtractAsync(connection, "newest", new Dictionary<string, string> { ["mailbox"] = mailbox }, 1, ct);
            if (asked.Outcome is not (ConnectorOutcome.OperationUndefined or ConnectorOutcome.ExtractionUnsupported))
            {
                return !asked.Success ? new PipelineTestEventView { Problem = $"'{mailbox}' could not be read: {asked.Message}" }
                    : asked.Records.Count == 0 ? new PipelineTestEventView { Problem = $"'{mailbox}' holds no message to test with." }
                    : new PipelineTestEventView { Value = WatchedMailboxes.Write(mailbox, asked.Records[0], keep: null) };
            }

            var status = await extraction.ExtractAsync(connection, "status", new Dictionary<string, string> { ["mailbox"] = mailbox }, 1, ct);
            if (!status.Success || status.Records.Count == 0)
            {
                return new PipelineTestEventView { Problem = $"'{mailbox}' could not be read: {status.Message}" };
            }

            var next = WatchedMailboxes.Long(status.Records[0], "uid_next");
            var read = await extraction.ExtractAsync(connection, "read", new Dictionary<string, string>
            {
                ["mailbox"] = mailbox,
                ["after_uid"] = Math.Max(0, next - 1 - Lookback).ToString(CultureInfo.InvariantCulture),
                ["attachments"] = "names",
            }, Lookback, ct);
            if (!read.Success)
            {
                return new PipelineTestEventView { Problem = $"'{mailbox}' could not be read: {read.Message}" };
            }

            var newest = read.Records.Where(record => WatchedMailboxes.Long(record, "uid") > 0).OrderBy(record => WatchedMailboxes.Long(record, "uid")).LastOrDefault();
            return newest is null
                ? new PipelineTestEventView { Problem = $"'{mailbox}' holds no message to test with." }
                : new PipelineTestEventView { Value = WatchedMailboxes.Write(mailbox, newest, keep: null) };
        }
    }
}
