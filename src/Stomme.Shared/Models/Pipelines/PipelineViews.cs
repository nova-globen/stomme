using System;
using System.Collections.Generic;

namespace Stomme.Models.Pipelines
{
    /// <summary>A pipeline as the site sees it: its LATEST version, with whether that version is published.</summary>
    public class PipelineView
    {
        /// <summary>The version id (the engine's <c>PipelineDefinition.Id</c>).</summary>
        public string Id { get; set; }

        /// <summary>The definition id, stable across versions.</summary>
        public string DefinitionId { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public int Version { get; set; }

        public bool IsLatest { get; set; }

        public bool IsPublished { get; set; }

        public DateTime CreatedOnUtc { get; set; }

        /// <summary>Its tags (<see cref="PipelineTags"/>) - on the pipelines list; empty elsewhere.</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>n8n's folders: the folder it sits in on the pipelines list (<see cref="PipelineFolders"/>); empty at the top, and elsewhere.</summary>
        public string Folder { get; set; } = "";

        /// <summary>Its newest run - when the list was asked for it (the pipelines page); null otherwise, and for none.</summary>
        public PipelineRunView LastRun { get; set; }

        /// <summary>Its runs waiting their turn - Queued (a pipeline that runs one at a time, the rest in turn); read with the last runs.</summary>
        public int Queued { get; set; }

        /// <summary>
        /// n8n's archive: when the pipeline was set aside - unpublished, out of the list until it is restored; null when it is
        /// not. An archived pipeline is not published again, nor so run, and the list deletes a pipeline from its archive.
        /// </summary>
        public DateTime? ArchivedOnUtc { get; set; }

        /// <summary>Who archived it, when it is.</summary>
        public string ArchivedBy { get; set; }
    }

    /// <summary>A pipeline with its body: the engine's serialised definition, which the editor reads and writes.</summary>
    public class PipelineDetail : PipelineView
    {
        public string Body { get; set; }
    }

    /// <summary>n8n's folders: where a pipeline is moved to (<see cref="PipelineFolders"/>); empty for the top.</summary>
    /// <summary>
    /// n8n's folder rename and move, and delete: <see cref="Folder"/> becomes <see cref="NewFolder"/> (a path - another
    /// name, or another place), what is in it going along; or, with <see cref="Remove"/>, it is gone and what was in it is in the
    /// folder above. A pipeline is never deleted with a folder.
    /// </summary>
    public class PipelineFolderMoveRequest
    {
        public string Folder { get; set; } = "";

        public string NewFolder { get; set; } = "";

        public bool Remove { get; set; }
    }

    /// <summary>Where the folder's pipelines are now (the folder renamed, or the one above a folder removed) and how many moved.</summary>
    public class PipelineFolderMoveResult
    {
        public string Folder { get; set; } = "";

        public int Moved { get; set; }
    }

    public class PipelineFolderRequest
    {
        public string Folder { get; set; } = "";
    }

    public class CreatePipelineRequest
    {
        public string Name { get; set; }

        public string Description { get; set; }
    }

    /// <summary>The module's own input hints, beside the engine's: what the editor draws for an input that names one.</summary>
    public static class PipelineInputHints
    {
        /// <summary>One of the site's connections (Admin > Connections), chosen by name, held by its id.</summary>
        public const string ConnectionPicker = "connection-picker";

        /// <summary>A condition's rules held as the input's value and tested on each record (Filter records) - built with the rules an If's are.</summary>
        public const string RecordCondition = "record-condition";

        /// <summary>A form's fields (the Form trigger) held as the input's value: each one's label, kind, whether it is required and its choices.</summary>
        public const string FormFields = "form-fields";

        /// <summary>An AI agent's tools: names, one per line, with the site's published AI agent tools offered to add.</summary>
        public const string AgentTools = "agent-tools";

        /// <summary>One of the site's data tables (n8n's Data tables), chosen by name, held by its name.</summary>
        public const string SiteTablePicker = "site-table-picker";

        /// <summary>
        /// A path in the site's pipeline folder (the Files page's folder) - a file to read or write, or a folder - typed,
        /// or chosen from the folder; held as the path, relative to it.
        /// </summary>
        public const string PipelineFile = "pipeline-file";

        /// <summary>
        /// The engine's own hint for a step property that holds one of the pipeline's variables itself - Set Variable's Variable,
        /// the variable the step sets: chosen among the pipeline's variables, never a value, an expression or a script.
        /// </summary>
        public const string VariablePicker = "variable-picker";

