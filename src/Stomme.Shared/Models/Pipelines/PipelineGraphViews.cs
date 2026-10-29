using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A pipeline's body as the editor sees it: the engine's Flowchart, reduced to steps with
    /// literal inputs and a designer position, and the connections between them by step id and port. The server
    /// turns it into the engine's own step document and back; nothing engine-specific reaches the client.
    /// </summary>
    public class PipelineGraph
    {
        public List<PipelineGraphStep> Steps { get; set; } = new List<PipelineGraphStep>();

        public List<PipelineGraphConnection> Connections { get; set; } = new List<PipelineGraphConnection>();

        /// <summary>
        /// The pipeline's variables: how one step's output reaches another step's input. A step saves an output to a variable
        /// (<see cref="PipelineGraphStep.OutputVariables"/>), a later step reads it into an input (<see cref="PipelineGraphStep.InputVariables"/>).
        /// </summary>
        public List<PipelineGraphVariable> Variables { get; set; } = new List<PipelineGraphVariable>();

        /// <summary>Sticky notes on the canvas: text for the reader, never run.</summary>
        public List<PipelineGraphNote> Notes { get; set; } = new List<PipelineGraphNote>();

        /// <summary>
        /// How many runs of the pipeline may go at once (<see cref="PipelineRunsAtOnce"/>): empty for any number, or one
        /// at a time - in all, or per correlation id. A run waiting for a time or a decision is still going.
        /// </summary>
        public string RunsAtOnce { get; set; }

        /// <summary>
        /// n8n's "save successful executions": false deletes each run that finishes well a couple of minutes after it
        /// ends, with its log and run data - a pipeline that runs every minute leaves no trail of good runs; a run that fails or is
        /// cancelled is kept. Null: every run is kept (the site's retention still applies).
        /// </summary>
        public bool? KeepSuccessfulRuns { get; set; }

        /// <summary>
        /// n8n's "save execution progress": true stores a run after each step it runs, not only when its burst of work ends -
        /// a long run's steps show as they finish, and a run a host stops mid-way keeps what it had done. More writes per run, so it
        /// is off unless asked. Null: off.
        /// </summary>
        public bool? SaveProgress { get; set; }

        /// <summary>
        /// n8n Enterprise's execution data redaction: what the runs keep of each step's inputs and outputs -
        /// <see cref="PipelineStepDataKeeping.WhenFailed"/> (only a run that fails keeps them), <see cref="PipelineStepDataKeeping.Never"/>;
        /// null: always. The run, its log and its saved values are kept either way.
        /// </summary>
        public string KeepStepData { get; set; }

        /// <summary>
        /// n8n's "time saved per execution": the minutes of a person's work one run that finishes well saves, which
        /// Insights adds up over the runs of a day, a week or a month (<see cref="PipelineInsights.MinutesSaved"/>). Null: not said.
        /// </summary>
        public int? MinutesSavedPerRun { get; set; }

        /// <summary>
        /// n8n's "Timeout Workflow": a run busy longer than these minutes in one go is stopped - the time it waits does
        /// not count (<see cref="PipelineRunTimeout"/>). Null: no limit.
        /// </summary>
        public int? TimeoutMinutes { get; set; }

        /// <summary>
        /// n8n's "This workflow can be called by": which pipelines may call this one (<see cref="PipelineCallers"/>) - null
        /// for any pipeline on the site, <see cref="PipelineCallers.Nobody"/>, or the definition ids it admits.
        /// </summary>
        public string CalledBy { get; set; }

        /// <summary>
        /// n8n's workflow "Timezone": the zone the pipeline reads time in - <c>$now</c>, <c>$today</c>, a Schedule with no
        /// zone of its own (<see cref="PipelineTimeZone"/>). Null for UTC.
        /// </summary>
        public string TimeZone { get; set; }
    }

    /// <summary>
    /// A sticky note: text an author leaves on the canvas - in the pipeline's own flowchart or a body - for
    /// whoever reads the pipeline next. The engine never runs it; the codec keeps it in the flowchart's designer metadata.
    /// </summary>
    public class PipelineGraphNote
    {
        public string Id { get; set; }

        public string Text { get; set; }

        public GraphPosition Position { get; set; } = new GraphPosition();

        /// <summary>The step whose body the note is in, with <see cref="ParentPort"/>; null for the pipeline's own flowchart.</summary>
        public string Parent { get; set; }

        public string ParentPort { get; set; }

        /// <summary>n8n's sticky-note colours: one of <see cref="PipelineNoteColors.All"/>, or null for the theme's accent.</summary>
        public string Color { get; set; }
    }

    /// <summary>
    /// The colours a sticky note may take, as n8n's notes do - each one of the theme's own: green its "ok", amber its
    /// "warn", red its "bad", grey its quiet text. Null, or a name not among them, is the accent every note had before.
    /// </summary>
    public static class PipelineNoteColors
    {
        public const string Green = "green";

        public const string Amber = "amber";

        public const string Red = "red";

        public const string Grey = "grey";

        public static readonly IReadOnlyList<string> All = new[] { Green, Amber, Red, Grey };

        public static string Normalize(string color) =>
            All.FirstOrDefault(known => string.Equals(known, color?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A pipeline variable: the engine's flowchart variable, by id; its name is what an author sees.</summary>
    public class PipelineGraphVariable
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>What it holds: <c>Object</c> (anything, the default), <c>String</c>, <c>Int32</c>, <c>Double</c> or <c>Boolean</c>.</summary>
        public string TypeName { get; set; } = "Object";

        /// <summary>Its value before any step sets it, as text; null for none.</summary>
        public string Value { get; set; }

        /// <summary>
        /// (n8n's pinned data) The value an author pinned for testing steps - kept with the pipeline, in the flowchart's
        /// designer metadata, and never read by a run. It can be real data, so only an author is given it (a viewer's graph,
        /// version or export has none); null for none.
        /// </summary>
        public string TestValue { get; set; }
    }

    public class PipelineGraphStep
    {
        public string Id { get; set; }

        /// <summary>The step type name from the catalog, e.g. <c>Stomme.WriteLine</c>.</summary>
        public string Type { get; set; }

        public int Version { get; set; } = 1;

        public string Name { get; set; }

        public GraphPosition Position { get; set; } = new GraphPosition();

        /// <summary>A comment on the step for whoever reads the pipeline - shown on it, never run.</summary>
        public string Comment { get; set; }

        /// <summary>
        /// (n8n's pinned data) The result an author pinned for the step - kept with the pipeline, in the flowchart's
        /// designer metadata. A TEST of the pipeline hands it on in place of running the step (an API not called again, a model
        /// not asked again); a published run never reads it. It can be real data, so only an author is given it and a file never
        /// carries it. Null when nothing is pinned.
        /// </summary>
        public string PinnedResult { get; set; }

        /// <summary>The step as it is but with nothing pinned - for what leaves the pipeline (a file, a viewer's graph).</summary>
        public PipelineGraphStep WithoutPinnedResult()
        {
            var copy = (PipelineGraphStep)MemberwiseClone();
            copy.PinnedResult = null;
            return copy;
        }

        /// <summary>How many times a failed step is tried again (0 to 5)...</summary>
        public int Retries { get; set; }

        /// <summary>...how many seconds apart (0 to 60)...</summary>
        public int RetryDelaySeconds { get; set; }

        /// <summary>...and whether the pipeline goes on past it (its "done" outcome) when it still fails, the failure logged.</summary>
        public bool ContinueOnError { get; set; }

        /// <summary>
        /// n8n's "continue (using error output)": when the step still fails, it goes out through its <see cref="PipelineOutcomes.Failed"/>
        /// outcome - a port of its own on the canvas - so what to do about a failure is drawn as a branch. Wins over
        /// <see cref="ContinueOnError"/>.
        /// </summary>
        public bool FailedPath { get; set; }

        /// <summary>
        /// The variable (its id) a failure is saved to when the pipeline goes on past it - by either way - as JSON text,
        /// <c>{"error": "...", "step": "..."}</c>, read with <c>{{ failure.error }}</c>.
        /// </summary>
        public string FailureVariable { get; set; }

        /// <summary>
        /// Switched off (n8n's "deactivate"): kept in the pipeline, skipped when a run reaches it - the run going on through its
        /// first outcome - and, for a trigger, starting nothing.
        /// </summary>
        public bool Disabled { get; set; }

        /// <summary>Literal input values by input name, as text; the server types them by the catalog's input type.</summary>
        public Dictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>();

        /// <summary>Inputs that read a pipeline variable: input name to variable id. An input named here is not a literal.</summary>
        public Dictionary<string, string> InputVariables { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Inputs that are an EXPRESSION: input name to its template, text with <c>{{ variable.path }}</c>
        /// placeholders the step's run fills in (<see cref="PipelineTemplate"/>). An input named here is neither a literal nor a
        /// variable binding.
        /// </summary>
        public Dictionary<string, string> InputExpressions { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Inputs computed by a JavaScript expression: input name to the expression, run in the module's bounded
        /// sandbox with the pipeline's variables in scope - only where the site allows scripts. An input named here is none of the
        /// others.
        /// </summary>
        public Dictionary<string, string> InputScripts { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Inputs that are a CONDITION built from rules: input name to the condition - a Decision's, an If's,
        /// a While's. An input named here is neither a literal, a variable binding nor an expression.
        /// </summary>
        public Dictionary<string, PipelineCondition> InputConditions { get; set; } = new Dictionary<string, PipelineCondition>();

        /// <summary>
        /// A Switch (flow)'s cases, in order: each a label - one of the step's outcomes - and the condition
        /// that sends the step out through it. Empty for every other step.
        /// </summary>
        public List<PipelineGraphCase> Cases { get; set; } = new List<PipelineGraphCase>();

        /// <summary>Outputs saved to a pipeline variable: output name (the catalog's, e.g. <c>Result</c>) to variable id.</summary>
        public Dictionary<string, string> OutputVariables { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// The step this one sits inside - a loop whose body it is part of, an If whose branch it is part of - or null for a step
        /// of the pipeline itself. A graph stays one flat list: a body is the steps naming the same <see cref="Parent"/> and
        /// <see cref="ParentPort"/>, and the connections between them; the server nests them into the engine's document.
        /// </summary>
        public string Parent { get; set; }

        /// <summary>Which of the parent's bodies (the catalog entry's <see cref="StepCatalogEntry.Bodies"/>, e.g. <c>Body</c>, <c>Then</c>).</summary>
        public string ParentPort { get; set; }
    }

    /// <summary>A case of a Switch (flow): the outcome it names, and the condition that chooses it.</summary>
    public class PipelineGraphCase
    {
        public string Label { get; set; }

        public PipelineCondition Condition { get; set; } = new PipelineCondition();
    }

    public class GraphPosition
    {
        public double X { get; set; }

        public double Y { get; set; }
    }

    public class PipelineGraphConnection
    {
        public string SourceId { get; set; }

        /// <summary>The source step's outcome port (<c>Done</c>, <c>True</c>, <c>Body</c>...); null for a step with one outcome.</summary>
        public string SourcePort { get; set; }

        public string TargetId { get; set; }
    }

    /// <summary>A step type the editor can place: what the engine's registry describes, minus the CLR.</summary>
    public class StepCatalogEntry
    {
        public string Type { get; set; }

        public int Version { get; set; }

        public string DisplayName { get; set; }

        public string Category { get; set; }

        public string Description { get; set; }

        /// <summary>Action, Trigger, Task or Job.</summary>
        public string Kind { get; set; }

        /// <summary>The outcome ports a connection can leave from; empty means the one implicit outcome.</summary>
        public List<string> Ports { get; set; } = new List<string>();

        /// <summary>
        /// The bodies the step holds other steps in - a loop's <c>Body</c>, an If's <c>Then</c> and <c>Else</c> - each a flowchart of
        /// its own; empty for a step that holds none. Never an outcome: nothing connects to a body, steps are placed in it.
        /// </summary>
        public List<string> Bodies { get; set; } = new List<string>();

        /// <summary>
        /// Where the step's OWN outcomes come from - <see cref="BranchesOutcomes"/> (a Fork's branches, an input
        /// holding a list of names) or <see cref="CaseOutcomes"/> (a Switch's cases, beside its <see cref="Ports"/>); null for a step
        /// whose outcomes are its ports alone.
        /// </summary>
        public string OutcomesFrom { get; set; }

        public const string BranchesOutcomes = "Branches";

        public const string CaseOutcomes = "Cases";

        public List<StepInputView> Inputs { get; set; } = new List<StepInputView>();

        public List<StepOutputView> Outputs { get; set; } = new List<StepOutputView>();
    }

    public class StepInputView
    {
        public string Name { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        /// <summary>The CLR type's name (<c>String</c>, <c>Int32</c>, <c>Boolean</c>, <c>Object</c>...).</summary>
        public string TypeName { get; set; }

        /// <summary>The engine's hint for the control: singleline, multiline, checkbox, dropdown, or a picker the editor may not have.</summary>
        public string UIHint { get; set; }

        public string DefaultValue { get; set; }

        public bool IsSensitive { get; set; }

        public string Category { get; set; }

        public float Order { get; set; }

        /// <summary>The values a dropdown input takes (an enum's member names); null for a free input.</summary>
        public List<string> Options { get; set; }

        /// <summary>
        /// For a connection picker, the connector types the step can use (<c>slack</c>, <c>telegram</c>...): the picker offers
        /// only connections of those, and a connection made from the step is one of them. Null: any connection.
        /// </summary>
        public List<string> ConnectorTypes { get; set; }

        /// <summary>
        /// The step cannot do without it: the step gives it no starting value and its type holds no null.
        /// Left empty, it is a problem that keeps the pipeline from being published (<see cref="PipelineGraphProblems"/>).
        /// </summary>
        public bool IsRequired { get; set; }
    }

    public class StepOutputView
    {
        public string Name { get; set; }

        public string DisplayName { get; set; }

        public string TypeName { get; set; }
    }
}
