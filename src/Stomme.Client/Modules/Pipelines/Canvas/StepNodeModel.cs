using System;
using System.Collections.Generic;
using System.Linq;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// A step on the canvas (BL-95 increment (c)). The library supplies pan, zoom, hit-testing and link drawing; the
    /// model is the ENGINE's: a step type from the catalog, its literal inputs, and one output port per outcome the
    /// catalog names (or a single unnamed one), so a link drawn from a port is a connection through that port.
    /// </summary>
    public sealed class StepNodeModel : NodeModel
    {
        public StepNodeModel(string stepId, StepCatalogEntry entry, string type, string name, Point position)
            : base(position)
        {
            StepId = stepId;
            Entry = entry;
            Type = type;
            Name = name;
            Title = name;
            InputPort = (StepPortModel)AddPort(new StepPortModel(this, null, isInput: true, PortAlignment.Left));
            var outcomes = entry?.Ports?.Count > 0 ? entry.Ports : new List<string> { null };
            foreach (var outcome in outcomes)
            {
                AddPort(new StepPortModel(this, outcome, isInput: false, PortAlignment.Right));
            }
        }

        public string StepId { get; }

        /// <summary>The catalog entry; null when the installation no longer offers the type (the node is kept, greyed).</summary>
        public StepCatalogEntry Entry { get; }

        public string Type { get; }

        public string Name { get; private set; }

        public Dictionary<string, string> Inputs { get; } = new Dictionary<string, string>();

        /// <summary>Inputs that read a pipeline variable: input name to variable id.</summary>
        public Dictionary<string, string> InputVariables { get; } = new Dictionary<string, string>();

        /// <summary>BL-229 increment 5: inputs that are an expression - input name to its template.</summary>
        public Dictionary<string, string> InputExpressions { get; } = new Dictionary<string, string>();

        /// <summary>BL-229 increment 5b: inputs computed by JavaScript - input name to the expression.</summary>
        public Dictionary<string, string> InputScripts { get; } = new Dictionary<string, string>();

        /// <summary>BL-229 increment 10: inputs that are a condition built from rules - input name to the condition.</summary>
        public Dictionary<string, PipelineCondition> InputConditions { get; } = new Dictionary<string, PipelineCondition>();

        /// <summary>BL-229 increment 10: a Switch (flow)'s cases, in order - each one of its outcomes.</summary>
        public List<PipelineGraphCase> Cases { get; } = new List<PipelineGraphCase>();

        /// <summary>Outputs saved to a pipeline variable: output name to variable id.</summary>
        public Dictionary<string, string> OutputVariables { get; } = new Dictionary<string, string>();

        /// <summary>
        /// The outcomes the step names itself - a Fork's branches, a Switch's cases beside its Default - and
        /// its Failed path when it takes one, as its output ports, in their order (<see cref="PipelineOutcomes.Ports"/>). A port
        /// whose outcome is still named stays (with its connections), one no longer named goes and is returned so the editor
        /// removes its connections, a new one is added. A step whose ports are already those is left as it is.
        /// </summary>
        public IReadOnlyList<StepPortModel> SyncOutcomes()
        {
            var wanted = PipelineOutcomes.Ports(Entry, Inputs, Cases, FailedPath);
            if (OutputPorts.Select(port => port.Outcome).SequenceEqual(wanted))
            {
                return Array.Empty<StepPortModel>();
            }

            // The first port of each outcome is kept (two cases may share a label while it is typed); every other port goes.
            var current = OutputPorts.ToList();
            var kept = new Dictionary<string, StepPortModel>(StringComparer.Ordinal);
            foreach (var port in current.Where(port => wanted.Contains(port.Outcome)))
            {
                kept.TryAdd(port.Outcome ?? "\u0000", port);
            }

            var removed = current.Where(port => !kept.ContainsValue(port)).ToList();
            foreach (var port in current)
            {
                RemovePort(port);
            }

            // Re-added in the order the outcomes are named, the ports kept being the same objects - their connections with them.
            foreach (var outcome in wanted)
            {
                AddPort(kept.TryGetValue(outcome ?? "\u0000", out var port) ? port : new StepPortModel(this, outcome, isInput: false, PortAlignment.Right));
            }

            Refresh();
            return removed;
        }

        public StepPortModel InputPort { get; }

        public IEnumerable<StepPortModel> OutputPorts => Ports.OfType<StepPortModel>().Where(port => !port.IsInput);

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Entry?.DisplayName ?? Type) : Name;

        /// <summary>
        /// One of five shades of the theme's accent for the step's category, the same for every step of it
        /// (the theme's colours govern - a shade is the accent mixed toward the surface, never a colour of its own).
        /// </summary>
        public int CategoryShade => (Entry?.Category ?? "").Aggregate(0, (sum, letter) => (sum * 31 + letter) % 9973) % 5;

        /// <summary>
        /// What the run shown on the canvas did with this step - <c>completed</c>, <c>faulted</c>, <c>running</c>,
        /// <c>canceled</c> or <c>none</c> (not reached) - and a short note (its time, how many passes); null when no run is shown.
        /// </summary>
        public string RunStatus { get; private set; }

        public string RunNote { get; private set; }

        public void ShowRun(string status, string note)
        {
            if (RunStatus == status && RunNote == note)
            {
                return;
            }

            RunStatus = status;
            RunNote = note;
            Refresh();
        }

        /// <summary>
        /// The worst of this step's problems and of those in its bodies - <c>error</c> (keeps the pipeline
        /// from being published) or <c>warning</c> - or null for none; the badge's text and what it says on hover.
        /// </summary>
        public string ProblemSeverity { get; private set; }

        public string ProblemBadge { get; private set; }

        public string ProblemNote { get; private set; }

        public void ShowProblems(IReadOnlyCollection<PipelineGraphProblem> own, IReadOnlyCollection<PipelineGraphProblem> inside)
        {
            var all = own.Concat(inside).ToList();
            var severity = all.Count == 0 ? null : all.Any(problem => problem.Severity == PipelineGraphProblems.Error) ? "error" : "warning";
            var badge = string.Join(" · ", new[]
            {
                own.Count == 0 ? null : own.Count == 1 ? "1 problem" : own.Count + " problems",
                inside.Count == 0 ? null : inside.Count + " inside",
            }.Where(part => part is not null));
            var note = string.Join(Environment.NewLine, own.Select(problem => problem.Message)
                .Concat(inside.Select(problem => problem.Step + " (inside): " + problem.Message)));
            if (ProblemSeverity == severity && ProblemBadge == badge && ProblemNote == note)
            {
                return;
            }

            ProblemSeverity = severity;
            ProblemBadge = badge;
            ProblemNote = note;
            Refresh();
        }

        /// <summary>
        /// While a version is compared with the canvas, what changed on the step since - <c>added</c> (it is new) or
        /// <c>changed</c> (its own settings, or the steps its bodies hold) - its badge, and on hover what changed; null when no
        /// version is compared or nothing on it changed.
        /// </summary>
        public string Change { get; private set; }

        public string ChangeBadge { get; private set; }

        public string ChangeNote { get; private set; }

        public void ShowChange(string change, string badge, string note)
        {
            if (Change == change && ChangeBadge == badge && ChangeNote == note)
            {
                return;
            }

            Change = change;
            ChangeBadge = badge;
            ChangeNote = note;
            Refresh();
        }

        /// <summary>BL-229 increment 9: how often and how far apart a failed step is tried again, and whether the run goes on past it.</summary>
        public int Retries { get; private set; }

        public int RetryDelaySeconds { get; private set; }

        public bool ContinueOnError { get; private set; }

        /// <summary>n8n's error output: when it still fails, the step goes out through its Failed port (<see cref="SyncOutcomes"/>).</summary>
        public bool FailedPath { get; private set; }

        /// <summary>The variable (id) a failure is saved to when the pipeline goes on past it; null for none.</summary>
        public string FailureVariable { get; private set; }

        public void SetReliability(int retries, int retryDelaySeconds, bool continueOnError, bool failedPath = false, string failureVariable = null)
        {
            Retries = Math.Clamp(retries, 0, 5);
            RetryDelaySeconds = Math.Clamp(retryDelaySeconds, 0, 60);
            ContinueOnError = continueOnError && !failedPath;
            FailedPath = failedPath;
            FailureVariable = (continueOnError || failedPath) && !string.IsNullOrWhiteSpace(failureVariable) ? failureVariable : null;
            Refresh();
        }

        /// <summary>The settings in a word, on the step: "↻ 3 × 5 s", "goes on if it fails".</summary>
        public string ReliabilityNote => string.Join(" · ", new[]
        {
            Retries > 0 ? $"↻ {Retries}" + (RetryDelaySeconds > 0 ? $" × {RetryDelaySeconds} s" : "") : null,
            ContinueOnError ? "goes on if it fails" : null,
        }.Where(part => part is not null));

        /// <summary>Switched off (n8n's "deactivate"): kept, greyed, and skipped when the pipeline runs.</summary>
        public bool Disabled { get; private set; }

        /// <summary>BL-229: the step double-clicked - the editor opens its wide view (n8n opens a node's details so).</summary>
        public Action<StepNodeModel> Opened { get; set; }

        /// <summary>BL-229: the "+" on an outcome nothing follows - the editor adds the next step there (n8n's + after a node); null for a viewer.</summary>
        public Action<StepNodeModel, StepPortModel> AddAfter { get; set; }

        /// <summary>
        /// n8n's buttons over a step (<see cref="StepNode"/>): test it on its own (null where the author may not run it),
        /// switch it off or back on, delete it. Null on a viewer's canvas, which then shows none of them.
        /// </summary>
        public Action<StepNodeModel> Test { get; set; }

        public Action<StepNodeModel> Toggle { get; set; }

        public Action<StepNodeModel> Delete { get; set; }

        /// <summary>BL-229 - n8n's right-click on a step: the editor opens its menu where the pointer is (client x, y); null for a viewer.</summary>
        public Action<StepNodeModel, double, double> Menu { get; set; }

        public void SetDisabled(bool disabled)
        {
            Disabled = disabled;
            Refresh();
        }

        /// <summary>BL-229 increment 8: the author's comment on the step, shown on it.</summary>
        public string Comment { get; private set; }

        public void Annotate(string comment)
        {
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment;
            Refresh();
        }

        /// <summary>BL-229 (n8n's pinned data): the result a test hands on in place of running the step; null when none is pinned.</summary>
        public string PinnedResult { get; private set; }

        public void Pin(string result)
        {
            PinnedResult = string.IsNullOrWhiteSpace(result) ? null : result;
            Refresh();
        }

        /// <summary>BL-229: the step's discussion not yet resolved, as a count on it; 0 for none.</summary>
        public int OpenComments { get; private set; }

        /// <summary>BL-229: the open comments on the steps its bodies hold, at any depth - a loop's or a branch's; 0 for none.</summary>
        public int OpenCommentsInside { get; private set; }

        public void SetOpenComments(int open, int inside = 0)
        {
            if (OpenComments == open && OpenCommentsInside == inside)
            {
                return;
            }

            OpenComments = open;
            OpenCommentsInside = inside;
            Refresh();
        }

        public void Rename(string name)
        {
            Name = name;
            Title = DisplayName;
            Refresh();
        }
    }

    /// <summary>An input port (left) or an outcome port (right, named after the outcome). A link goes from an outcome to an input of another step.</summary>
    public sealed class StepPortModel : PortModel
    {
        public StepPortModel(NodeModel parent, string outcome, bool isInput, PortAlignment alignment)
            : base(parent, alignment)
        {
            Outcome = outcome;
            IsInput = isInput;
        }

        public string Outcome { get; private set; }

        /// <summary>BL-229 increment 10: a case or branch renamed - the same port, its connections with it, under the new name.</summary>
        public void Rename(string outcome)
        {
            Outcome = outcome;
            Refresh();
        }

        public bool IsInput { get; }

        public string Label => IsInput ? "in" : (Outcome ?? "done");

        public override bool CanAttachTo(ILinkable other) =>
            !IsInput && other is StepPortModel target && target.IsInput && !ReferenceEquals(target.Parent, Parent) && base.CanAttachTo(other);
    }
}