        /// <summary>
        /// A SQL statement (Extract from connection's Query): drawn as code, line by line; an expression writing a value
        /// into it is warned of (<see cref="PipelineGraphProblems.ValueInSql"/>) - a value goes in as a bound <c>@name</c>.
        /// </summary>
        public const string SqlQuery = "sql-query";

        /// <summary>
        /// The operation of the connection the step names (Extract from connection's Operation): chosen among that
        /// connection's connector's own, each saying what it does and what it takes; typed when the connector is not known.
        /// </summary>
        public const string ConnectorOperation = "connector-operation";
    }

    /// <summary>One of the site's AI agent tools - a published pipeline that starts with "AI agent tool".</summary>
    public class AgentToolView
    {
        /// <summary>The name the model calls it by.</summary>
        public string Name { get; set; }

        /// <summary>What it does - what the model reads to decide.</summary>
        public string Description { get; set; }

        /// <summary>The pipeline it runs.</summary>
        public string DefinitionId { get; set; }

        public string PipelineName { get; set; }
    }

    /// <summary>What this site lets the editor offer: JavaScript expressions, and receiving webhooks.</summary>
    public class PipelineCapabilitiesView
    {
        /// <summary><c>Pipelines:Scripts:Enabled</c> - the Run script step and JavaScript expressions run.</summary>
        public bool Scripts { get; set; }

        /// <summary><c>Pipelines:Webhooks:Enabled</c>.</summary>
        public bool Webhooks { get; set; }

        /// <summary>
        /// The site's model can build in the editor for the person asking - the AI assistant on for the installation and
        /// this site, and the person holding the assistant's own key (<c>assistant:use</c>), which a module's page cannot read
        /// itself (it reads its own module's keys). The server asks the same again of every request.
        /// </summary>
        public bool Ai { get; set; }
    }

    /// <summary>n8n's AI workflow builder: what the author asks of the pipeline on the canvas, and the canvas as it stands.</summary>
    public class PipelineGraphDesignRequest
    {
        public PipelineGraph Graph { get; set; }

        public string Instruction { get; set; }

        /// <summary>The conversation so far, oldest first - what was asked and what the AI said - kept by the editor, not stored.</summary>
        public List<PipelineAiTurn> Earlier { get; set; } = new List<PipelineAiTurn>();
    }

    /// <summary>One exchange with the editor's AI: what the author asked, and what the AI said.</summary>
    public class PipelineAiTurn
    {
        public string Asked { get; set; }

        public string Said { get; set; }
    }

    /// <summary>
    /// What the site's model made of the canvas: the pipeline as it would be, judged by the save's own rule before it came back,
    /// and what the model says it did. Nothing is stored - the author keeps it by saving, or undoes it.
    /// </summary>
    public class PipelineGraphDesignView
    {
        /// <summary>The pipeline changed; null when the model answered without changing anything (a question, or one asked back).</summary>
        public PipelineGraph Graph { get; set; }

        /// <summary>The model's own words: what it changed, and anything left for the author (a key to enter, a choice to make).</summary>
        public string Summary { get; set; }
    }

