#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Models.Pipelines;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - n8n's AI workflow builder, on the editor's canvas: a copy of the pipeline as it stands and the tools a model changes
    /// it with - find and describe a step type, read the pipeline, add, set, connect, remove, check - each held to the rule the
    /// editor holds the same change to, so what a model cannot do in the editor it cannot do here. The model sees no secret: an
    /// input the author alone fills (a sensitive one, a connection) is shown as set and refused when set. Nothing is stored here;
    /// what comes out is the editor's to keep or undo, and the save's to judge.
    /// </summary>
    /// <remarks>
    /// Steps are named to the model by short ids (<c>s1</c>, <c>s2</c>...) in the order they stand, so it never copies a 32-digit
    /// id; a step it adds gets a real id, as one the editor adds does, and the next short one. Every answer reaches the model AS
    /// WRITTEN (<see cref="AsWritten"/>): by default a tool's string is sent as a JSON string, quoted with every inner quote escaped
    /// (measured on the first drive), and JSON returned as JSON is sent pretty-printed (the second) - every character a metered
    /// token.
    /// </remarks>
    public sealed class PipelineGraphWorkbench
    {
        /// <summary>At most this many steps added in one request - a model that keeps adding is stopped, not trusted.</summary>
        public const int MaxStepsAdded = 40;

        /// <summary>At most this many step types answer one search.</summary>
        public const int MaxFound = 12;

        private const double ColumnWidth = 260;
        private const double RowHeight = 140;

        // What a model reads: JSON as written - an apostrophe stays one, not an escape (this is no HTML page) - nulls left out.
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly Dictionary<string, StepCatalogEntry> _types;
        private readonly Func<PipelineGraph, string?>? _judge;
        private readonly Func<string> _newId;
        private readonly Dictionary<string, string> _shortOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _idOf = new(StringComparer.OrdinalIgnoreCase);
        private int _added;

        /// <param name="graph">The canvas as it stands; copied, never changed.</param>
        /// <param name="catalog">The step types the editor offers.</param>
        /// <param name="judge">The save's own judgement of a graph: null when it would take it, else why not.</param>
        /// <param name="newId">A new step's id; the editor's kind by default.</param>
        public PipelineGraphWorkbench(PipelineGraph graph, IEnumerable<StepCatalogEntry> catalog, Func<PipelineGraph, string?>? judge = null, Func<string>? newId = null)
        {
            ArgumentNullException.ThrowIfNull(graph);
            Graph = JsonSerializer.Deserialize<PipelineGraph>(JsonSerializer.Serialize(graph, Json), Json) ?? new PipelineGraph();
            Graph.Steps ??= new List<PipelineGraphStep>();
            Graph.Connections ??= new List<PipelineGraphConnection>();
            Graph.Variables ??= new List<PipelineGraphVariable>();
            Graph.Notes ??= new List<PipelineGraphNote>();
            _types = (catalog ?? Enumerable.Empty<StepCatalogEntry>())
                .Where(entry => entry?.Type is not null)
                .GroupBy(entry => entry.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.Version).First(), StringComparer.Ordinal);
            _judge = judge;
            _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
            foreach (var step in Graph.Steps.Where(step => !string.IsNullOrWhiteSpace(step?.Id)))
            {
                Name(step.Id);
            }
        }

        /// <summary>The pipeline as the model has left it.</summary>
        public PipelineGraph Graph { get; }

        /// <summary>Whether any tool changed it.</summary>
        public bool Changed { get; private set; }

        /// <summary>What the model said it did, when it finished.</summary>
        public string? Summary { get; private set; }

        /// <summary>What the model is told: what a pipeline is here, and how to work on one.</summary>
        public const string Instructions =
            "You change a pipeline in Nova Globen Stomme's pipeline editor, as its author asks. A pipeline is a flowchart of steps. It starts at " +
            "its trigger (a step whose kind is Trigger: a webhook, a schedule, a form...) or, with none, at its first step; each step " +
            "runs, then the run goes on through one of its outcomes to the steps connected there. Data moves through variables: a " +
            "step saves an output to a variable (SaveOutput), and a later step reads it in an input as an expression: {{ order }}, " +
            "{{ order.total }}, or text such as 'Total: {{ order.total }}'. A loop's or an If's steps go inside its body (AddStep " +
            "with inside and body). Work like this: call ReadPipeline first. Choose steps with FindStepTypes and DescribeStepType, " +
            "and use only the types they return and only the inputs DescribeStepType lists. Change only what the author asked for " +
            "and leave the rest as it is; asked a question instead, change nothing and answer it in Finish - and ask back there " +
            "when the request is unclear. Never set an input marked authorOnly - the author enters secrets and connections in the " +
            "editor. Refer to steps by the short ids ReadPipeline and AddStep give (s1, s2...). Call Check, and fix every problem it " +
            "reports that you can. Then call Finish once, with a short summary in the author's language of what you changed and " +
            "what the author must still do.";

        /// <summary>A tool's answer handed to the model as the tool wrote it: compact JSON as JSON, a sentence as a sentence.</summary>
        private static readonly AIFunctionFactoryOptions AsWritten = new() { MarshalResult = (result, _, _) => new System.Threading.Tasks.ValueTask<object?>(result) };

        private static AIFunction Tool(Delegate method) => AIFunctionFactory.Create(method, AsWritten);

        /// <summary>The tools, as the model calls them.</summary>
        public IReadOnlyList<AITool> AsTools() =>
        [
            Tool(FindStepTypes),
            Tool(DescribeStepType),
            Tool(ReadPipeline),
            Tool(AddStep),
            Tool(SetInputs),
            Tool(SetCondition),
            Tool(SetCases),
            Tool(SaveOutput),
            Tool(SetVariable),
            Tool(RenameVariable),
            Tool(Connect),
            Tool(Disconnect),
            Tool(RemoveStep),
            Tool(RenameStep),
            Tool(Check),
            Tool(Finish),
        ];

        [Description("Finds step types by words in their name, category or description - e.g. 'http', 'send mail', 'loop', 'csv', 'schedule'. Returns at most 12: each one's type, name, category, kind (a Trigger starts the pipeline) and what it does.")]
        public string FindStepTypes([Description("The words to look for.")] string query)
        {
            var words = (query ?? string.Empty).Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(word => word.Trim()).Where(word => word.Length > 0).ToList();
            if (words.Count == 0)
            {
                return "Give a word to look for, e.g. http, mail, loop, csv, schedule. Categories: " + string.Join(", ", _types.Values.Select(entry => entry.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category, StringComparer.OrdinalIgnoreCase)) + ".";
            }

            var found = _types.Values
                .Select(entry => (Entry: entry, Score: Score(entry, words)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFound)
                .Select(match => new { type = match.Entry.Type, name = match.Entry.DisplayName, category = match.Entry.Category, kind = match.Entry.Kind, does = Short(match.Entry.Description, 200) })
                .ToList();
            return found.Count == 0
                ? $"No step type matches '{query}'. Try another word."
                : JsonSerializer.Serialize(found, Json);
        }

        [Description("Describes one step type: its inputs (name, type, whether required, the choices it takes), its outputs, its outcomes (what a connection leaves it by) and its bodies (where steps go inside it). Call it before adding or setting a step of that type.")]
        public string DescribeStepType([Description("The step type, as FindStepTypes gives it.")] string type)
        {
            if (!_types.TryGetValue((type ?? string.Empty).Trim(), out var entry))
            {
                return $"There is no step type '{type}'. Use FindStepTypes.";
            }

            return JsonSerializer.Serialize(new
            {
                type = entry.Type,
                name = entry.DisplayName,
                kind = entry.Kind,
                does = entry.Description,
                outcomes = entry.OutcomesFrom switch
                {
                    StepCatalogEntry.BranchesOutcomes => "one per branch named in its Branches input, all taken at once",
                    StepCatalogEntry.CaseOutcomes => "one per case (SetCases), and " + string.Join(", ", entry.Ports ?? new List<string>()),
                    _ => entry.Ports is { Count: > 0 } ports ? string.Join(", ", ports) : "one - connect it without a port",
                },
                bodies = entry.Bodies is { Count: > 0 } bodies ? bodies : null,
                inputs = (entry.Inputs ?? new List<StepInputView>()).Select(input => new
                {
                    name = input.Name,
                    type = input.TypeName,
                    required = input.IsRequired ? true : (bool?)null,
                    choices = input.Options is { Count: > 0 } options ? options : null,
                    @default = string.IsNullOrEmpty(input.DefaultValue) ? null : input.DefaultValue,
                    authorOnly = AuthorOnly(input) ? true : (bool?)null,
                    takes = input.UIHint == PipelineInputHints.VariablePicker ? "the name of the variable this step sets - made when the pipeline has none" : null,
                    condition = input.TypeName == "Boolean" ? "can be a condition (SetCondition)" : null,
                    about = Short(input.Description, 160),
                }),
                outputs = (entry.Outputs ?? new List<StepOutputView>()).Select(output => new { name = output.Name, type = output.TypeName }),
            }, Json);
        }

        [Description("Reads the pipeline as it stands now: its variables, its steps (by short id) with their inputs and the variables they save to, and the connections between them.")]
        public string ReadPipeline() =>
            JsonSerializer.Serialize(new
            {
                variables = Graph.Variables.Select(variable => new { name = variable.Name, type = variable.TypeName, startsAs = variable.Value }),
                steps = Graph.Steps.Where(step => !string.IsNullOrWhiteSpace(step?.Id)).Select(Describe),
                connections = Graph.Connections.Where(connection => connection is not null).Select(connection => new
                {
                    from = Name(connection.SourceId),
                    port = string.IsNullOrEmpty(connection.SourcePort) ? null : connection.SourcePort,
                    to = Name(connection.TargetId),
                }),
                notes = Graph.Notes.Where(note => !string.IsNullOrWhiteSpace(note?.Text)).Take(20).Select(note => Short(note.Text, 300)),
            }, Json);

        [Description("Adds a step and answers its id. Give 'after' (a step's id) to put it after that step, connected from it - through 'port' when that step has several outcomes. Give 'inside' (a step's id) and 'body' (one of its bodies, e.g. Body) to put it inside that step. Inputs are by name: a value as text, or an expression with {{ }} placeholders.")]
        public string AddStep(
            [Description("The step type, as FindStepTypes gives it.")] string type,
            [Description("What the step is called on the canvas, e.g. 'Send the report'.")] string name,
            [Description("The id of the step it follows, or nothing.")] string? after = null,
            [Description("The outcome of 'after' it follows, when that step has several.")] string? port = null,
            [Description("The id of the step whose body it goes inside, or nothing.")] string? inside = null,
            [Description("Which body of 'inside', e.g. Body, Then or Else.")] string? body = null,
            [Description("Input values by input name.")] Dictionary<string, string>? inputs = null)
        {
            if (!_types.TryGetValue((type ?? string.Empty).Trim(), out var entry))
            {
                return $"Not added: there is no step type '{type}'. Use FindStepTypes.";
            }

            if (_added >= MaxStepsAdded)
            {
                return $"Not added: at most {MaxStepsAdded} steps are added in one request.";
            }

            PipelineGraphStep? previous = null;
            if (!string.IsNullOrWhiteSpace(after) && (previous = Find(after)) is null)
            {
                return $"Not added: there is no step '{after}'. ReadPipeline lists them.";
            }

            string? parent = null;
            string? parentPort = null;
            if (!string.IsNullOrWhiteSpace(inside))
            {
                var holder = Find(inside);
                if (holder is null)
                {
                    return $"Not added: there is no step '{inside}'.";
                }

                var bodies = _types.TryGetValue(holder.Type ?? string.Empty, out var holderEntry) ? holderEntry.Bodies ?? new List<string>() : new List<string>();
                var chosen = string.IsNullOrWhiteSpace(body) && bodies.Count == 1 ? bodies[0] : bodies.FirstOrDefault(candidate => string.Equals(candidate, body?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (chosen is null)
                {
                    return bodies.Count == 0
                        ? $"Not added: '{Label(holder)}' holds no steps inside it."
                        : $"Not added: '{Label(holder)}' has the bodies {string.Join(", ", bodies)} - name one.";
                }

                parent = holder.Id;
                parentPort = chosen;
            }
            else if (previous is not null)
            {
                parent = previous.Parent;
                parentPort = previous.ParentPort;
            }

            string? outcome = null;
            if (previous is not null)
            {
                if (previous.Parent != parent || previous.ParentPort != parentPort)
                {
                    return $"Not added: '{Label(previous)}' is not in that body, so the new step cannot follow it there.";
                }

                outcome = Outcome(previous, port, out var outcomeError);
                if (outcomeError is not null)
                {
                    return "Not added: " + outcomeError;
                }
            }

            var step = new PipelineGraphStep
            {
                Id = _newId(),
                Type = entry.Type,
                Version = entry.Version,
                Name = string.IsNullOrWhiteSpace(name) ? entry.DisplayName : name.Trim(),
                Parent = parent,
                ParentPort = parentPort,
                Position = Place(parent, parentPort, previous),
            };

            var refused = new List<string>();
            foreach (var (input, value) in inputs ?? new Dictionary<string, string>())
            {
                if (Assign(step, entry, input, value) is { } problem)
                {
                    refused.Add(problem);
                }
            }

            Graph.Steps.Add(step);
            _added++;
            if (previous is not null)
            {
                Graph.Connections.Add(new PipelineGraphConnection { SourceId = previous.Id, SourcePort = outcome, TargetId = step.Id });
            }

            Changed = true;
            var said = $"Added '{step.Name}' as {Name(step.Id)}" + (previous is null ? "" : $", after '{Label(previous)}'" + (outcome is null ? "" : $" ({outcome})")) + ".";
            return refused.Count == 0 ? said : said + " Not set: " + string.Join(" ", refused);
        }

        [Description("Sets inputs of a step, by input name: a value as text, an expression with {{ }} placeholders, or an empty text to clear it.")]
        public string SetInputs(
            [Description("The step's id.")] string step,
            [Description("Input values by input name.")] Dictionary<string, string> inputs)
        {
            if (Find(step) is not { } found || !_types.TryGetValue(found.Type ?? string.Empty, out var entry))
            {
                return $"There is no step '{step}' whose type this site has.";
            }

            var set = new List<string>();
            var refused = new List<string>();
            foreach (var (input, value) in inputs ?? new Dictionary<string, string>())
            {
                if (Assign(found, entry, input, value) is { } problem)
                {
                    refused.Add(problem);
                }
                else
                {
                    set.Add(input);
                    Changed = true;
                }
            }

            return (set.Count == 0 ? "Nothing set." : $"Set {string.Join(", ", set)} on '{Label(found)}'.") + (refused.Count == 0 ? "" : " Not set: " + string.Join(" ", refused));
        }

        [Description("Makes a true-or-false input of a step (an If's or a Decision's Condition) a condition built from rules. Each rule compares 'left' (usually {{ variable.field }}) by an operator with 'right'. Operators: equals, not-equals, contains, not-contains, starts-with, ends-with, greater, at-least, less, at-most, empty, not-empty, true, false. 'match' is all (every rule holds) or any.")]
        public string SetCondition(
            [Description("The step's id.")] string step,
            [Description("The true-or-false input, e.g. Condition.")] string input,
            [Description("The rules.")] List<RuleArg> rules,
            [Description("all or any.")] string match = PipelineConditions.All)
        {
            if (Find(step) is not { } found || !_types.TryGetValue(found.Type ?? string.Empty, out var entry))
            {
                return $"There is no step '{step}' whose type this site has.";
            }

            var target = (entry.Inputs ?? new List<StepInputView>()).FirstOrDefault(candidate => string.Equals(candidate.Name, input?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null || target.TypeName != "Boolean")
            {
                return $"'{Label(found)}' has no true-or-false input '{input}'.";
            }

            if (Condition(rules, match, out var condition) is { } problem)
            {
                return "Not set: " + problem;
            }

            Clear(found, target.Name);
            found.InputConditions[target.Name] = condition;
            Changed = true;
            return $"{target.Name} of '{Label(found)}' is now: {PipelineConditions.Describe(condition)}.";
        }

        [Description("Sets a Switch (flow)'s cases, in order: each a label - the outcome its connection leaves by - and the rules that choose it. The run goes out through the first case that holds, else through Default.")]
        public string SetCases(
            [Description("The Switch (flow) step's id.")] string step,
            [Description("The cases, in order.")] List<CaseArg> cases)
        {
            if (Find(step) is not { } found || !_types.TryGetValue(found.Type ?? string.Empty, out var entry))
            {
                return $"There is no step '{step}' whose type this site has.";
            }

            if (entry.OutcomesFrom != StepCatalogEntry.CaseOutcomes)
            {
                return $"'{Label(found)}' has no cases - only a Switch (flow) does.";
            }

            var made = new List<PipelineGraphCase>();
            foreach (var @case in cases ?? new List<CaseArg>())
            {
                var label = @case?.Label?.Trim();
                if (string.IsNullOrEmpty(label))
                {
                    return "Not set: every case needs a label.";
                }

                if (made.Any(other => string.Equals(other.Label, label, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"Not set: the case '{label}' is there twice.";
                }

                if (Condition(@case!.Rules, @case.Match, out var condition) is { } problem)
                {
                    return $"Not set: the case '{label}' {problem}";
                }

                made.Add(new PipelineGraphCase { Label = label, Condition = condition });
            }

            // A connection that left by a case no longer there would leave by nothing.
            var kept = made.Select(@case => @case.Label).Concat(entry.Ports ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
            Graph.Connections.RemoveAll(connection => connection.SourceId == found.Id && !string.IsNullOrEmpty(connection.SourcePort) && !kept.Contains(connection.SourcePort));
            found.Cases = made;
            Changed = true;
            return $"'{Label(found)}' has the cases {string.Join(", ", made.Select(@case => @case.Label))}, then {string.Join(", ", entry.Ports ?? new List<string>())}.";
        }

        [Description("Saves one of a step's outputs (by its name, e.g. Result) to a variable - made when the pipeline has none by that name. Later steps read it as {{ variable }}.")]
        public string SaveOutput(
            [Description("The step's id.")] string step,
            [Description("The output's name, as DescribeStepType gives it.")] string output,
            [Description("The variable's name, e.g. order.")] string variable)
        {
            if (Find(step) is not { } found || !_types.TryGetValue(found.Type ?? string.Empty, out var entry))
            {
                return $"There is no step '{step}' whose type this site has.";
            }

            var named = (entry.Outputs ?? new List<StepOutputView>()).FirstOrDefault(candidate => string.Equals(candidate.Name, output?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                return $"'{Label(found)}' has no output '{output}'" + (entry.Outputs is { Count: > 0 } outputs ? $" - it has {string.Join(", ", outputs.Select(candidate => candidate.Name))}." : " - it gives none.");
            }

            if (VariableName(variable) is not { } name)
            {
                return $"'{variable}' cannot be a variable's name: letters, digits and _ only, starting with a letter.";
            }

            var target = Graph.Variables.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (target is null)
            {
                target = new PipelineGraphVariable { Id = "var-" + Guid.NewGuid().ToString("N")[..12], Name = name, TypeName = "Object" };
                Graph.Variables.Add(target);
            }

            found.OutputVariables ??= new Dictionary<string, string>();
            found.OutputVariables[named.Name] = target.Id;
            Changed = true;
            return $"'{Label(found)}' saves {named.Name} to {{{{ {name} }}}}.";
        }

        [Description("Adds a variable with a starting value, or changes the starting value of one. Types: Object (anything), String, Int32, Double, Boolean.")]
        public string SetVariable(
            [Description("The variable's name.")] string name,
            [Description("Its value before any step saves to it, as text; nothing for none.")] string? value = null,
            [Description("Object, String, Int32, Double or Boolean.")] string type = "Object")
        {
            if (VariableName(name) is not { } valid)
            {
                return $"'{name}' cannot be a variable's name: letters, digits and _ only, starting with a letter.";
            }

            var kinds = new[] { "Object", "String", "Int32", "Double", "Boolean" };
            var kind = kinds.FirstOrDefault(candidate => string.Equals(candidate, type?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Object";
            var variable = Graph.Variables.FirstOrDefault(candidate => string.Equals(candidate.Name, valid, StringComparison.Ordinal));
            if (variable is null)
            {
                variable = new PipelineGraphVariable { Id = "var-" + Guid.NewGuid().ToString("N")[..12], Name = valid, TypeName = kind };
                Graph.Variables.Add(variable);
            }

            variable.Value = string.IsNullOrEmpty(value) ? null : value;
            Changed = true;
            return $"{{{{ {valid} }}}} starts as " + (variable.Value is null ? "nothing." : $"'{Short(variable.Value, 80)}'.");
        }

        [Description("Renames a variable, and every expression and condition that reads it. A script or a record's rules holding the old name are said, not changed.")]
        public string RenameVariable(
            [Description("The variable's name now.")] string name,
            [Description("Its new name: letters, digits and _, starting with a letter.")] string newName)
        {
            var variable = Graph.Variables.FirstOrDefault(candidate => string.Equals(candidate.Name, name?.Trim(), StringComparison.Ordinal));
            if (variable is null)
            {
                return $"There is no variable '{name}'. ReadPipeline lists them.";
            }

            if (PipelineVariableRenames.Problem(newName, Graph.Variables, variable.Id) is { } problem)
            {
                return "Not renamed: " + problem;
            }

            var from = variable.Name;
            var outcome = PipelineVariableRenames.Apply(Graph, variable.Id, newName, _types.Values);
            Changed = true;
            return $"{{{{ {from} }}}} is now {{{{ {variable.Name} }}}}" + (outcome.Places switch { 0 => ".", 1 => ", in the one expression or condition reading it.", var places => $", in the {places} expressions and conditions reading it." })
                + (outcome.ToCheck.Count == 0 ? "" : $" Check {string.Join(", ", outcome.ToCheck.Select(step => "'" + step + "'"))}: a script or a record's rules there may read the old name.");
        }

        [Description("Connects two steps of the same flowchart: from a step - through one of its outcomes (port) when it has several - to another.")]
        public string Connect(
            [Description("The step the connection leaves.")] string from,
            [Description("The step it leads to.")] string to,
            [Description("The outcome it leaves by, when 'from' has several.")] string? port = null)
        {
            if (Find(from) is not { } source || Find(to) is not { } target)
            {
                return "Not connected: both steps must be in the pipeline. ReadPipeline lists them.";
            }

            if (source.Id == target.Id)
            {
                return "Not connected: a step cannot lead to itself.";
            }

            if (source.Parent != target.Parent || source.ParentPort != target.ParentPort)
            {
                return "Not connected: the two steps are in different flowcharts (one is inside a body the other is not in).";
            }

            var outcome = Outcome(source, port, out var error);
            if (error is not null)
            {
                return "Not connected: " + error;
            }

            if (Graph.Connections.Any(connection => connection.SourceId == source.Id && connection.TargetId == target.Id && (connection.SourcePort ?? "") == (outcome ?? "")))
            {
                return "They are connected already.";
            }

            Graph.Connections.Add(new PipelineGraphConnection { SourceId = source.Id, SourcePort = outcome, TargetId = target.Id });
            Changed = true;
            return $"Connected '{Label(source)}'{(outcome is null ? "" : $" ({outcome})")} to '{Label(target)}'.";
        }

        [Description("Removes the connection from one step to another (through 'port', when there are several).")]
        public string Disconnect(
            [Description("The step the connection leaves.")] string from,
            [Description("The step it leads to.")] string to,
            [Description("The outcome it leaves by, when there are several.")] string? port = null)
        {
            var source = Find(from);
            var target = Find(to);
            var removed = source is null || target is null
                ? 0
                : Graph.Connections.RemoveAll(connection => connection.SourceId == source.Id && connection.TargetId == target.Id
                    && (string.IsNullOrWhiteSpace(port) || string.Equals(connection.SourcePort, port.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (removed == 0)
            {
                return "No such connection.";
            }

            Changed = true;
            return removed == 1 ? "Removed the connection." : $"Removed {removed} connections.";
        }

        [Description("Removes a step, every step inside its bodies, and their connections.")]
        public string RemoveStep([Description("The step's id.")] string step)
        {
            if (Find(step) is not { } found)
            {
                return $"There is no step '{step}'.";
            }

            var gone = new HashSet<string>(StringComparer.Ordinal) { found.Id };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var child in Graph.Steps.Where(candidate => candidate.Parent is not null && gone.Contains(candidate.Parent) && !gone.Contains(candidate.Id)).ToList())
                {
                    gone.Add(child.Id);
                    grew = true;
                }
            }

            Graph.Steps.RemoveAll(candidate => gone.Contains(candidate.Id));
            Graph.Connections.RemoveAll(connection => gone.Contains(connection.SourceId ?? "") || gone.Contains(connection.TargetId ?? ""));
            Graph.Notes.RemoveAll(note => note.Parent is not null && gone.Contains(note.Parent));
            Changed = true;
            return gone.Count switch
            {
                1 => $"Removed '{Label(found)}'.",
                2 => $"Removed '{Label(found)}' and the step inside it.",
                _ => $"Removed '{Label(found)}' and the {gone.Count - 1} steps inside it.",
            };
        }

        [Description("Renames a step.")]
        public string RenameStep([Description("The step's id.")] string step, [Description("Its new name.")] string name)
        {
            if (Find(step) is not { } found)
            {
                return $"There is no step '{step}'.";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return "A step needs a name.";
            }

            found.Name = name.Trim();
            Changed = true;
            return $"{Name(found.Id)} is now called '{found.Name}'.";
        }

        [Description("Checks the pipeline as it stands: what the editor marks as a problem or a warning, and whether the save would take it. Call it before Finish and fix what it reports.")]
        public string Check()
        {
            var lines = PipelineGraphProblems.Find(Graph, _types.Values)
                .Select(problem => $"{(problem.Severity == PipelineGraphProblems.Error ? "Problem" : "Warning")} at '{problem.Step}' ({Name(problem.StepId)}): {problem.Message}")
                .ToList();
            if (_judge?.Invoke(Graph) is { } refused)
            {
                lines.Add("The save would refuse it: " + refused);
            }

            return lines.Count == 0 ? "No problems." : string.Join("\n", lines);
        }

        [Description("Ends the work, with a short summary for the author of what you changed and what they must still do (a secret or a connection to enter, a choice to make). Call it once, last.")]
        public string Finish([Description("The summary, in the author's language.")] string summary)
        {
            Summary = Short(summary, 1000) ?? string.Empty;
            if (FunctionInvokingChatClient.CurrentContext is { } context)
            {
                // Finished is the end of the turn: a closing answer would be one more metered call that changes nothing.
                context.Terminate = true;
            }

            return "Finished.";
        }

        /// <summary>A rule of a condition, as the model gives it.</summary>
        public sealed class RuleArg
        {
            [Description("What is tested, usually {{ variable.field }}.")]
            public string Left { get; set; } = string.Empty;

            [Description("equals, not-equals, contains, not-contains, starts-with, ends-with, greater, at-least, less, at-most, empty, not-empty, true or false.")]
            public string Operator { get; set; } = PipelineConditions.EqualsOperator;

            [Description("What it is compared with; nothing for an operator that takes one side.")]
            public string? Right { get; set; }
        }

        /// <summary>A Switch (flow)'s case, as the model gives it.</summary>
        public sealed class CaseArg
        {
            [Description("The case's label - the outcome its connection leaves by.")]
            public string Label { get; set; } = string.Empty;

            [Description("all or any.")]
            public string Match { get; set; } = PipelineConditions.All;

            [Description("The rules that choose it.")]
            public List<RuleArg> Rules { get; set; } = new();
        }

        // The short id a step is named by to the model, made the first time it is asked for.
        private string? Name(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (!_shortOf.TryGetValue(id, out var name))
            {
                name = "s" + (_shortOf.Count + 1);
                _shortOf[id] = name;
                _idOf[name] = id;
            }

            return name;
        }

        // A step by its short id - or its real one, which a model may have read elsewhere.
        private PipelineGraphStep? Find(string? id)
        {
            var key = id?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            var real = _idOf.TryGetValue(key, out var mapped) ? mapped : key;
            return Graph.Steps.FirstOrDefault(step => step?.Id == real);
        }

        private string Label(PipelineGraphStep step) =>
            !string.IsNullOrWhiteSpace(step.Name) ? step.Name.Trim() : _types.TryGetValue(step.Type ?? string.Empty, out var entry) ? entry.DisplayName : step.Type ?? Name(step.Id) ?? "?";

        private object Describe(PipelineGraphStep step)
        {
            _types.TryGetValue(step.Type ?? string.Empty, out var entry);
            var hidden = (entry?.Inputs ?? new List<StepInputView>()).Where(AuthorOnly).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
            var sets = (entry?.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.VariablePicker).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
            string Shown(string input, string? value) => hidden.Contains(input) ? "(set by the author)" : Short(value, 300) ?? string.Empty;
            var variables = Graph.Variables.Where(variable => variable?.Id is not null).GroupBy(variable => variable.Id).ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
            string VariableOf(string id) => variables.TryGetValue(id ?? string.Empty, out var name) ? name : "(a variable that is gone)";

            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (input, value) in step.Inputs ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrEmpty(value))
                {
                    inputs[input] = Shown(input, value);
                }
            }

            foreach (var (input, template) in step.InputExpressions ?? new Dictionary<string, string>())
            {
                inputs[input] = Shown(input, template);
            }

            foreach (var (input, id) in step.InputVariables ?? new Dictionary<string, string>())
            {
                // The variable it sets is named as SetInputs takes it; one it reads, as an expression reads it.
                inputs[input] = sets.Contains(input) ? VariableOf(id) : "{{ " + VariableOf(id) + " }}";
            }

            foreach (var (input, condition) in step.InputConditions ?? new Dictionary<string, PipelineCondition>())
            {
                inputs[input] = "condition: " + PipelineConditions.Describe(condition);
            }

            foreach (var (input, script) in step.InputScripts ?? new Dictionary<string, string>())
            {
                inputs[input] = "JavaScript: " + Shown(input, script);
            }

            return new
            {
                id = Name(step.Id),
                type = step.Type,
                name = step.Name,
                kind = entry?.Kind,
                inside = step.Parent is null ? null : new { step = Name(step.Parent), body = step.ParentPort },
                off = step.Disabled ? true : (bool?)null,
                inputs = inputs.Count == 0 ? null : inputs,
                cases = step.Cases is { Count: > 0 } cases ? cases.Select(@case => new { label = @case.Label, when = PipelineConditions.Describe(@case.Condition) }) : null,
                savesTo = step.OutputVariables is { Count: > 0 } outputs ? outputs.ToDictionary(pair => pair.Key, pair => VariableOf(pair.Value)) : null,
                comment = Short(step.Comment, 200),
            };
        }

        // An input set as the editor sets one: a value, or - holding {{ }} - an expression; nothing clears it. The input is one thing
        // at a time, so whatever it was (a variable, a condition, a script) it is no longer. The variable a step sets (Set Variable's
        // Variable) is named, and made when the pipeline has none by that name.
        private string? Assign(PipelineGraphStep step, StepCatalogEntry entry, string input, string? value)
        {
            var target = (entry.Inputs ?? new List<StepInputView>()).FirstOrDefault(candidate => string.Equals(candidate.Name, input?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return $"'{input}' is not an input of {entry.DisplayName}.";
            }

            if (AuthorOnly(target))
            {
                return $"'{target.Name}' is the author's to enter in the editor.";
            }

            if (entry.OutcomesFrom == StepCatalogEntry.CaseOutcomes && target.Name == StepCatalogEntry.CaseOutcomes)
            {
                return "A Switch's cases are set with SetCases.";
            }

            var text = value ?? string.Empty;
            if (text.Length == 0)
            {
                Clear(step, target.Name);
                return null;
            }

            if (target.UIHint == PipelineInputHints.VariablePicker)
            {
                // Named as ReadPipeline shows it, or as an expression reads it: {{ total }} is the variable total.
                var named = text.Trim();
                if (named.StartsWith("{{", StringComparison.Ordinal) && named.EndsWith("}}", StringComparison.Ordinal))
                {
                    named = named[2..^2].Trim();
                }

                if (VariableName(named) is not { } valid)
                {
                    return $"'{target.Name}' takes the name of the variable it sets (letters, digits and _, starting with a letter), not '{Short(text, 40)}'.";
                }

                var variable = Graph.Variables.FirstOrDefault(candidate => string.Equals(candidate.Name, valid, StringComparison.Ordinal));
                if (variable is null)
                {
                    variable = new PipelineGraphVariable { Id = "var-" + Guid.NewGuid().ToString("N")[..12], Name = valid, TypeName = "Object" };
                    Graph.Variables.Add(variable);
                }

                Clear(step, target.Name);
                step.InputVariables[target.Name] = variable.Id;
                return null;
            }

            if (text.Contains("{{", StringComparison.Ordinal))
            {
                Clear(step, target.Name);
                step.InputExpressions[target.Name] = text;
                return null;
            }

            // A Fork's branches, kept as the editor keeps them: a list of names.
            if (entry.OutcomesFrom == StepCatalogEntry.BranchesOutcomes && target.Name == StepCatalogEntry.BranchesOutcomes)
            {
                Clear(step, target.Name);
                step.Inputs[target.Name] = PipelineOutcomes.WriteBranches(PipelineOutcomes.Branches(text));
                return null;
            }

            if (!PipelineGraphProblems.Holds(target, text))
            {
                return target.Options is { Count: > 0 } options
                    ? $"'{target.Name}' takes one of {string.Join(", ", options)}, not '{Short(text, 40)}'."
                    : $"'{target.Name}' takes a {target.TypeName}, not '{Short(text, 40)}'.";
            }

            Clear(step, target.Name);
            step.Inputs[target.Name] = target.Options?.FirstOrDefault(option => string.Equals(option, text.Trim(), StringComparison.OrdinalIgnoreCase)) ?? text;
            return null;
        }

        private static void Clear(PipelineGraphStep step, string input)
        {
            step.Inputs ??= new Dictionary<string, string>();
            step.InputVariables ??= new Dictionary<string, string>();
            step.InputExpressions ??= new Dictionary<string, string>();
            step.InputScripts ??= new Dictionary<string, string>();
            step.InputConditions ??= new Dictionary<string, PipelineCondition>();
            step.Inputs.Remove(input);
            step.InputVariables.Remove(input);
            step.InputExpressions.Remove(input);
            step.InputScripts.Remove(input);
            step.InputConditions.Remove(input);
        }

        // A secret, or one of the site's connections: the author's to give, in the editor, never a model's.
        private static bool AuthorOnly(StepInputView input) => input.IsSensitive || input.UIHint == PipelineInputHints.ConnectionPicker;

        // The outcome a connection from the step leaves by: none for a step with one unnamed outcome, the one there is, or the one named.
        private string? Outcome(PipelineGraphStep step, string? port, out string? error)
        {
            error = null;
            _types.TryGetValue(step.Type ?? string.Empty, out var entry);
            var ports = PipelineOutcomes.Ports(entry, step.Inputs, step.Cases, step.FailedPath);
            var named = ports.Where(candidate => !string.IsNullOrEmpty(candidate)).Cast<string>().ToList();
            if (named.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(port))
            {
                if (named.Count == 1)
                {
                    return named[0];
                }

                error = $"'{Label(step)}' has the outcomes {string.Join(", ", named)} - name one as the port.";
                return null;
            }

            var chosen = named.FirstOrDefault(candidate => string.Equals(candidate, port.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                error = $"'{Label(step)}' has no outcome '{port}' - it has {string.Join(", ", named)}.";
            }

            return chosen;
        }

        // Where a new step goes: to the right of the one it follows (lower while that place is taken), or right of the flowchart's
        // steps, so it is seen; the author's Tidy up lays out the rest.
        private GraphPosition Place(string? parent, string? parentPort, PipelineGraphStep? previous)
        {
            var scope = Graph.Steps.Where(step => step.Parent == parent && step.ParentPort == parentPort).ToList();
            double x;
            double y;
            if (previous is not null)
            {
                x = previous.Position?.X + ColumnWidth ?? ColumnWidth;
                y = previous.Position?.Y ?? 0;
            }
            else if (scope.Count > 0)
            {
                x = scope.Max(step => step.Position?.X ?? 0) + ColumnWidth;
                y = scope.Min(step => step.Position?.Y ?? 0);
            }
            else
            {
                x = 80;
                y = 80;
            }

            while (scope.Any(step => Math.Abs((step.Position?.X ?? 0) - x) < ColumnWidth / 2 && Math.Abs((step.Position?.Y ?? 0) - y) < RowHeight / 2))
            {
                y += RowHeight;
            }

            return new GraphPosition { X = x, Y = y };
        }

        private static string? Condition(List<RuleArg>? rules, string? match, out PipelineCondition condition)
        {
            condition = new PipelineCondition
            {
                Match = string.Equals(match?.Trim(), PipelineConditions.Any, StringComparison.OrdinalIgnoreCase) ? PipelineConditions.Any : PipelineConditions.All,
                Rules = (rules ?? new List<RuleArg>()).Where(rule => rule is not null).Select(rule => new PipelineConditionRule
                {
                    Left = rule.Left?.Trim() ?? string.Empty,
                    Operator = rule.Operator?.Trim().ToLowerInvariant() ?? string.Empty,
                    Right = rule.Right?.Trim() ?? string.Empty,
                }).ToList(),
            };
            return PipelineConditions.Incomplete(condition) is { } incomplete ? "The condition " + incomplete : null;
        }

        private static string? VariableName(string? name)
        {
            var trimmed = name?.Trim();
            return !string.IsNullOrEmpty(trimmed) && char.IsLetter(trimmed[0]) && trimmed.All(c => char.IsLetterOrDigit(c) || c == '_') ? trimmed : null;
        }

        private static int Score(StepCatalogEntry entry, IReadOnlyList<string> words)
        {
            var score = 0;
            foreach (var word in words)
            {
                var hit = Has(entry.DisplayName, word) ? 4 : Has(entry.Type, word) ? 3 : Has(entry.Category, word) ? 2 : Has(entry.Description, word) ? 1 : 0;
                if (hit == 0)
                {
                    return 0;
                }

                score += hit;
            }

            return score;
        }

        private static bool Has(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;


        private static string? Short(string? text, int max) =>
            string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text.Trim() : text[..max].TrimEnd() + "…";
    }
}
