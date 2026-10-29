#nullable enable
using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>A real event a trigger's next steps are tested with (n8n's "Fetch test event"): its value as a run gets it, or why there is none.</summary>
    public sealed class PipelineTestEventView
    {
        public string? Value { get; set; }

        public string? Problem { get; set; }
    }

    /// <summary>
    /// What an "Email received" trigger hands the pipeline it starts (n8n's Email Trigger (IMAP)): the message, as JSON
    /// text, read with an expression - <c>{{ email.subject }}</c>, <c>{{ email.from }}</c>, <c>{{ email.text }}</c>. Its
    /// attachments are a list: each its name, type and size, and - when the trigger keeps them - its <c>path</c> in the site's
    /// pipeline folder, for a file step to read. ONE shape, written by the server from the IMAP connector's record and by the
    /// editor for the sample an author tests the next steps with.
    /// </summary>
    public static class PipelineEmailEvent
    {
        /// <summary>The trigger's step type.</summary>
        public const string StepType = "Stomme.EmailReceived";

        /// <summary>The mailbox a trigger watches when it names none.</summary>
        public const string Inbox = "INBOX";

        /// <summary>A message to test with in the editor, before any has come: an invoice with its PDF kept in the pipeline folder.</summary>
        public static string Sample(string? mailbox, bool keepAttachments, DateTimeOffset now)
        {
            var attachment = new JsonObject { ["name"] = "invoice-1001.pdf", ["contentType"] = "application/pdf", ["size"] = 48_213 };
            if (keepAttachments)
            {
                attachment["path"] = "mail/" + now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/1001-invoice-1001.pdf";
            }

            return new JsonObject
            {
                ["mailbox"] = string.IsNullOrWhiteSpace(mailbox) ? Inbox : mailbox.Trim(),
                ["uid"] = 1001,
                ["messageId"] = "invoice-1001@supplier.example",
                ["date"] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["from"] = "\"Grace Supplier\" <grace@supplier.example>",
                ["to"] = "\"Orders\" <orders@example.com>",
                ["cc"] = "",
                ["replyTo"] = "",
                ["subject"] = "Invoice 1001",
                ["text"] = "Please find invoice 1001 attached: 120.00 EUR, due in 30 days.",
                ["html"] = "<p>Please find invoice 1001 attached: 120.00 EUR, due in 30 days.</p>",
                ["truncated"] = false,
                ["size"] = 52_480,
                ["attachments"] = new JsonArray(attachment),
            }.ToJsonString(Readable);
        }

        // An address says "Grace" <grace@x> as it is: the text is JSON to read, not markup.
        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }
}