    /// <summary>A JavaScript expression to preview, with the variables' values as text, by name.</summary>
    public class ScriptPreviewRequest
    {
        public string Script { get; set; }

        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>What a JavaScript expression gives, as text - or why it gives nothing.</summary>
    public class ScriptPreviewView
    {
        public string Value { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// n8n's "Ask AI" in its Code node: what the author wants a Run script step's script to do, with what the script can
    /// see - what it is written for, the names of its extra variables and the shape of what it receives (field names and kinds,
    /// never a value). The script as it stands goes too, so a change can be asked of it.
    /// </summary>
    public class PipelineScriptRequest
    {
        public string Instruction { get; set; }

        /// <summary>The script as it stands; empty for a new one.</summary>
        public string Script { get; set; }

        /// <summary>What it is written for, the Run script step's Style: <c>JavaScript</c> (empty too), <c>N8nAllItems</c>, <c>N8nEachItem</c>.</summary>
        public string Style { get; set; }

        /// <summary>The names its Variables lines give - never their values.</summary>
        public List<string> Variables { get; set; } = new List<string>();

        /// <summary>What it receives, as the editor last saw it (<see cref="PipelineScriptInput"/>): records, one record, text, or empty - not seen.</summary>
        public string Receives { get; set; }

        /// <summary>The fields of the records it receives - names and kinds only.</summary>
        public List<PipelineScriptField> Fields { get; set; } = new List<PipelineScriptField>();
    }

    /// <summary>A field of what a script receives: its name and what its values are (<see cref="PipelineDataTables.Kinds"/>).</summary>
    public class PipelineScriptField
    {
        public string Name { get; set; }

        public string Kind { get; set; }
    }

    /// <summary>n8n's Code node linting: scripts to check they parse as their step runs them, nothing run.</summary>
    public class PipelineScriptCheckRequest
    {
        public List<PipelineScriptCheck> Scripts { get; set; } = new List<PipelineScriptCheck>();
    }

    /// <summary>A script and what it is written for, as <see cref="PipelineScriptRequest.Style"/> says it (an input's JavaScript: empty).</summary>
    public class PipelineScriptCheck
    {
        public string Script { get; set; }

        public string Style { get; set; }

        /// <summary>
        /// A Run script step's variables' names, to say a name the script reads and can see nowhere; null for a script whose names
        /// are not all known here (an input's JavaScript reads every variable in scope), which is checked to parse only.
        /// </summary>
        public List<string> Names { get; set; }
    }

    /// <summary>Why each script asked about does not parse, in the order asked - null for one that does.</summary>
    public class PipelineScriptCheckView
    {
        public List<string> Problems { get; set; } = new List<string>();
    }

    /// <summary>The script the site's model wrote - stored nowhere and run by nothing until the author keeps and runs it.</summary>
    public class PipelineScriptView
    {
        public string Script { get; set; }

        /// <summary>Why it cannot run as written - it does not parse, said with where; null when it parses.</summary>
        public string Problem { get; set; }
    }

    /// <summary>
    /// What the editor says about a trigger step: a webhook's address and how a call must be signed, whether
    /// the site receives webhooks at all; a schedule's next runs. Never the webhook secret - only whether one is configured.
    /// </summary>
    public class TriggerInfoView
    {
        /// <summary><c>Webhook</c>, <c>Schedule</c>, or empty for a step that is neither.</summary>
        public string Kind { get; set; }

        /// <summary>Why the trigger cannot work as it stands (a name or a cron that is not one); null when it can.</summary>
        public string Error { get; set; }

        /// <summary>The site receives webhooks (<c>Pipelines:Webhooks:Enabled</c>) / evaluates schedules (<c>Pipelines:Schedules:Enabled</c>).</summary>
        public bool Enabled { get; set; }

        /// <summary>Webhook: the address's path under the site, e.g. <c>api/Pipelines/webhooks/incoming</c>.</summary>
        public string Path { get; set; }

        /// <summary>Webhook: the header each call carries its signature in.</summary>
        public string SignatureHeader { get; set; }

        /// <summary>Webhook: a secret is configured - its own or the installation's - so a signed call can be received; without one every call is refused.</summary>
        public bool Signed { get; set; }

        /// <summary>Webhook: it has a secret of its own, and calls are verified under that, not the installation's.</summary>
        public bool OwnSecret { get; set; }

        /// <summary>Webhook: its own secret cannot be read (the host's key ring changed), so every call is refused until a new one is made.</summary>
        public bool OwnSecretUnreadable { get; set; }

        /// <summary>
        /// Webhook: the secret is to come from a file (<c>Pipelines:Webhooks:SecretFile</c>) - so when it is not <see cref="Signed"/>,
        /// that file is missing, unreadable or empty, not a secret never configured. The file's path is the host's, never shown.
        /// </summary>
        public bool SecretFromFile { get; set; }

        /// <summary>Webhook: the callers it admits are limited by an allowlist.</summary>
        public bool Allowlisted { get; set; }

        /// <summary>Schedule: its next runs, UTC.</summary>
        public List<DateTime> NextRunsUtc { get; set; } = new List<DateTime>();

        /// <summary>Schedule: the time zone it is read in, as given; null for UTC.</summary>
        public string TimeZone { get; set; }

        /// <summary>Schedule: its next runs as the clock in <see cref="TimeZone"/> reads them; empty for UTC.</summary>
        public List<DateTime> NextRunsInZone { get; set; } = new List<DateTime>();

        /// <summary>Schedule: how often the site's schedules are checked.</summary>
        public int CheckedEverySeconds { get; set; }
    }

    /// <summary>A webhook's new secret of its own, given out this once - the site keeps it protected and never shows it again.</summary>
    public class PipelineWebhookSecretView
    {
        public string Name { get; set; }

        public string Secret { get; set; }

        // A view must never print the secret (a logged view would leak it).
        public override string ToString() => $"PipelineWebhookSecretView {{ Name = {Name} }}";
    }

    /// <summary>One call a webhook received while the editor listened, or that none came.</summary>
    public class WebhookCallView
    {
        public bool Received { get; set; }

        public DateTime ReceivedAtUtc { get; set; }

        public string ContentType { get; set; }

        /// <summary>The body as text, at most <see cref="MaxBodyChars"/> characters of it.</summary>
        public string Body { get; set; }

        public bool Truncated { get; set; }

        /// <summary>Why nothing was listened for (webhooks off, a name that is not one); null otherwise.</summary>
        public string Error { get; set; }

        public const int MaxBodyChars = 64 * 1024;
    }

    /// <summary>A new pipeline with a body: a file imported, or a starter.</summary>
    public class ImportPipelineRequest
    {
        public string Name { get; set; }

        public string Description { get; set; }

        public PipelineGraph Graph { get; set; }
    }

    public class UpdatePipelineRequest
    {
        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary>The engine's serialised definition; null leaves the current body as it is.</summary>
        public string Body { get; set; }
    }

    public class PublishResult
    {
        public bool Succeeded { get; set; }

        public List<string> Errors { get; set; } = new List<string>();
    }

    public class StartPipelineRequest
    {
        public string CorrelationId { get; set; }

        public string Name { get; set; }

        /// <summary>The run's input, by name. Values are strings here; a typed input is the editor's job.</summary>
        public Dictionary<string, string> Input { get; set; }
    }

    /// <summary>What starting a run returns: the id to poll, never the run itself (a run is dispatched, not awaited).</summary>
    public class PipelineRunStarted
    {
        public string RunId { get; set; }

        /// <summary>The run waits its turn - its pipeline runs one at a time, the rest in turn, and a run of it is going.</summary>
        public bool Queued { get; set; }
    }

    public class PipelineRunView
    {
        public string Id { get; set; }

        /// <summary>What the run saved about itself with "Save run data" (<see cref="PipelineRunData"/>) - key and value.</summary>
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();

        /// <summary>What a person said about the run (<see cref="PipelineRunReview"/>, n8n's execution annotations); null when nobody has.</summary>
        public PipelineRunReview Review { get; set; }

        public string DefinitionId { get; set; }

        public string DefinitionVersionId { get; set; }

        public int Version { get; set; }

        public string Name { get; set; }

        public string CorrelationId { get; set; }

        /// <summary>The engine's status: Running or Finished.</summary>
        public string Status { get; set; }

        /// <summary>The engine's sub-status: Executing, Suspended, Pending, Finished, Faulted, Cancelled.</summary>
        public string SubStatus { get; set; }

        public int IncidentCount { get; set; }

        public DateTime CreatedOnUtc { get; set; }

        public DateTime UpdatedOnUtc { get; set; }

        public DateTime? FinishedOnUtc { get; set; }

        public bool IsFinished => Status == "Finished";

        /// <summary>The pipeline's name (its latest version's), so a list of every pipeline's runs says whose each is.</summary>
        public string PipelineName { get; set; }

        /// <summary>What the run was started with, as JSON (n8n's execution data) - one run read by an author only; null otherwise.</summary>
        public string InputJson { get; set; }

        /// <summary>
        /// What the run was started with was emptied by its pipeline's setting (<see cref="PipelineStepDataKeeping"/>) - its
        /// page says so, and it is not run again or gone on with without it.
        /// </summary>
        public bool InputNotKept { get; set; }

        /// <summary>
        /// The pipeline's published version now, on one run's read - when it is not the one the run ran, the run's page
        /// also offers to run it again on its own version (n8n's retry with the original workflow); null when none is published.
        /// </summary>
        public int? PublishedVersion { get; set; }

        /// <summary>For a run a "Pipeline failed" trigger started: the run that failed, and its pipeline's name.</summary>
        public string FailedRunId { get; set; }

        public string FailedPipelineName { get; set; }

        /// <summary>For a run another pipeline's run started (Execute or Dispatch Pipeline), that run and its pipeline's name.</summary>
        public string ParentRunId { get; set; }

        public string ParentPipelineName { get; set; }

        /// <summary>For a run that went on from a failed run's failed step, the failed run - where the steps before it ran.</summary>
        public string RetryOfRunId { get; set; }

        /// <summary>The runs this one started - its sub-pipelines' (one run read only; empty in a list).</summary>
        public List<PipelineRunView> ChildRuns { get; set; } = new List<PipelineRunView>();
    }

    /// <summary>
    /// One version of a pipeline: its number, whether it is published or the latest, when it was made and
    /// who last saved it - recorded from 10.11.0 on, so empty for a version saved before.
    /// </summary>
    public class PipelineVersionView
    {
        public string Id { get; set; }

        public int Version { get; set; }

        public bool IsPublished { get; set; }

        public bool IsLatest { get; set; }

        public DateTime CreatedOnUtc { get; set; }

        public string SavedBy { get; set; }

        /// <summary>What the author said changed when publishing it - shown for a version that was published; null for none.</summary>
        public string Note { get; set; }
    }

    /// <summary>A publish, with what changed in the author's words (optional, at most <see cref="MaxNoteLength"/>).</summary>
    public class PublishPipelineRequest
    {
        public const int MaxNoteLength = 500;

        public string Note { get; set; }
    }

    /// <summary>
    /// Test one step: the editor's graph as it stands, the step to test, and the values the pipeline's
    /// variables hold for the test, by variable NAME (a variable not named keeps its starting value).
    /// </summary>
    public class StepTestRequest
    {
        public PipelineGraph Graph { get; set; }

        public string StepId { get; set; }

        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();

        /// <summary>(n8n's test of a step with what leads to it) Every step that leads to this one runs first, then it.</summary>
        public bool UpToHere { get; set; }

        /// <summary>(n8n's "Test workflow") The whole pipeline as it stands, as a run would go through it; no step named.</summary>
        public bool Whole { get; set; }
    }

    /// <summary>
    /// What testing one step did: how it ended (<c>Finished</c>, <c>Faulted</c>, <c>Suspended</c> - a trigger waiting -, or
    /// <c>Refused</c> when it could not run at all), why, each step that ran as a run's steps are shown (data for an author
    /// only), and the log lines it wrote. Nothing of it is stored.
    /// </summary>
    public class StepTestResult
    {
        public string Status { get; set; }

        public string Error { get; set; }

        public List<PipelineStepRunView> Steps { get; set; } = new List<PipelineStepRunView>();

        public List<string> Log { get; set; } = new List<string>();
    }

    /// <summary>
    /// One execution of one step in a run: what the editor shows ON the step - its state and timing - and,
    /// to an author, what it received and what it produced. A step in a loop's body has one per pass.
    /// </summary>
    public class PipelineStepRunView
    {
        /// <summary>The graph step's id, so the editor finds the node it belongs to.</summary>
        public string StepId { get; set; }

        public string StepName { get; set; }

        public string StepType { get; set; }

        /// <summary>The engine's step status: <c>Running</c>, <c>Completed</c>, <c>Canceled</c> or <c>Faulted</c>.</summary>
        public string Status { get; set; }

        public DateTime StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>The fault's message, when the step faulted - or failed and the pipeline went on past it (<see cref="HandledFailure"/>).</summary>
        public string Error { get; set; }

        /// <summary>
        /// How a failure the step did not fault on was handled: <see cref="TookFailedPath"/> (n8n's error output) or
        /// <see cref="WentOn"/> (continue on error); null when it did not fail.
        /// </summary>
        public string HandledFailure { get; set; }

        /// <summary>
        /// The step is parked on a bookmark - a Wait until its moment, an event, a sub-pipeline it waits for - rather than working;
        /// the run is stored and resumes when that comes.
        /// </summary>
        public bool Waiting { get; set; }

        public const string TookFailedPath = "FailedPath";

        public const string WentOn = "WentOn";

        /// <summary>
        /// What the step received, as JSON - null unless the caller may author pipelines (<c>pipelines:manage</c>); an input the
        /// step marks as able to hold a secret is shown hidden, never its value.
        /// </summary>
        public string InputsJson { get; set; }

        /// <summary>What the step produced, as JSON - under the same rule as <see cref="InputsJson"/>.</summary>
        public string OutputsJson { get; set; }

        /// <summary>What the step received and produced was not kept - the pipeline keeps it only for a run that fails, or never.</summary>
        public bool DataNotKept { get; set; }

        /// <summary>True when a value was longer than the editor is sent and was cut.</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// How many records the step's result holds - its items, when it is a list - counted before anything is cut (n8n's
        /// "3 items" on the canvas); null when the result is no list, and under the same rule as <see cref="OutputsJson"/>.
        /// </summary>
        public int? ResultCount { get; set; }
    }

    public class PipelineRunLogLine
    {
        public long Sequence { get; set; }

        public DateTime TimestampUtc { get; set; }

        public string StepName { get; set; }

        public string StepType { get; set; }

        public string EventName { get; set; }

        public string Message { get; set; }
    }
}
