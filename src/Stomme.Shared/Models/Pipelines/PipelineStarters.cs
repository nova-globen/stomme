#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>A shipped pipeline to start from: what it does, and a fresh copy of its graph each time.</summary>
    public sealed class PipelineStarter
    {
        public PipelineStarter(string id, string name, string description, Func<PipelineGraph> graph)
        {
            Id = id;
            Name = name;
            Description = description;
            _graph = graph;
        }

        private readonly Func<PipelineGraph> _graph;

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public PipelineGraph Graph() => _graph();
    }

    /// <summary>
    /// The pipelines a site can start from (n8n's templates): each one built from the steps every installation
    /// has, saved as it stands and published as it stands - a note on the canvas says what to change first (a file to put in the
    /// pipeline folder, a webhook to switch on). The test suite holds every one to the engine's save and the problem check, so a
    /// starter that drifts from the steps fails the build, never a person.
    /// </summary>
    public static class PipelineStarters
    {
        public static IReadOnlyList<PipelineStarter> All { get; } = new[]
        {
            new PipelineStarter("greeting", "Say hello", "A variable read into a message with an expression - the smallest pipeline that runs.", Greeting),
            new PipelineStarter("csv-to-spreadsheet", "CSV file to spreadsheet", "Read a CSV file from the pipeline folder, parse it, and write its rows to an XLSX spreadsheet.", CsvToSpreadsheet),
            new PipelineStarter("csv-put-in-a-folder", "Each CSV put in a folder, as a spreadsheet", "Start when a CSV file is put in the inbox folder - on the Files page, by a form or another pipeline - and write its rows to a spreadsheet of the same name - n8n's Local File Trigger.", CsvPutInAFolder),
            new PipelineStarter("for-each-row", "For each row", "Parse a CSV file and run a step once per row, the row in a variable.", ForEachRow),
            new PipelineStarter("webhook-to-file", "Webhook to file", "Start when the site's webhook is called, and keep what it sent in a file.", WebhookToFile),
            new PipelineStarter("hourly-check", "Hourly HTTP check", "Every hour, call an address and log whether it answered - the two outcomes of a request each going its own way.", HourlyCheck),
            new PipelineStarter("alert-on-failure", "Alert when a pipeline fails", "Start when a run of any pipeline on the site fails, and say what failed and where - n8n's error workflow.", AlertOnFailure),
            new PipelineStarter("evaluate-sorting", "Sort messages with AI, and test it", "Sort a webhook's messages with the site's language model - and test the sorting on a data table of messages and the category each should get, scored and averaged on the Evaluations page - n8n's Evaluations.", EvaluateSorting),
            new PipelineStarter("ai-sort", "Sort messages with AI", "Start when the site's webhook is called, have the site's language model put the message in a category - each its own port - and read who wrote a complaint and about which order.", AiSort),
            new PipelineStarter("approve-invoice", "Approve before paying", "Start when an invoice comes in, ask a person to approve it within two days, and go on by what they decided - n8n's human in the loop.", ApproveInvoice),
            new PipelineStarter("answer-a-call", "Answer a webhook call (a small API)", "Start when the site's webhook is called, work out an answer, and send it back to the caller - n8n's Respond to Webhook.", AnswerACall),
            new PipelineStarter("ask-for-details", "Ask a person for details", "Start when an order comes in, hand the customer a form for what is missing, wait up to three days for the answers, and go on with them - n8n's Wait for a form.", AskForDetails),
            new PipelineStarter("contact-form", "A contact form", "A form the site shows to anyone with its address; each message sent with it is kept in a file - n8n's Form trigger.", ContactForm),
            new PipelineStarter("signup-two-pages", "A sign-up form of two pages", "A form of two pages: a person's name, then their e-mail and plan on the next page - where they came from carried unseen from the form's address - n8n's multi-page forms and hidden fields.", SignupTwoPages),
            new PipelineStarter("screen-before-ai", "Screen a message before AI", "Start when the site's webhook is called, take personal data and keys out of the message, check it is no jailbreak and keeps to your topic, and only then ask the site's language model - n8n's Guardrails.", ScreenBeforeAi),
            new PipelineStarter("orders-report", "Orders as an HTML page", "Read orders from a CSV file and write them as an HTML page - a table with every value encoded, under a heading - n8n's HTML node.", OrdersReport),
            new PipelineStarter("telegram-ai-bot", "A Telegram bot that answers with AI", "Start when the site's Telegram bot gets a message, have the site's language model answer it, and send the answer back in the same chat, in reply - n8n's Telegram Trigger, an AI node and a Telegram node.", TelegramAiBot),
            new PipelineStarter("chat-assistant", "A chat assistant", "A chat page the site shows its visitors, answered by the site's language model, which remembers each visitor's conversation - n8n's Chat trigger and AI Agent.", ChatAssistant),
            new PipelineStarter("new-feed-items", "New items of a feed", "Every hour, read an RSS feed, keep its items in a data table, and go on with only the ones not seen before - n8n's RSS Read and Data tables.", NewFeedItems),
            new PipelineStarter("invoice-from-pdf", "Invoice from a PDF", "Read an invoice PDF from the pipeline folder, have the site's language model pull out its number, supplier, total and due date, and keep them in a data table - n8n's Extract from File and Information Extractor.", InvoiceFromPdf),
            new PipelineStarter("keep-document-passages", "Keep a document's passages", "Read a PDF from the pipeline folder and keep its passages in a data table, for \"Answer from your documents\" to search - storing it again replaces them.", KeepDocumentPassages),
            new PipelineStarter("answer-from-documents", "Answer from your documents", "Start when the site's webhook is called with a question, find the passages that best answer it, and have the site's language model answer from them alone, citing them - n8n's question and answer chain.", AnswerFromDocuments),
            new PipelineStarter("top-customers", "Top customers from a CSV", "Read orders from a CSV file, keep the paid ones, total them per customer, and write the ten biggest to a spreadsheet - filter, summarize, sort and limit.", TopCustomers),
        };

        public static PipelineStarter? Find(string? id) => All.FirstOrDefault(starter => string.Equals(starter.Id, id, StringComparison.Ordinal));

        private static PipelineGraph Greeting() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "world" } },
            Steps =
            {
                new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Name = "Greet", Position = At(80, 80), InputExpressions = { ["Message"] = "Hello, {{ who }}!" } },
            },
            Notes = { Note("Change who's value under Variables, then Save and publish, and Run. The message is in the run's log.", 80, 240) },
        };

        private static PipelineGraph CsvToSpreadsheet() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "Read the CSV", Position = At(80, 80), Inputs = { ["Path"] = "input.csv" } },
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse it", Position = At(360, 80) },
                new PipelineGraphStep { Id = "write", Type = "Stomme.WriteSpreadsheet", Name = "Write the spreadsheet", Position = At(640, 80), Inputs = { ["Path"] = "output.xlsx" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "parse" },
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "write" },
            },
            Notes = { Note("Put input.csv in the site's pipeline folder first. Each step reads what the one before it produced.", 80, 260) },
        };

        // File added (n8n's Local File Trigger) - the file's path read by the next step, its name naming what is written.
        private static PipelineGraph CsvPutInAFolder() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-file", Name = "file" } },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "added", Type = PipelineFileEvent.StepType, Name = "A CSV is put in inbox", Position = At(80, 80),
                    Inputs = { ["Folder"] = "inbox", ["Pattern"] = "*.csv" }, OutputVariables = { ["Result"] = "v-file" },
                },
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "Read it", Position = At(360, 80), InputExpressions = { ["Path"] = "{{ file.path }}" } },
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse it", Position = At(640, 80) },
                new PipelineGraphStep
                {
                    Id = "write", Type = "Stomme.WriteSpreadsheet", Name = "Write the spreadsheet", Position = At(920, 80),
                    InputExpressions = { ["Path"] = "spreadsheets/{{ file.name }}.xlsx" },
                },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "added", TargetId = "read" },
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "parse" },
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "write" },
            },
            Notes = { Note("Publish it, then put a CSV in the inbox folder on the Files page: its spreadsheet appears in spreadsheets. Files already there when it is published start nothing. Write somewhere other than inbox - a pipeline writing into the folder it watches starts itself again.", 80, 260) },
        };

        private static PipelineGraph ForEachRow() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-rows", Name = "rows" },
                new PipelineGraphVariable { Id = "v-row", Name = "row" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "Read the CSV", Position = At(80, 80), Inputs = { ["Path"] = "input.csv" } },
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse it", Position = At(360, 80), OutputVariables = { ["Result"] = "v-rows" } },
                new PipelineGraphStep { Id = "each", Type = "Stomme.ForEach", Name = "Each row", Position = At(640, 80), InputVariables = { ["Items"] = "v-rows" }, OutputVariables = { ["CurrentValue"] = "v-row" } },
                new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log the row", Parent = "each", ParentPort = "Body", Position = At(80, 80), InputExpressions = { ["Message"] = "Row: {{ row }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "parse" },
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "each" },
            },
            Notes = { Note("Put input.csv in the site's pipeline folder first. Open Each row's body to change what runs per row - {{ row.column }} reads one column.", 80, 260) },
        };

        private static PipelineGraph WebhookToFile() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When called", Position = At(80, 80), Inputs = { ["WebhookName"] = "incoming" } },
                new PipelineGraphStep { Id = "keep", Type = "Stomme.WriteTextFile", Name = "Keep what it sent", Position = At(360, 80), Inputs = { ["Path"] = "webhooks/incoming.txt" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "hook", TargetId = "keep" } },
            Notes = { Note("The site's webhooks need Pipelines:Webhooks:Enabled and a secret. Select the webhook step to see its address and to listen for a call.", 80, 260) },
        };

        private static PipelineGraph HourlyCheck() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-status", Name = "status" },
                new PipelineGraphVariable { Id = "v-error", Name = "error" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "every-hour", Type = "Stomme.Schedule", Name = "Every hour", Position = At(80, 120), Inputs = { ["Cron"] = "0 * * * *" } },
                new PipelineGraphStep
                {
                    Id = "call", Type = "Stomme.SendHttpRequest", Name = "Call the address", Position = At(360, 120),
                    Inputs = { ["Url"] = "https://example.com/", ["OnFailure"] = "Continue" },
                    OutputVariables = { ["StatusCode"] = "v-status", ["Error"] = "v-error" },
                },
                new PipelineGraphStep { Id = "up", Type = "Stomme.LogMessage", Name = "It answered", Position = At(660, 40), InputExpressions = { ["Message"] = "Up: HTTP {{ status }}" } },
                new PipelineGraphStep { Id = "down", Type = "Stomme.LogMessage", Name = "It did not", Position = At(660, 220), Inputs = { ["Level"] = "Warning" }, InputExpressions = { ["Message"] = "Down: {{ error }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "every-hour", TargetId = "call" },
                new PipelineGraphConnection { SourceId = "call", SourcePort = "Done", TargetId = "up" },
                new PipelineGraphConnection { SourceId = "call", SourcePort = "Failed", TargetId = "down" },
            },
            Notes = { Note("Change the address in Call the address. A request that fails goes out through Failed with its error, instead of failing the run.", 80, 320) },
        };

        private static PipelineGraph AlertOnFailure() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-failure", Name = "failure" } },
            Steps =
            {
                new PipelineGraphStep { Id = "failed", Type = PipelineFailure.StepType, Name = "When a pipeline fails", Position = At(80, 80), OutputVariables = { ["Result"] = "v-failure" } },
                new PipelineGraphStep
                {
                    Id = "say", Type = "Stomme.LogMessage", Name = "Say what failed", Position = At(360, 80),
                    Inputs = { ["Level"] = "Warning" },
                    InputExpressions = { ["Message"] = "{{ failure.pipeline }} failed at {{ failure.step }}: {{ failure.error }} (run {{ failure.run }})" },
                },
            },
            Connections = { new PipelineGraphConnection { SourceId = "failed", TargetId = "say" } },
            Notes = { Note("Choose the pipeline to watch in When a pipeline fails, or leave it for every pipeline. Swap the log for Send notification or Send HTTP request to be told.", 80, 260) },
        };

        /// <summary>
        /// n8n's "Error workflow" setting, made from the pipeline that fails: the alert starter watching that one pipeline
        /// (by its definition id, as its trigger is indexed), its step named for it.
        /// </summary>
        public static PipelineGraph FailureHandler(string definitionId, string? name)
        {
            var graph = AlertOnFailure();
            var watching = graph.Steps.Single(step => step.Type == PipelineFailure.StepType);
            watching.Inputs["Pipeline"] = definitionId;
            watching.Name = string.IsNullOrWhiteSpace(name) ? "When it fails" : $"When '{name.Trim()}' fails";
            graph.Notes.Clear();
            graph.Notes.Add(Note("Says what failed and where in the run's log. Swap the log for Send notification or Send HTTP request to be told - the failure's pipeline, step, error and run are in failure.", 80, 260));
            return graph;
        }

        // Ask AI reads what the webhook was sent (an empty prompt asks about the previous result) and says what it is; a Switch
        // sends a complaint one way and everything else the other.
        // n8n's Text Classifier and Information Extractor - the categories are the step's ports.
        private static PipelineGraph AiSort() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-kind", Name = "kind" },
                new PipelineGraphVariable { Id = "v-complaint", Name = "complaint" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When a message comes", Position = At(80, 160), Inputs = { ["WebhookName"] = "messages" } },
                new PipelineGraphStep
                {
                    Id = "sort", Type = "Stomme.ClassifyText", Name = "What is it?", Position = At(360, 160),
                    Inputs =
                    {
                        [StepCatalogEntry.BranchesOutcomes] = PipelineOutcomes.WriteBranches(new[] { "Complaint", "Question", "Order" }),
                        ["Descriptions"] = "Complaint: something went wrong and the customer is unhappy\nQuestion: the customer asks something\nOrder: the customer wants to buy",
                    },
                    OutputVariables = { ["Category"] = "v-kind" },
                },
                new PipelineGraphStep
                {
                    Id = "read", Type = "Stomme.ExtractInformation", Name = "Who and which order", Position = At(660, 40),
                    Inputs = { ["Fields"] = "name: who wrote\nemail: their e-mail address\norder: the order number, if they give one" },
                    OutputVariables = { ["Result"] = "v-complaint" },
                },
                new PipelineGraphStep { Id = "complaint", Type = "Stomme.LogMessage", Name = "Pass it on", Position = At(960, 40), Inputs = { ["Level"] = "Warning" }, InputExpressions = { ["Message"] = "A complaint from {{ complaint.name }} <{{ complaint.email }}> about order {{ complaint.order }}." } },
                new PipelineGraphStep { Id = "question", Type = "Stomme.LogMessage", Name = "A question", Position = At(660, 180), InputExpressions = { ["Message"] = "A {{ kind }} came in." } },
                new PipelineGraphStep { Id = "order", Type = "Stomme.LogMessage", Name = "An order", Position = At(660, 300), InputExpressions = { ["Message"] = "An {{ kind }} came in." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "sort" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Complaint", TargetId = "read" },
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "complaint" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Question", TargetId = "question" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Order", TargetId = "order" },
            },
            Notes = { Note("The webhook needs Pipelines:Webhooks:Enabled, and the AI steps the site's assistant switched on with a model (Admin › Assistant › Settings). Each category of What is it? is a port - add one in its Categories. Swap the logs for Send notification to be told.", 80, 420) },
        };

        // n8n's Evaluations: the webhook's runs and an evaluation's runs both reach "What is it?" - When evaluating gives it
        // a row's message as the webhook gives the body, and the row to {{ row }} - then the evaluation's runs score the category
        // against the row's expected one and keep it, while the webhook's go on as usual.
        private static PipelineGraph EvaluateSorting() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-kind", Name = "kind" },
                new PipelineGraphVariable { Id = "v-row", Name = "row" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When a message comes", Position = At(80, 80), Inputs = { ["WebhookName"] = "messages-to-sort" } },
                new PipelineGraphStep { Id = "cases", Type = PipelineEvaluations.TriggerType, Name = "When evaluating", Position = At(80, 280), Inputs = { ["Column"] = "message" }, OutputVariables = { ["Row"] = "v-row" } },
                new PipelineGraphStep
                {
                    Id = "sort", Type = "Stomme.ClassifyText", Name = "What is it?", Position = At(360, 180),
                    Inputs =
                    {
                        [StepCatalogEntry.BranchesOutcomes] = PipelineOutcomes.WriteBranches(new[] { "Complaint", "Question", "Order" }),
                        ["Descriptions"] = "Complaint: something went wrong and the customer is unhappy\nQuestion: the customer asks something\nOrder: the customer wants to buy",
                    },
                    OutputVariables = { ["Category"] = "v-kind" },
                },
                new PipelineGraphStep { Id = "check", Type = "Stomme.IfEvaluating", Name = "Evaluating?", Position = At(660, 180) },
                new PipelineGraphStep
                {
                    Id = "score", Type = "Stomme.Evaluation", Name = "Score the category", Position = At(960, 80),
                    Inputs = { ["Operation"] = "ScoreMatch", ["MetricName"] = "right category", ["Match"] = "IgnoringCase" },
                    InputExpressions = { ["Expected"] = "{{ row.expected }}", ["Actual"] = "{{ kind }}" },
                },
                new PipelineGraphStep { Id = "keep", Type = "Stomme.Evaluation", Name = "Keep the category", Position = At(1240, 80), Inputs = { ["Operation"] = "RecordOutput", ["MetricName"] = "category" }, InputExpressions = { ["Value"] = "{{ kind }}" } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Name = "Go on with it", Position = At(960, 280), InputExpressions = { ["Message"] = "A {{ kind }} came in." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "sort" },
                new PipelineGraphConnection { SourceId = "cases", TargetId = "sort" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Complaint", TargetId = "check" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Question", TargetId = "check" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Order", TargetId = "check" },
                new PipelineGraphConnection { SourceId = "check", SourcePort = "Evaluating", TargetId = "score" },
                new PipelineGraphConnection { SourceId = "score", SourcePort = "Done", TargetId = "keep" },
                new PipelineGraphConnection { SourceId = "check", SourcePort = "Not evaluating", TargetId = "say" },
            },
            Notes = { Note("Test it before it goes live: make a data table (Pipelines › Data tables) with the columns message and expected - a message and the category it should get - publish, and open Evaluations from the bar. The webhook needs Pipelines:Webhooks:Enabled, the AI the site's assistant switched on with a model.", 80, 440) },
        };

        // "answer from your documents", its first half: a document's text kept as passages.
        private static PipelineGraph KeepDocumentPassages() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadDocument", Name = "Read the document", Position = At(80, 80), Inputs = { ["Path"] = "documents/handbook.pdf" } },
                new PipelineGraphStep { Id = "keep", Type = "Stomme.StorePassages", Name = "Keep its passages", Position = At(360, 80), Inputs = { ["Table"] = "Passages", ["Source"] = "handbook.pdf" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "keep" } },
            Notes = { Note("Put a PDF (or a .docx) at documents/handbook.pdf in the pipeline folder, and name it as Source. Run it again after the document changes: its passages are replaced. \"Answer from your documents\" searches the Passages table.", 80, 240) },
        };

        // And its second half: a question, the passages that answer it, an answer from them alone.
        private static PipelineGraph AnswerFromDocuments() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-question", Name = "question" },
                new PipelineGraphVariable { Id = "v-passages", Name = "passages" },
                new PipelineGraphVariable { Id = "v-answer", Name = "answer" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When a question comes", Position = At(80, 80), Inputs = { ["WebhookName"] = "questions" }, OutputVariables = { ["Result"] = "v-question" } },
                new PipelineGraphStep { Id = "find", Type = "Stomme.FindPassages", Name = "Find the passages", Position = At(360, 80), Inputs = { ["Table"] = "Passages", ["Top"] = "4" }, OutputVariables = { ["Context"] = "v-passages" } },
                new PipelineGraphStep
                {
                    Id = "answer", Type = "Stomme.AskAi", Name = "Answer from them", Position = At(640, 80),
                    Inputs = { ["Instructions"] = "Answer the question from the numbered passages alone, citing their numbers like [2]. When they do not hold the answer, say that you do not know." },
                    InputExpressions = { ["Prompt"] = "Question: {{ question }}\n\nPassages:\n{{ passages }}" },
                    OutputVariables = { ["Result"] = "v-answer" },
                },
                new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Name = "Say the answer", Position = At(920, 80), InputExpressions = { ["Message"] = "{{ answer }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "find" },
                new PipelineGraphConnection { SourceId = "find", SourcePort = "Done", TargetId = "answer" },
                new PipelineGraphConnection { SourceId = "answer", SourcePort = "Done", TargetId = "say" },
            },
            Notes = { Note("Fill the Passages table first with \"Keep a document's passages\". The webhook needs Pipelines:Webhooks:Enabled, the AI step the site's assistant switched on with a model. To answer the caller, start from \"Answer a webhook call\" instead of the webhook.", 80, 240) },
        };

        // n8n's Extract from File and Information Extractor: a PDF's text, its fields pulled out as a record, the record kept.
        private static PipelineGraph InvoiceFromPdf() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-invoice", Name = "invoice" } },
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadDocument", Name = "Read the PDF", Position = At(80, 80), Inputs = { ["Path"] = "invoices/invoice.pdf" } },
                new PipelineGraphStep
                {
                    Id = "extract", Type = "Stomme.ExtractInformation", Name = "Pull out its fields", Position = At(360, 80),
                    Inputs = { ["Fields"] = "number: the invoice number\nsupplier: who sent it\ntotal (number): the amount to pay\ndue (date): when it must be paid" },
                    OutputVariables = { ["Result"] = "v-invoice" },
                },
                new PipelineGraphStep { Id = "keep", Type = "Stomme.DataTable", Name = "Keep it", Position = At(640, 80), Inputs = { ["Table"] = "Invoices", ["Operation"] = "Upsert", ["MatchColumns"] = "number", ["CreateIfMissing"] = "true" } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Name = "Say what came", Position = At(920, 80), InputExpressions = { ["Message"] = "Invoice {{ invoice.number }} from {{ invoice.supplier }}: {{ invoice.total }}, due {{ invoice.due }}." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "extract" },
                new PipelineGraphConnection { SourceId = "extract", SourcePort = "Done", TargetId = "keep" },
                new PipelineGraphConnection { SourceId = "keep", SourcePort = "Done", TargetId = "say" },
            },
            Notes = { Note("Put an invoice at invoices/invoice.pdf in the pipeline folder - or fetch one with HTTP request (save the response to a file) in front of Read the PDF. A scanned PDF holds pictures, not text: Read the PDF says so. The AI step needs the site's assistant switched on with a model.", 80, 260) },
        };

        // n8n's Wait node "on form submitted" - the run's form address handed out, then a durable wait for the answers.
        private static PipelineGraph AskForDetails() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-order", Name = "order" },
                new PipelineGraphVariable { Id = "v-form", Name = "formAddress" },
                new PipelineGraphVariable { Id = "v-details", Name = "details" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When an order comes", Position = At(80, 160), Inputs = { ["WebhookName"] = "orders-to-complete" }, OutputVariables = { ["Result"] = "v-order" } },
                new PipelineGraphStep { Id = "address", Type = PipelineCalls.AddressStepType, Name = "A form for the customer", Position = At(360, 160), OutputVariables = { ["FormAddress"] = "v-form" } },
                new PipelineGraphStep { Id = "tell", Type = "Stomme.LogMessage", Name = "Send the form", Position = At(640, 160), InputExpressions = { ["Message"] = "Ask the customer of order {{ order.id }} to fill in {{ formAddress }}" } },
                new PipelineGraphStep
                {
                    Id = "wait", Type = "Stomme.WaitForForm", Name = "Wait for the details", Position = At(920, 160),
                    Inputs =
                    {
                        ["Title"] = "A few details for your order",
                        ["Description"] = "We need these to send it.",
                        ["Fields"] = PipelineForms.Serialize(new[]
                        {
                            new PipelineFormField { Label = "Delivery address", Type = PipelineFormFieldTypes.LongText, Required = true, Name = "address" },
                            new PipelineFormField { Label = "Phone", Type = PipelineFormFieldTypes.Text, Name = "phone" },
                        }),
                        [PipelineCalls.TimeOutInput] = "3 days",
                    },
                    OutputVariables = { ["Result"] = "v-details" },
                },
                new PipelineGraphStep { Id = "got", Type = "Stomme.LogMessage", Name = "Go on with them", Position = At(1200, 80), InputExpressions = { ["Message"] = "Order {{ order.id }} ships to {{ details.address }}" } },
                new PipelineGraphStep { Id = "late", Type = "Stomme.LogMessage", Name = "Nobody answered", Position = At(1200, 260), Inputs = { ["Level"] = "Warning" }, InputExpressions = { ["Message"] = "No details for order {{ order.id }} in three days." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "address" },
                new PipelineGraphConnection { SourceId = "address", TargetId = "tell" },
                new PipelineGraphConnection { SourceId = "tell", TargetId = "wait" },
                new PipelineGraphConnection { SourceId = "wait", SourcePort = "Submitted", TargetId = "got" },
                new PipelineGraphConnection { SourceId = "wait", SourcePort = PipelineCalls.TimedOut, TargetId = "late" },
            },
            Notes = { Note("The form page needs Pipelines:Forms:Enabled and the webhook Pipelines:Webhooks:Enabled. Swap Send the form for Send notification (or an e-mail) to reach the customer; the run waits, stored, holding no worker.", 80, 360) },
        };

        // n8n's RSS Read and Data tables - an Upsert on the link keeps every item once; its Inserted output is only the new.
        private static PipelineGraph NewFeedItems() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-new", Name = "newItems" },
                new PipelineGraphVariable { Id = "v-count", Name = "newCount" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "every-hour", Type = "Stomme.Schedule", Name = "Every hour", Position = At(80, 160), Inputs = { ["Cron"] = "0 * * * *" } },
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadFeed", Name = "Read the feed", Position = At(360, 160), Inputs = { ["Url"] = "https://example.com/feed.xml" } },
                new PipelineGraphStep
                {
                    Id = "keep", Type = "Stomme.DataTable", Name = "Keep the items", Position = At(640, 160),
                    Inputs = { ["Table"] = "Feed items", ["Operation"] = "Upsert", ["MatchColumns"] = "link", ["CreateIfMissing"] = "true" },
                    OutputVariables = { ["Inserted"] = "v-new", ["InsertedCount"] = "v-count" },
                },
                new PipelineGraphStep
                {
                    Id = "any", Type = "Stomme.FlowDecision", Name = "Anything new?", Position = At(920, 160),
                    InputConditions = { ["Condition"] = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ newCount }}", Operator = "greater", Right = "0" } } } },
                },
                new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Name = "Say what is new", Position = At(1200, 80), InputExpressions = { ["Message"] = "{{ newCount }} new item(s): {{ newItems }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "every-hour", TargetId = "read" },
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "keep" },
                new PipelineGraphConnection { SourceId = "keep", SourcePort = "Done", TargetId = "any" },
                new PipelineGraphConnection { SourceId = "any", SourcePort = "True", TargetId = "say" },
            },
            Notes = { Note("Put the feed's address in Read the feed. Keep the items makes the data table 'Feed items' on its first run, a column per field of an item, and keeps each item once by its link - so Anything new? sees only what the feed added since. Swap Say what is new for Send notification to be told.", 80, 340) },
        };

        // The webhook's body is the invoice; a person approves or rejects it on the run's page, or nobody does in two days.
        private static PipelineGraph ApproveInvoice() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-invoice", Name = "invoice" },
                new PipelineGraphVariable { Id = "v-approval", Name = "approval" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When an invoice comes", Position = At(80, 160), Inputs = { ["WebhookName"] = "invoices" }, OutputVariables = { ["Result"] = "v-invoice" } },
                new PipelineGraphStep
                {
                    Id = "ask", Type = PipelineApprovals.StepType, Name = "Approve the invoice", Position = At(360, 160),
                    Inputs = { [PipelineApprovals.QuestionInput] = "Pay the invoice that came in?", [PipelineApprovals.DecideWithinInput] = "2 days", [PipelineApprovals.NotifyInput] = "role:Administrators" },
                    InputExpressions = { ["Details"] = "{{ invoice }}" },
                    OutputVariables = { ["Result"] = "v-approval" },
                },
                new PipelineGraphStep { Id = "pay", Type = "Stomme.LogMessage", Name = "Pay it", Position = At(680, 40), InputExpressions = { ["Message"] = "Approved by {{ approval.by }} - pay it." } },
                new PipelineGraphStep { Id = "refuse", Type = "Stomme.LogMessage", Name = "Refused", Position = At(680, 160), Inputs = { ["Level"] = "Warning" }, InputExpressions = { ["Message"] = "Rejected by {{ approval.by }}: {{ approval.comment }}" } },
                new PipelineGraphStep { Id = "late", Type = "Stomme.LogMessage", Name = "Nobody decided", Position = At(680, 280), Inputs = { ["Level"] = "Warning", ["Message"] = "Nobody decided on the invoice in two days." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "ask" },
                new PipelineGraphConnection { SourceId = "ask", SourcePort = PipelineApprovals.Approved, TargetId = "pay" },
                new PipelineGraphConnection { SourceId = "ask", SourcePort = PipelineApprovals.Rejected, TargetId = "refuse" },
                new PipelineGraphConnection { SourceId = "ask", SourcePort = PipelineApprovals.Expired, TargetId = "late" },
            },
            Notes = { Note("A person with the pipelines:approve permission decides on the run's page - the Administrators are told in the app, with a link; the runs list shows what waits. The webhook needs Pipelines:Webhooks:Enabled. Swap the logs for the real payment.", 80, 400) },
        };

        // The webhook's caller waits for the answer the pipeline gives (Respond to webhook), worked out with filters and built-ins.
        private static PipelineGraph AnswerACall() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-request", Name = "request" } },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "hook", Type = PipelineWebhookAnswering.WebhookStepType, Name = "When asked for a quote", Position = At(80, 120),
                    Inputs = { ["WebhookName"] = "quote", [PipelineWebhookAnswering.AnswerInput] = PipelineWebhookAnswering.FromThePipeline },
                    OutputVariables = { ["Result"] = "v-request" },
                },
                new PipelineGraphStep
                {
                    Id = "answer", Type = PipelineWebhookAnswering.RespondStepType, Name = "Send the quote back", Position = At(380, 120),
                    InputExpressions = { ["Body"] = "{\"item\": \"{{ request.item | default: \"unknown\" }}\", \"price\": {{ request.quantity | default: 1 | times: 9.5 | fixed: 2 }}, \"quoted\": \"{{ $now }}\"}" },
                },
                new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Note the quote", Position = At(680, 120), InputExpressions = { ["Message"] = "Quoted {{ request.quantity | default: 1 }} x {{ request.item | default: \"unknown\" }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "answer" },
                new PipelineGraphConnection { SourceId = "answer", TargetId = "log" },
            },
            Notes = { Note("POST {\"item\": \"tea\", \"quantity\": 3} to the site's webhook 'quote' (signed): the caller gets the quote back. The webhook needs Pipelines:Webhooks:Enabled and a secret.", 80, 300) },
        };

        // n8n's data nodes in a row: keep the paid orders, total them per customer, and write the ten biggest to a spreadsheet.
        private static PipelineGraph TopCustomers() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "Read the orders", Position = At(80, 80), Inputs = { ["Path"] = "orders.csv" } },
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse them", Position = At(360, 80) },
                new PipelineGraphStep
                {
                    Id = "paid", Type = "Stomme.FilterRecords", Name = "Only the paid ones", Position = At(640, 80),
                    Inputs =
                    {
                        ["Condition"] = PipelineConditions.Write(new PipelineCondition
                        {
                            IgnoreCase = true,
                            Rules = { new PipelineConditionRule { Left = "{{ status }}", Operator = PipelineConditions.EqualsOperator, Right = "paid" } },
                        }),
                    },
                },
                new PipelineGraphStep { Id = "total", Type = "Stomme.SummarizeRecords", Name = "Total per customer", Position = At(920, 80), Inputs = { ["GroupBy"] = "customer", ["Aggregations"] = "count as orders\nsum amount as total" } },
                new PipelineGraphStep { Id = "sort", Type = "Stomme.SortRecords", Name = "Biggest first", Position = At(920, 260), Inputs = { ["SortBy"] = "total desc" } },
                new PipelineGraphStep { Id = "top", Type = "Stomme.LimitRecords", Name = "The top ten", Position = At(640, 260), Inputs = { ["MaxRecords"] = "10" } },
                new PipelineGraphStep { Id = "write", Type = "Stomme.WriteSpreadsheet", Name = "Write the spreadsheet", Position = At(360, 260), Inputs = { ["Path"] = "top-customers.xlsx" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "parse" },
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "paid" },
                new PipelineGraphConnection { SourceId = "paid", SourcePort = "Done", TargetId = "total" },
                new PipelineGraphConnection { SourceId = "total", SourcePort = "Done", TargetId = "sort" },
                new PipelineGraphConnection { SourceId = "sort", SourcePort = "Done", TargetId = "top" },
                new PipelineGraphConnection { SourceId = "top", SourcePort = "Done", TargetId = "write" },
            },
            Notes = { Note("Put orders.csv (customer, amount, status columns) in the site's pipeline folder first. Each step reads what the one before it produced; select a step and Run to see its records as a table.", 80, 420) },
        };

        // n8n's multi-page forms and its hidden field - a form's next page, and where a person came from carried from the address.
        private static PipelineGraph SignupTwoPages() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-first", Name = "first" }, new PipelineGraphVariable { Id = "v-second", Name = "second" } },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "form", Type = PipelineForms.StepType, Name = "Sign up", Position = At(80, 80),
                    Inputs =
                    {
                        ["FormName"] = "signup",
                        ["Title"] = "Sign up",
                        ["Fields"] = PipelineForms.Serialize(
                        [
                            new PipelineFormField { Label = "Your name", Name = "name", Required = true },
                            new PipelineFormField { Type = PipelineFormFieldTypes.Hidden, Name = "source", Value = "web" },
                        ]),
                        ["SubmitLabel"] = "Next",
                        ["Answer"] = "Welcome aboard - we will be in touch.",
                    },
                    OutputVariables = { ["Result"] = "v-first" },
                },
                new PipelineGraphStep
                {
                    Id = "page-two", Type = PipelineForms.WaitStepType, Name = "Page two", Position = At(360, 80),
                    Inputs =
                    {
                        ["Title"] = "Almost done",
                        [PipelineForms.NextPageInput] = "true",
                        ["Fields"] = PipelineForms.Serialize(
                        [
                            new PipelineFormField { Label = "E-mail", Name = "email", Type = PipelineFormFieldTypes.Email, Required = true },
                            new PipelineFormField { Label = "Plan", Name = "plan", Type = PipelineFormFieldTypes.Dropdown, Options = ["Free", "Pro"], Required = true },
                        ]),
                        ["SubmitLabel"] = "Finish",
                    },
                    OutputVariables = { ["Result"] = "v-second" },
                },
                new PipelineGraphStep
                {
                    Id = "log", Type = "Stomme.LogMessage", Name = "Say who signed up", Position = At(640, 80),
                    InputExpressions = { ["Message"] = "{{ first.name }} <{{ second.email }}> signed up for {{ second.plan }}, from {{ first.source }}." },
                },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "form", TargetId = "page-two" },
                new PipelineGraphConnection { SourceId = "page-two", SourcePort = "Submitted", TargetId = "log" },
            },
            Notes = { Note("The form needs Pipelines:Forms:Enabled. Its address can say where a person came from: .../forms/signup?source=newsletter - the hidden field carries it, 'web' when the address says nothing.", 80, 260) },
        };

        // n8n's Guardrails - a message cleaned and checked before the site's language model sees it.
        private static PipelineGraph ScreenBeforeAi() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-clean", Name = "clean" }, new PipelineGraphVariable { Id = "v-answer", Name = "answer" } },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When a message comes", Position = At(80, 160), Inputs = { ["WebhookName"] = "ask" } },
                new PipelineGraphStep
                {
                    Id = "clean", Type = "Stomme.GuardrailSanitize", Name = "Take out personal data and keys", Position = At(360, 160),
                    Inputs = { ["PersonalData"] = "all", ["SecretKeys"] = "Balanced" },
                    OutputVariables = { ["Result"] = "v-clean" },
                },
                new PipelineGraphStep
                {
                    Id = "check", Type = "Stomme.GuardrailCheck", Name = "Safe and on topic?", Position = At(660, 160),
                    Inputs = { ["Jailbreak"] = "true", ["Topic"] = "questions about our products, orders and deliveries" },
                },
                new PipelineGraphStep
                {
                    Id = "ask", Type = "Stomme.AskAi", Name = "Answer it", Position = At(960, 60),
                    Inputs = { ["Instructions"] = "You answer customers' questions about our products, orders and deliveries, in a few friendly sentences." },
                    InputExpressions = { ["Prompt"] = "{{ clean }}" },
                    OutputVariables = { ["Result"] = "v-answer" },
                },
                new PipelineGraphStep { Id = "answered", Type = "Stomme.LogMessage", Name = "The answer", Position = At(1260, 60), InputExpressions = { ["Message"] = "{{ answer }}" } },
                new PipelineGraphStep { Id = "flagged", Type = "Stomme.LogMessage", Name = "Not answered", Position = At(960, 260), Inputs = { ["Level"] = "Warning", ["Message"] = "A message was not answered: it tried to steer the model, or strayed from the topic." } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "clean" },
                new PipelineGraphConnection { SourceId = "clean", SourcePort = "Done", TargetId = "check" },
                new PipelineGraphConnection { SourceId = "check", SourcePort = "Passed", TargetId = "ask" },
                new PipelineGraphConnection { SourceId = "ask", SourcePort = "Done", TargetId = "answered" },
                new PipelineGraphConnection { SourceId = "check", SourcePort = "Flagged", TargetId = "flagged" },
            },
            Notes = { Note("The webhook needs Pipelines:Webhooks:Enabled, and the checks and the answer the site's assistant switched on with a model (Admin › Assistant › Settings). What the guardrails find is listed by kind and place in their Findings output - never the value they found.", 80, 380) },
        };

        // n8n's most common template - a Telegram bot answered by a model: the message in, the model's answer back to its
        // chat in reply. Both steps name the bot's connection, which a starter cannot: the author chooses it, the editor saying so.
        private static PipelineGraph TelegramAiBot() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-message", Name = "message" }, new PipelineGraphVariable { Id = "v-answer", Name = "answer" } },
            Steps =
            {
                new PipelineGraphStep { Id = "bot", Type = PipelineTelegramEvent.StepType, Name = "A message to the bot", Position = At(80, 80), OutputVariables = { ["Result"] = "v-message" } },
                new PipelineGraphStep
                {
                    Id = "ask", Type = "Stomme.AskAi", Name = "Answer it", Position = At(380, 80),
                    Inputs = { ["Instructions"] = "You are the shop's assistant on Telegram. Answer customers' questions about our products, orders and deliveries in a few friendly sentences." },
                    InputExpressions = { ["Prompt"] = "{{ message.text }}" },
                    OutputVariables = { ["Result"] = "v-answer" },
                },
                new PipelineGraphStep
                {
                    Id = "reply", Type = "Stomme.SendChatMessage", Name = "Send the answer", Position = At(680, 80),
                    InputExpressions = { ["To"] = "{{ message.chat.id }}", ["Text"] = "{{ answer }}", ["ReplyTo"] = "{{ message.messageId }}" },
                },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "bot", TargetId = "ask" },
                new PipelineGraphConnection { SourceId = "ask", SourcePort = "Done", TargetId = "reply" },
            },
            Notes = { Note("Choose the bot's Telegram connection in 'A message to the bot' and in 'Send the answer' (Admin › Connections: the token @BotFather gave you), and switch on the site's assistant with a model (Admin › Assistant › Settings). The bot is asked for its messages every few seconds - it needs no webhook, and one set elsewhere stops it.", 80, 300) },
        };

        // n8n's HTML node - records as a table, every value encoded, in a page.
        private static PipelineGraph OrdersReport() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-table", Name = "table" } },
            Steps =
            {
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "Read the orders", Position = At(80, 80), Inputs = { ["Path"] = "orders.csv" } },
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse them", Position = At(360, 80) },
                new PipelineGraphStep
                {
                    Id = "table", Type = "Stomme.MakeHtml", Name = "As a table", Position = At(640, 80),
                    Inputs = { ["Caption"] = "Orders", ["CapitalizeHeaders"] = "true" },
                    OutputVariables = { ["Result"] = "v-table" },
                },
                new PipelineGraphStep
                {
                    Id = "page", Type = "Stomme.MakeHtml", Name = "In a page", Position = At(920, 80),
                    Inputs = { ["Make"] = "Template" },
                    InputExpressions = { ["Html"] = "<h1>Orders on {{ $today | html }}</h1>\n{{ table | raw }}" },
                },
                new PipelineGraphStep { Id = "write", Type = "Stomme.WriteTextFile", Name = "Write the page", Position = At(1200, 80), Inputs = { ["Path"] = "orders.html" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "parse" },
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "table" },
                new PipelineGraphConnection { SourceId = "table", SourcePort = "Done", TargetId = "page" },
                new PipelineGraphConnection { SourceId = "page", SourcePort = "Done", TargetId = "write" },
            },
            Notes = { Note("Put orders.csv in the pipeline folder (Admin › Pipelines › Files). The table encodes every value it is given; in the page, | html encodes a value and | raw puts in HTML a step made. Send the page with Send notification, or keep it as it is.", 80, 260) },
        };

        private static PipelineGraph ContactForm() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-message", Name = "message" } },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "form", Type = PipelineForms.StepType, Name = "Contact form", Position = At(80, 80),
                    Inputs =
                    {
                        ["FormName"] = "contact",
                        ["Title"] = "Contact us",
                        ["Description"] = "Write to us - we answer within a working day.",
                        ["Fields"] = PipelineForms.Serialize(
                        [
                            new PipelineFormField { Label = "Your name", Name = "name", Required = true },
                            new PipelineFormField { Label = "E-mail", Name = "email", Type = PipelineFormFieldTypes.Email, Required = true },
                            new PipelineFormField { Label = "About", Name = "topic", Type = PipelineFormFieldTypes.Dropdown, Options = ["A question", "An order", "Something else"] },
                            new PipelineFormField { Label = "Message", Name = "message", Type = PipelineFormFieldTypes.LongText, Required = true },
                        ]),
                        ["SubmitLabel"] = "Send",
                        ["Answer"] = "Thank you - we have your message and answer within a working day.",
                    },
                    OutputVariables = { ["Result"] = "v-message" },
                },
                new PipelineGraphStep
                {
                    Id = "keep", Type = "Stomme.WriteTextFile", Name = "Keep the message", Position = At(360, 80),
                    InputExpressions = { ["Path"] = "contact/{{ $now | date: \"yyyy-MM-dd-HHmmss\" }}.json", ["Content"] = "{{ message }}" },
                },
                new PipelineGraphStep
                {
                    Id = "log", Type = "Stomme.LogMessage", Name = "Say who wrote", Position = At(640, 80),
                    InputExpressions = { ["Message"] = "A message from {{ message.name }} <{{ message.email }}> about {{ message.topic | default: \"something\" }}." },
                },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "form", TargetId = "keep" },
                new PipelineGraphConnection { SourceId = "keep", SourcePort = "Done", TargetId = "log" },
            },
            Notes = { Note("The site serves forms with Pipelines:Forms:Enabled. Publish, then select the form step to open its address; to test the next steps first, use a sample submission.", 80, 260) },
        };

        private static PipelineGraph ChatAssistant() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-message", Name = "message" },
                new PipelineGraphVariable { Id = "v-session", Name = "session" },
            },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "chat", Type = "Stomme.ChatTrigger", Name = "A visitor writes", Position = At(80, 80),
                    Inputs = { ["ChatName"] = "assistant", ["Title"] = "Ask us", ["Welcome"] = "Hello! Ask me anything - I remember what we said." },
                    OutputVariables = { ["Result"] = "v-message", ["Session"] = "v-session" },
                },
                new PipelineGraphStep
                {
                    Id = "agent", Type = "Stomme.AiAgent", Name = "Answer", Position = At(360, 80),
                    Inputs = { ["Instructions"] = "You are this site's assistant. Answer briefly and kindly; say so when you do not know." },
                    InputExpressions = { ["Prompt"] = "{{ message }}", ["MemoryKey"] = "{{ session }}" },
                },
            },
            Connections = { new PipelineGraphConnection { SourceId = "chat", TargetId = "agent" } },
            Notes = { Note("The site serves chats with Pipelines:Chats:Enabled, and the agent needs the site's AI assistant set up. Publish, then select the chat step to open its page. Give the agent tools - pipelines that start with \"AI agent tool\" - to let it look things up.", 80, 260) },
        };

        private static GraphPosition At(double x, double y) => new() { X = x, Y = y };

        private static PipelineGraphNote Note(string text, double x, double y) => new() { Id = "note-1", Text = text, Position = At(x, y) };
    }
}
