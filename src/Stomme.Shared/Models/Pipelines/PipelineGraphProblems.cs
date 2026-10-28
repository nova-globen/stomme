using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>One thing wrong with, or worth knowing about, a step of a pipeline's graph (BL-229 increment 4).</summary>
    public class PipelineGraphProblem
    {
        /// <summary>The step it is about.</summary>
        public string StepId { get; set; }

        /// <summary>What the author calls that step: its name, or its type's.</summary>
        public string Step { get; set; }

        /// <summary>The input it is about; null for the step as a whole.</summary>
        public string Input { get; set; }

        /// <summary>
        /// <see cref="PipelineGraphProblems.Error"/> - the pipeline is not published while one remains - or
        /// <see cref="PipelineGraphProblems.Warning"/>, which is shown and refuses nothing.
        /// </summary>
        public string Severity { get; set; }

        /// <summary>The kind, one of the <see cref="PipelineGraphProblems"/> codes.</summary>
        public string Code { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// What is wrong with a pipeline's graph before it runs (BL-229 increment 4) - ONE rule, run by the editor as the author
    /// changes the graph and by the server before it publishes. Errors are what would fail the run or that the save refuses
    /// (an input the step cannot do without left empty, a value its type cannot hold, a variable that is gone); warnings are
    /// what runs but does nothing the author can have meant (a step nothing leads to, a variable read that never holds a value,
    /// a body holding no step). A step whose type the catalog does not list is left to the save, which knows every type.
    /// </summary>
    public static class PipelineGraphProblems
    {
        public const string Error = "Error";

        public const string Warning = "Warning";

        public const string MissingInput = "MissingInput";

        public const string BadValue = "BadValue";

        public const string MissingVariable = "MissingVariable";

        /// <summary>An expression reads <c>{{ name }}</c> and no variable has that name (increment 5).</summary>
        public const string UnknownVariable = "UnknownVariable";

        public const string NeverRuns = "NeverRuns";

        public const string VariableNeverSet = "VariableNeverSet";

        public const string EmptyBody = "EmptyBody";

        /// <summary>A condition built from rules that cannot be tested (increment 10): no rule, a rule testing nothing.</summary>
        public const string BadCondition = "BadCondition";

        /// <summary>A Switch's case without a label, or a label twice; no case at all is a warning (increment 10).</summary>
        public const string BadCase = "BadCase";

        /// <summary>A step that takes its Failed path (n8n's error output) with nothing connected to it.</summary>
        public const string FailedPathLeadsNowhere = "FailedPathLeadsNowhere";

        /// <summary>A value (not an expression) holding <c>{{ name }}</c> of one of the pipeline's variables: sent as typed.</summary>
        public const string PlaceholderInValue = "PlaceholderInValue";

        private const string MakeHtmlStepType = "Stomme.MakeHtml";

        private const string MakeHtmlTemplateInput = "Html";

        /// <summary>A "Wait for a call" in a pipeline with no "Call address" to make the address it waits on (BL-229) - or a "Wait for a form" with neither one nor Next page.</summary>
        public const string NoCallAddress = "NoCallAddress";

        /// <summary>A "Wait for a form" with Next page in a pipeline with no form before it - no Form trigger, no other Wait for a form - so nobody is taken on to it (BL-229).</summary>
        public const string NoFormBefore = "NoFormBefore";

        /// <summary>A Make HTML template that puts a value in without the html filter - put in as it is, markup and all (BL-229).</summary>
        public const string UnencodedInHtml = "UnencodedInHtml";

        /// <summary>A Respond to webhook step no call waits for: no Webhook here answers from the pipeline (BL-229).</summary>
        public const string NoWebhookWaits = "NoWebhookWaits";

        /// <summary>A Webhook that answers from the pipeline with no Respond to webhook step to give the answer (BL-229).</summary>
        public const string NoWebhookAnswer = "NoWebhookAnswer";

        /// <summary>The engine's own start marker: a flowchart holding one starts there.</summary>
        public const string StartStepType = "Stomme.Start";

        /// <summary>A call to a sub-pipeline sending what the sub-pipeline would refuse (<see cref="PipelineCallContracts"/>, BL-229).</summary>
        public const string BadCall = "BadCall";

        /// <summary>
        /// A SQL statement (<see cref="PipelineInputHints.SqlQuery"/>) an expression writes a value into (BL-229): spliced into the
        /// SQL, a value from a run - a form's field, a webhook's body - can end the string and say something else. A warning: the
        /// author may mean it (a table's name cannot be bound), but a value belongs in a bound <c>@name</c>.
        /// </summary>
        public const string ValueInSql = "ValueInSql";

        /// <summary>
        /// A script - a Run script step's, or an input given as JavaScript - that does not parse as the step runs it, or (a Run script
        /// step's) reads a name it can see nowhere (BL-229, n8n's Code node linting). Found by the server as the author writes
        /// (<c>POST api/Pipelines/scripts/check</c>), so the editor adds it beside this rule's; a warning - the save does not parse
        /// scripts - though the step fails when it runs.
        /// </summary>
        public const string ScriptDoesNotParse = "ScriptDoesNotParse";

        /// <summary>
        /// The graph's problems, and each call's to a pipeline whose contract is known (<paramref name="calls"/>, by definition id)
        /// - a warning: the contract is the called pipeline's, which can change apart from this one. A call the pipeline called
        /// would refuse from this one (<paramref name="self"/>, its definition id; null before it is saved) is one of them.
        /// </summary>
        public static List<PipelineGraphProblem> Find(PipelineGraph graph, IEnumerable<StepCatalogEntry> catalog, IReadOnlyDictionary<string, PipelineCallContract> calls, string self = null)
        {
            var problems = Find(graph, catalog);
            if (calls is null || calls.Count == 0 || graph?.Steps is null)
            {
                return problems;
            }

            var entries = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => entry?.Type is not null).GroupBy(entry => entry.Type).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var step in graph.Steps.Where(step => step is not null && !step.Disabled && PipelineCallContracts.IsCall(step.Type)))
            {
                if (step.Inputs.TryGetValue(PipelineCallContracts.CalledInput, out var called) && called is not null && calls.TryGetValue(called, out var contract) && contract is not null)
                {
                    entries.TryGetValue(step.Type, out var entry);
                    if (PipelineCallContracts.Refusal(contract, self) is { } refusal)
                    {
                        problems.Add(Problem(step, entry, PipelineCallContracts.CalledInput, Warning, BadCall, refusal));
                    }

                    problems.AddRange(PipelineCallContracts.Check(step, contract).Select(message => Problem(step, entry, PipelineCallContracts.SentInput, Warning, BadCall, message)));
                }
            }

            return problems;
        }

        public static List<PipelineGraphProblem> Find(PipelineGraph graph, IEnumerable<StepCatalogEntry> catalog)
        {
            var problems = new List<PipelineGraphProblem>();
            if (graph?.Steps is null)
            {
                return problems;
            }

            var entries = new Dictionary<string, StepCatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in catalog ?? Enumerable.Empty<StepCatalogEntry>())
            {
                if (entry?.Type is not null && !entries.ContainsKey(entry.Type))
                {
                    entries[entry.Type] = entry;
                }
            }

            var variables = new Dictionary<string, PipelineGraphVariable>(StringComparer.Ordinal);
            foreach (var variable in graph.Variables ?? new List<PipelineGraphVariable>())
            {
                if (!string.IsNullOrWhiteSpace(variable?.Id) && !variables.ContainsKey(variable.Id.Trim()))
                {
                    variables[variable.Id.Trim()] = variable;
                }
            }

            var steps = graph.Steps.Where(step => !string.IsNullOrWhiteSpace(step?.Id)).ToList();
            // What a step saves to: its outputs' variables, the one its error is saved to when the pipeline goes on past it, and the
            // one it sets (Set Variable).
            var savedTo = new HashSet<string>(
                steps.SelectMany(step => (step.OutputVariables?.Values ?? Enumerable.Empty<string>())
                        .Append((step.FailedPath || step.ContinueOnError) ? step.FailureVariable : null)
                        .Concat(SetsVariables(step, entries)))
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
                StringComparer.Ordinal);

            foreach (var step in steps)
            {
                if (!entries.TryGetValue(step.Type ?? "", out var entry))
                {
                    continue;
                }

                foreach (var input in entry.Inputs ?? new List<StepInputView>())
                {
                    // A Switch's cases are checked as cases, below (increment 10).
                    if (entry.OutcomesFrom == StepCatalogEntry.CaseOutcomes && input.Name == StepCatalogEntry.CaseOutcomes)
                    {
                        continue;
                    }

                    // The variable a step sets (Set Variable): one the pipeline has - without it the step fails every run.
                    if (input.UIHint == PipelineInputHints.VariablePicker)
                    {
                        var setsId = step.InputVariables is not null && step.InputVariables.TryGetValue(input.Name, out var picked) ? picked : null;
                        if (string.IsNullOrWhiteSpace(setsId))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingInput, $"{Label(input)} needs a variable: the one this step sets."));
                        }
                        else if (!variables.ContainsKey(setsId.Trim()))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingVariable, $"{Label(input)} is a variable that no longer exists."));
                        }

                        continue;
                    }

                    // A JavaScript expression (increment 5b) reads what it likes - only the run can say; an empty one fills nothing.
                    if (step.InputScripts is not null && step.InputScripts.TryGetValue(input.Name, out var script) && script is not null)
                    {
                        if (string.IsNullOrWhiteSpace(script) && input.IsRequired)
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingInput, $"{Label(input)} needs a value."));
                        }

                        continue;
                    }

                    // A condition built from rules (increment 10): complete, and reading only variables the pipeline has.
                    if (step.InputConditions is not null && step.InputConditions.TryGetValue(input.Name, out var condition) && condition is not null)
                    {
                        problems.AddRange(ConditionProblems(step, entry, input.Name, Label(input), condition, variables, savedTo));
                        continue;
                    }

                    // An expression fills the input when the step runs (increment 5); each name it reads must be a variable of the
                    // pipeline - the engine finds a variable by its exact name.
                    if (step.InputExpressions is not null && step.InputExpressions.TryGetValue(input.Name, out var template) && template is not null)
                    {
                        if (string.IsNullOrWhiteSpace(template) && input.IsRequired)
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingInput, $"{Label(input)} needs a value."));
                        }

                        // A filter that is not one, or a number filter given no number, fails the step: say so before it runs.
                        foreach (var message in PipelineTemplateFilters.Problems(template))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, BadValue, $"{Label(input)}: {message}"));
                        }

                        if (input.UIHint == PipelineInputHints.SqlQuery && PipelineTemplate.Placeholders(template).FirstOrDefault() is { } spliced)
                        {
                            problems.Add(Problem(step, entry, input.Name, Warning, ValueInSql,
                                $"{Label(input)} writes {{{{ {spliced.Trim()} }}}} into the SQL itself, where a value from a run can change the statement - write @name there and give the value in Parameters as '@name = {{{{ {spliced.Trim()} }}}}': bound, never spliced."));
                        }

                        // A built-in ({{ $now }}, PipelineBuiltIns) is read from the run, never a variable.
                        foreach (var name in PipelineTemplate.Names(template).Where(name => !PipelineBuiltIns.IsBuiltIn(name)))
                        {
                            var named = variables.Values.FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal));
                            if (named is null)
                            {
                                problems.Add(Problem(step, entry, input.Name, Error, UnknownVariable, $"{Label(input)} reads {{{{ {name} }}}}, but no variable is named '{name}'."));
                            }
                            else if (string.IsNullOrEmpty(named.Value) && !savedTo.Contains(named.Id.Trim()))
                            {
                                problems.Add(Problem(step, entry, input.Name, Warning, VariableNeverSet, $"{Label(input)} reads '{name}', which has no starting value and no step saves to."));
                            }
                        }

                        continue;
                    }

                    if (step.InputVariables is not null && step.InputVariables.TryGetValue(input.Name, out var readsId) && !string.IsNullOrWhiteSpace(readsId))
                    {
                        if (!variables.TryGetValue(readsId.Trim(), out var reads))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingVariable, $"{Label(input)} reads a variable that no longer exists."));
                        }
                        else if (string.IsNullOrEmpty(reads.Value) && !savedTo.Contains(readsId.Trim()))
                        {
                            problems.Add(Problem(step, entry, input.Name, Warning, VariableNeverSet, $"{Label(input)} reads '{reads.Name}', which has no starting value and no step saves to."));
                        }

                        continue;
                    }

                    var text = step.Inputs is not null && step.Inputs.TryGetValue(input.Name, out var value) ? value : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        if (input.IsRequired)
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, MissingInput, $"{Label(input)} needs a value."));
                        }

                        continue;
                    }

                    // Rules tested on each record (Filter records): a condition the editor wrote, complete once it has a rule. The
                    // names it reads are the records' fields, which only the run knows.
                    if (input.UIHint == PipelineInputHints.RecordCondition)
                    {
                        if (!PipelineConditions.TryRead(text, out var rules))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, BadValue, $"{Label(input)} is not a condition the editor wrote - set it again."));
                        }
                        else if (rules.Rules.Count > 0 && PipelineConditions.Incomplete(rules) is { } incomplete)
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, BadCondition, $"{Label(input)}: the condition {incomplete}"));
                        }

                        continue;
                    }

                    // A form's fields (the Form trigger): each thing that keeps them from being a form - publishing refuses them.
                    if (input.UIHint == PipelineInputHints.FormFields)
                    {
                        var fields = PipelineForms.Parse(text, out var fieldsError);
                        foreach (var problem in fields is null ? new List<string> { fieldsError } : PipelineForms.Problems(fields))
                        {
                            problems.Add(Problem(step, entry, input.Name, Error, BadValue, $"{Label(input)}: {problem}"));
                        }

                        continue;
                    }

                    if (!Holds(input, text))
                    {
                        problems.Add(Problem(step, entry, input.Name, Error, BadValue, $"{Label(input)}: '{text.Trim()}' is not {Kind(input)}."));
                    }

                    // A value is sent as typed, so {{ who }} in one is never filled in - n8n makes such a field an expression; this
                    // says so, when what it names is one of the pipeline's variables (found driving: "Hello {{ who }}" came out as is).
                    if (PipelineTemplate.Names(text).FirstOrDefault(name => variables.Values.Any(variable => string.Equals(variable.Name, name, StringComparison.Ordinal))) is { } placeholder)
                    {
                        problems.Add(Problem(step, entry, input.Name, Warning, PlaceholderInValue, $"{Label(input)} is a value, so {{{{ {placeholder} }}}} is sent as typed, not filled in - make it an expression."));
                    }

                    // A Fork's branches are its outcomes (increment 10): two a person cannot tell apart are one too many.
                    if (entry.OutcomesFrom == StepCatalogEntry.BranchesOutcomes && input.Name == StepCatalogEntry.BranchesOutcomes
                        && PipelineOutcomes.Branches(text).GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
                    {
                        problems.Add(Problem(step, entry, input.Name, Error, BadValue, $"{Label(input)} names the branch '{twice.Key}' twice."));
                    }
                }

                // A Switch's cases (increment 10): each labelled once, each a condition that can be tested.
                if (entry.OutcomesFrom == StepCatalogEntry.CaseOutcomes)
                {
                    problems.AddRange(CaseProblems(step, entry, variables, savedTo));
                }

                foreach (var output in step.OutputVariables ?? new Dictionary<string, string>())
                {
                    if (!string.IsNullOrWhiteSpace(output.Value) && !variables.ContainsKey(output.Value.Trim()))
                    {
                        var label = entry.Outputs?.FirstOrDefault(candidate => candidate.Name == output.Key)?.DisplayName ?? output.Key;
                        problems.Add(Problem(step, entry, null, Error, MissingVariable, $"{label} is saved to a variable that no longer exists."));
                    }
                }

                // n8n's error output: the error saved to a variable the pipeline has, and a Failed path that leads somewhere.
                if (!string.IsNullOrWhiteSpace(step.FailureVariable) && !variables.ContainsKey(step.FailureVariable.Trim()))
                {
                    problems.Add(Problem(step, entry, null, Error, MissingVariable, "Its error is saved to a variable that no longer exists."));
                }

                if (step.FailedPath && !(graph.Connections ?? new List<PipelineGraphConnection>()).Any(connection => connection?.SourceId == step.Id && connection.SourcePort == PipelineOutcomes.Failed))
                {
                    problems.Add(Problem(step, entry, null, Warning, FailedPathLeadsNowhere, "Its Failed path leads nowhere: a failure ends the flow there, quietly."));
                }

                // BL-229: a Wait says how long or until when, read as the step will read it (PipelineWaits).
                if (step.Type == PipelineWaits.StepType)
                {
                    problems.AddRange(WaitProblems(step, entry));
                }

                // BL-229: an approval's time to decide is a duration, read as the step will read it.
                if (step.Type == PipelineApprovals.StepType && step.Inputs is not null
                    && step.Inputs.TryGetValue(PipelineApprovals.DecideWithinInput, out var within) && !string.IsNullOrWhiteSpace(within)
                    && !PipelineWaits.TryParseDuration(within, out _, out var withinError))
                {
                    problems.Add(Problem(step, entry, PipelineApprovals.DecideWithinInput, Error, BadValue, "Decide within: " + withinError));
                }

                // BL-229, Respond to webhook: an answer reaches a call only when the pipeline's Webhook answers from the pipeline, and a
                // Webhook that does keeps its caller waiting for an answer some step must give - each a warning (it runs, to no end).
                if (step.Type == PipelineWebhookAnswering.RespondStepType && !step.Disabled && !steps.Any(PipelineWebhookAnswering.Answers))
                {
                    problems.Add(Problem(step, entry, null, Warning, NoWebhookWaits, "No Webhook here answers from the pipeline, so no call waits for this answer - set the Webhook's Answer to FromThePipeline."));
                }

                if (PipelineWebhookAnswering.Answers(step) && !steps.Any(other => other.Type == PipelineWebhookAnswering.RespondStepType && !other.Disabled))
                {
                    problems.Add(Problem(step, entry, PipelineWebhookAnswering.AnswerInput, Warning, NoWebhookAnswer, "It answers from the pipeline, but no Respond to webhook step gives the answer: the caller waits, then gets 202."));
                }

                // BL-229: a wait for a call waits on the address a Call address made, for a time read as the step reads it.
                if (step.Type == PipelineCalls.WaitStepType)
                {
                    if (!steps.Any(other => other.Type == PipelineCalls.AddressStepType && !other.Disabled))
                    {
                        problems.Add(Problem(step, entry, null, Error, NoCallAddress, "It needs a 'Call address' step before it: that makes the address another system calls."));
                    }

                    if (step.Inputs is not null && step.Inputs.TryGetValue(PipelineCalls.TimeOutInput, out var timeOut) && !string.IsNullOrWhiteSpace(timeOut)
                        && !PipelineWaits.TryParseDuration(timeOut, out _, out var timeOutError))
                    {
                        problems.Add(Problem(step, entry, PipelineCalls.TimeOutInput, Error, BadValue, "Time out after: " + timeOutError));
                    }
                }

                // BL-229, n8n's HTML node: a template puts a value in as it is - a record's "<script>" becomes the page's - so a value put
                // in without the html filter is said; | raw says it is HTML the author made, put in as it is on purpose.
                if (step.Type == MakeHtmlStepType && step.InputExpressions is not null && step.InputExpressions.TryGetValue(MakeHtmlTemplateInput, out var html)
                    && PipelineTemplate.Placeholders(html).Select(each => each.Trim()).FirstOrDefault(each =>
                        !PipelineTemplateFilters.Parse(each).Filters.Any(filter => filter.Name is "html" or "raw")) is { } unencoded)
                {
                    problems.Add(Problem(step, entry, MakeHtmlTemplateInput, Warning, UnencodedInHtml, $"{{{{ {unencoded} }}}} is put in the HTML as it is - add | html to encode it, or | raw if it is HTML you made."));
                }

                // BL-229: a wait for a form is filled in at the run's form address, which a Call address makes - unless it is the next page
                // of the form before it, whose sender is taken on to it (n8n's multi-page forms), which needs a form before it.
                if (step.Type == PipelineForms.WaitStepType && step.Inputs is not null)
                {
                    if (!PipelineForms.IsNextPage(step))
                    {
                        if (!steps.Any(other => other.Type == PipelineCalls.AddressStepType && !other.Disabled))
                        {
                            problems.Add(Problem(step, entry, PipelineForms.NextPageInput, Error, NoCallAddress, "It needs a 'Call address' step before it - its Form address is where the form is filled in - or Next page ticked, to take the person who sent the form before it on to this one."));
                        }
                    }
                    else if (!steps.Any(other => other != step && !other.Disabled && (other.Type == PipelineForms.StepType || other.Type == PipelineForms.WaitStepType)))
                    {
                        problems.Add(Problem(step, entry, PipelineForms.NextPageInput, Warning, NoFormBefore, "It is the next page of a form, but no form comes before it - no Form submitted trigger, no other Wait for a form - so nobody is taken on to it."));
                    }
                }

                // Who is told, and how - read as the step reads them; a line the step cannot read leaves someone untold.
                if (step.Type == PipelineApprovals.StepType && step.Inputs is not null)
                {
                    if (step.Inputs.TryGetValue(PipelineApprovals.NotifyInput, out var notify) && !PipelineApprovals.TryParseRecipients(notify, out _, out var notifyError))
                    {
                        problems.Add(Problem(step, entry, PipelineApprovals.NotifyInput, Error, BadValue, "Notify: " + notifyError));
                    }

                    if (step.Inputs.TryGetValue(PipelineApprovals.NotifyByInput, out var notifyBy) && !PipelineApprovals.TryParseChannels(notifyBy, out _, out var notifyByError))
                    {
                        problems.Add(Problem(step, entry, PipelineApprovals.NotifyByInput, Error, BadValue, "Notify by: " + notifyByError));
                    }
                }

                if (entry.Bodies is { Count: > 0 } bodies && !steps.Any(child => child.Parent == step.Id))
                {
                    var names = string.Join(" and ", bodies);
                    problems.Add(Problem(step, entry, null, Warning, EmptyBody, bodies.Count == 1 ? $"Its {names} holds no step, so it does nothing." : $"Its {names} hold no step, so it does nothing."));
                }
            }

            // Each flowchart - the pipeline's, and each body - starts at one place; a step nothing leads to from there never runs.
            foreach (var scope in steps.GroupBy(step => string.IsNullOrWhiteSpace(step.Parent) ? "" : step.Parent + "\u001f" + step.ParentPort))
            {
                problems.AddRange(NeverReached(scope.ToList(), scope.Key.Length == 0, graph.Connections ?? new List<PipelineGraphConnection>(), entries));
            }

            return problems;
        }

        // Until wins when given; a time read from a variable, an expression or a script is the run's to judge.
        private static IEnumerable<PipelineGraphProblem> WaitProblems(PipelineGraphStep step, StepCatalogEntry entry)
        {
            bool Bound(string input) => (step.InputVariables?.ContainsKey(input) ?? false) || (step.InputExpressions?.ContainsKey(input) ?? false) || (step.InputScripts?.ContainsKey(input) ?? false);
            string Literal(string input) => step.Inputs is not null && step.Inputs.TryGetValue(input, out var value) ? value : null;

            var until = Literal(PipelineWaits.UntilInput);
            if (!string.IsNullOrWhiteSpace(until))
            {
                if (!PipelineWaits.TryParseMoment(until, out _, out var untilError))
                {
                    yield return Problem(step, entry, PipelineWaits.UntilInput, Error, BadValue, "Until: " + untilError);
                }

                yield break;
            }

            if (Bound(PipelineWaits.UntilInput))
            {
                yield break;
            }

            var forText = Literal(PipelineWaits.ForInput);
            if (!string.IsNullOrWhiteSpace(forText))
            {
                if (!PipelineWaits.TryParseDuration(forText, out _, out var forError))
                {
                    yield return Problem(step, entry, PipelineWaits.ForInput, Error, BadValue, "For: " + forError);
                }

                yield break;
            }

            if (!Bound(PipelineWaits.ForInput))
            {
                yield return Problem(step, entry, PipelineWaits.ForInput, Error, MissingInput, "It needs For (how long) or Until (a moment).");
            }
        }

        /// <summary>Whether a literal is a value of the input's type - the save's own test (the server's graph codec).</summary>
        public static bool Holds(StepInputView input, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            switch (input?.TypeName)
            {
                case "Boolean":
                    return bool.TryParse(text, out _);
                case "Int16":
                case "Int32":
                case "Int64":
                    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                case "Single":
                case "Double":
                case "Decimal":
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }

            // A dropdown: one of its members by name, any case - or a number, which the engine's enum parse takes too.
            return input?.Options is not { Count: > 0 } options
                || options.Any(option => string.Equals(option, text.Trim(), StringComparison.OrdinalIgnoreCase))
                || long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private static IEnumerable<PipelineGraphProblem> NeverReached(List<PipelineGraphStep> scope, bool top, List<PipelineGraphConnection> connections, Dictionary<string, StepCatalogEntry> entries)
        {
            var ids = new HashSet<string>(scope.Select(step => step.Id), StringComparer.Ordinal);
            var inner = connections.Where(connection => connection is not null && ids.Contains(connection.SourceId ?? "") && ids.Contains(connection.TargetId ?? "")).ToList();
            // BL-229: a "When evaluating" step starts an evaluation's runs and no other (the engine never picks it as the start),
            // so it is a start of its own beside the one below, never that one.
            var evaluationStarts = top
                ? scope.Where(step => !step.Disabled && step.Type == PipelineEvaluations.TriggerType && !inner.Any(connection => connection.TargetId == step.Id)).ToList()
                : new List<PipelineGraphStep>();
            var roots = scope.Where(step => step.Type != PipelineEvaluations.TriggerType && !inner.Any(connection => connection.TargetId == step.Id)).ToList();

            // The engine's order (the flowchart's start): its triggers - each starts the runs it receives, and a run started by
            // hand starts at the first - then a Start step, then the first step nothing leads to, then the first step.
            var starts = top
                ? roots.Where(step => !step.Disabled && entries.TryGetValue(step.Type ?? "", out var entry) && entry.Kind == "Trigger").ToList()
                : new List<PipelineGraphStep>();
            if (starts.Count == 0)
            {
                starts = scope.Where(step => step.Type == StartStepType).Take(1).ToList();
            }

            if (starts.Count == 0)
            {
                starts = roots.Take(1).ToList();
            }

            if (starts.Count == 0)
            {
                starts = scope.Where(step => step.Type != PipelineEvaluations.TriggerType).Take(1).ToList();
            }

            var start = starts.Count == 1 ? Name(starts[0], entries) : null;
            starts.AddRange(evaluationStarts);
            var reached = new HashSet<string>(starts.Select(step => step.Id), StringComparer.Ordinal);
            var queue = new Queue<string>(reached);
            while (queue.Count > 0)
            {
                var from = queue.Dequeue();
                foreach (var connection in inner.Where(connection => connection.SourceId == from))
                {
                    if (reached.Add(connection.TargetId))
                    {
                        queue.Enqueue(connection.TargetId);
                    }
                }
            }

            foreach (var step in scope.Where(step => !reached.Contains(step.Id)))
            {
                entries.TryGetValue(step.Type ?? "", out var entry);
                var where = top ? "the pipeline" : "this body";
                yield return Problem(step, entry, null, Warning, NeverRuns, start is null
                    ? $"Nothing leads to this step, so it never runs."
                    : $"Nothing leads here from '{start}', where {where} starts, so this step never runs.");
            }
        }

        private static IEnumerable<PipelineGraphProblem> ConditionProblems(PipelineGraphStep step, StepCatalogEntry entry, string input, string label, PipelineCondition condition,
            Dictionary<string, PipelineGraphVariable> variables, HashSet<string> savedTo)
        {
            if (PipelineConditions.Incomplete(condition) is { } incomplete)
            {
                yield return Problem(step, entry, input, Error, BadCondition, $"{label}: the condition {incomplete}");
            }

            foreach (var message in condition.Rules.SelectMany(rule => PipelineTemplateFilters.Problems(rule.Left).Concat(PipelineTemplateFilters.Problems(rule.Right))))
            {
                yield return Problem(step, entry, input, Error, BadCondition, $"{label}: {message}");
            }

            foreach (var name in PipelineConditions.Names(condition).Where(name => !PipelineBuiltIns.IsBuiltIn(name)))
            {
                var named = variables.Values.FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal));
                if (named is null)
                {
                    yield return Problem(step, entry, input, Error, UnknownVariable, $"{label} reads {{{{ {name} }}}}, but no variable is named '{name}'.");
                }
                else if (string.IsNullOrEmpty(named.Value) && !savedTo.Contains(named.Id.Trim()))
                {
                    yield return Problem(step, entry, input, Warning, VariableNeverSet, $"{label} reads '{name}', which has no starting value and no step saves to.");
                }
            }
        }

        private static IEnumerable<PipelineGraphProblem> CaseProblems(PipelineGraphStep step, StepCatalogEntry entry, Dictionary<string, PipelineGraphVariable> variables, HashSet<string> savedTo)
        {
            var cases = step.Cases ?? new List<PipelineGraphCase>();
            if (cases.Count == 0)
            {
                yield return Problem(step, entry, StepCatalogEntry.CaseOutcomes, Warning, BadCase, "It has no case, so it always goes out through Default.");
                yield break;
            }

            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < cases.Count; index++)
            {
                var label = cases[index]?.Label?.Trim();
                if (string.IsNullOrEmpty(label))
                {
                    yield return Problem(step, entry, StepCatalogEntry.CaseOutcomes, Error, BadCase, $"Case {index + 1} needs a label: it is the outcome its connection leaves by.");
                }
                else if (!labels.Add(label))
                {
                    yield return Problem(step, entry, StepCatalogEntry.CaseOutcomes, Error, BadCase, $"The case '{label}' is there twice.");
                }

                foreach (var problem in ConditionProblems(step, entry, StepCatalogEntry.CaseOutcomes, string.IsNullOrEmpty(label) ? $"Case {index + 1}" : $"Case '{label}'", cases[index]?.Condition ?? new PipelineCondition(), variables, savedTo))
                {
                    yield return problem;
                }
            }
        }

        // The variables a step sets itself - its variable-picker inputs (Set Variable's Variable) - when its type is listed.
        private static IEnumerable<string> SetsVariables(PipelineGraphStep step, Dictionary<string, StepCatalogEntry> entries) =>
            step.InputVariables is { Count: > 0 } && entries.TryGetValue(step.Type ?? "", out var entry)
                ? (entry.Inputs ?? new List<StepInputView>()).Where(input => input.UIHint == PipelineInputHints.VariablePicker)
                    .Select(input => step.InputVariables.TryGetValue(input.Name, out var id) ? id : null)
                : Enumerable.Empty<string>();

        private static PipelineGraphProblem Problem(PipelineGraphStep step, StepCatalogEntry entry, string input, string severity, string code, string message) => new()
        {
            StepId = step.Id,
            Step = string.IsNullOrWhiteSpace(step.Name) ? (entry?.DisplayName ?? step.Type) : step.Name.Trim(),
            Input = input,
            Severity = severity,
            Code = code,
            Message = message,
        };

        private static string Name(PipelineGraphStep step, Dictionary<string, StepCatalogEntry> entries) =>
            string.IsNullOrWhiteSpace(step.Name) ? (entries.TryGetValue(step.Type ?? "", out var entry) ? entry.DisplayName : step.Type) : step.Name.Trim();

        private static string Label(StepInputView input) => string.IsNullOrWhiteSpace(input.DisplayName) ? input.Name : input.DisplayName;

        private static string Kind(StepInputView input) => input.TypeName switch
        {
            "Boolean" => "true or false",
            "Int16" or "Int32" or "Int64" => "a whole number",
            "Single" or "Double" or "Decimal" => "a number",
            _ => "one of " + string.Join(", ", input.Options ?? new List<string>()),
        };
    }
}
