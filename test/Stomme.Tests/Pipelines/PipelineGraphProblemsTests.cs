using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 4 - problems before publish: the one rule the editor runs as the author changes the graph and the server
    /// runs before it publishes.
    /// </summary>
    public sealed class PipelineGraphProblemsTests
    {
        private static readonly List<StepCatalogEntry> Catalog = new()
        {
            new() { Type = "Stomme.WriteLine", DisplayName = "Write Line", Kind = "Action", Inputs = { new() { Name = "Text", DisplayName = "Text", TypeName = "String", IsRequired = true } } },
            new()
            {
                Type = "Stomme.Delay", DisplayName = "Delay", Kind = "Action",
                Inputs = { new() { Name = "Seconds", DisplayName = "Seconds", TypeName = "Int32" } },
            },
            new()
            {
                Type = "Stomme.Log", DisplayName = "Log", Kind = "Action",
                Inputs =
                {
                    new() { Name = "Message", DisplayName = "Message", TypeName = "String", IsRequired = true },
                    new() { Name = "Level", DisplayName = "Level", TypeName = "LogMessageLevel", Options = new List<string> { "Information", "Warning" } },
                    new() { Name = "Loud", DisplayName = "Loud", TypeName = "Boolean" },
                    new() { Name = "Ratio", DisplayName = "Ratio", TypeName = "Double" },
                },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            new() { Type = "Stomme.Schedule", DisplayName = "Schedule", Kind = "Trigger" },
            new() { Type = "Stomme.Webhook", DisplayName = "Webhook", Kind = "Trigger" },
            new() { Type = "Stomme.ForEach", DisplayName = "For Each", Kind = "Action", Bodies = { "Body" } },
            new() { Type = "Stomme.If", DisplayName = "If", Kind = "Action", Bodies = { "Then", "Else" } },
        };

        private static PipelineGraphStep Step(string id, string type, string name = null, string parent = null, string port = null) =>
            new() { Id = id, Type = type, Name = name, Parent = parent, ParentPort = port };

        private static PipelineGraphConnection Link(string from, string to) => new() { SourceId = from, TargetId = to };

        private static List<(string StepId, string Input, string Severity, string Code)> Found(PipelineGraph graph) =>
            PipelineGraphProblems.Find(graph, Catalog).Select(problem => (problem.StepId, problem.Input, problem.Severity, problem.Code)).ToList();

        [Fact]
        public void A_clean_pipeline_has_no_problems()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.WriteLine"), Step("b", "Stomme.Delay") },
                Connections = { Link("a", "b") },
            };
            graph.Steps[0].Inputs["Text"] = "hello";

            Assert.Empty(Found(graph));
        }

        // n8n's error output: a Failed path with nothing on it is a warning (a failure ends the flow there, quietly), and an error
        // saved to a variable the pipeline no longer has is an error, like any binding to one.
        [Fact]
        public void A_Failed_path_leading_nowhere_is_a_warning_and_a_lost_failure_variable_an_error()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.Delay", "Wait"), Step("b", "Stomme.Delay", "Then") },
                Connections = { Link("a", "b") },
            };
            graph.Steps[0].FailedPath = true;
            graph.Steps[0].FailureVariable = "v-gone";

            Assert.Equal(new[]
            {
                ("a", (string)null, PipelineGraphProblems.Error, PipelineGraphProblems.MissingVariable),
                ("a", null, PipelineGraphProblems.Warning, PipelineGraphProblems.FailedPathLeadsNowhere),
            }, Found(graph));

            graph.Variables.Add(new PipelineGraphVariable { Id = "v-gone", Name = "failure", TypeName = "Object" });
            graph.Connections.Add(new PipelineGraphConnection { SourceId = "a", SourcePort = PipelineOutcomes.Failed, TargetId = "b" });
            Assert.Empty(Found(graph));

            // A step reading the error: the variable is saved to by the failing step, so no "never holds a value" (found driving).
            graph.Steps.Add(Step("c", "Stomme.WriteLine", "Say"));
            graph.Steps[2].InputExpressions["Text"] = "handled: {{ failure.error }}";
            graph.Connections.Add(Link("b", "c"));
            Assert.Empty(Found(graph));
            graph.Steps[0].FailedPath = false; // not going on past a failure, it saves nothing
            Assert.Contains(("c", "Text", PipelineGraphProblems.Warning, PipelineGraphProblems.VariableNeverSet), Found(graph));
        }

        // BL-229: a Wait needs For or Until, each read as the step will read it; a time from a variable or an expression is the
        // run's to judge.
        [Fact]
        public void A_Wait_needs_a_time_it_can_read()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineWaits.StepType, DisplayName = "Wait", Kind = "Action", Inputs = { new() { Name = "For", DisplayName = "For", TypeName = "String" }, new() { Name = "Until", DisplayName = "Until", TypeName = "String" } } },
            };
            var wait = Step("w", PipelineWaits.StepType, "Wait");
            var graph = new PipelineGraph { Steps = { wait } };
            (string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Select(problem => (problem.Input, problem.Code, problem.Message)).ToArray();

            Assert.Equal(new[] { ("For", PipelineGraphProblems.MissingInput, "It needs For (how long) or Until (a moment).") }, Found());

            wait.Inputs["For"] = "two hours";
            Assert.Equal(PipelineGraphProblems.BadValue, Assert.Single(Found()).Item2);
            wait.Inputs["For"] = "2 hours";
            Assert.Empty(Found());

            wait.Inputs["Until"] = "Christmas";
            Assert.Contains("2026-12-24T09:00:00Z", Assert.Single(Found()).Item3);
            wait.Inputs["Until"] = "2026-12-24T09:00:00Z";
            Assert.Empty(Found());

            wait.Inputs.Clear();
            wait.InputExpressions["For"] = "{{ delay }}";
            graph.Variables.Add(new PipelineGraphVariable { Id = "v", Name = "delay", Value = "1 hour" });
            Assert.Empty(Found());
        }

        // BL-229: an approval's time to decide is read as the step reads it; left empty, it waits for a decision.
        [Fact]
        public void An_approvals_time_to_decide_is_a_duration_it_can_read()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineApprovals.StepType, DisplayName = "Ask for approval", Kind = "Action", Inputs = { new() { Name = PipelineApprovals.QuestionInput, DisplayName = "Question", TypeName = "String" }, new() { Name = PipelineApprovals.DecideWithinInput, DisplayName = "Decide within", TypeName = "String" } } },
            };
            var ask = Step("a", PipelineApprovals.StepType, "Approve");
            ask.Inputs[PipelineApprovals.QuestionInput] = "Pay?";
            var graph = new PipelineGraph { Steps = { ask } };
            (string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Select(problem => (problem.Input, problem.Code, problem.Message)).ToArray();

            Assert.Empty(Found());
            ask.Inputs[PipelineApprovals.DecideWithinInput] = "soon";
            var (input, code, message) = Assert.Single(Found());
            Assert.Equal((PipelineApprovals.DecideWithinInput, PipelineGraphProblems.BadValue), (input, code));
            Assert.StartsWith("Decide within: 'soon' is not a duration", message);
            ask.Inputs[PipelineApprovals.DecideWithinInput] = "2 days";
            Assert.Empty(Found());

            // Who is told, and how: a line the step cannot read is a bad value, named.
            ask.Inputs[PipelineApprovals.NotifyInput] = "role:Approvers\npager:555";
            Assert.Equal((PipelineApprovals.NotifyInput, PipelineGraphProblems.BadValue, "Notify: 'pager:555' is not role:<name>, user:<id> or email:<address>."), Assert.Single(Found()));
            ask.Inputs[PipelineApprovals.NotifyInput] = "role:Approvers\nuser:7\nemail:boss@example.test";
            ask.Inputs[PipelineApprovals.NotifyByInput] = "InApp, Sms";
            Assert.Equal((PipelineApprovals.NotifyByInput, PipelineGraphProblems.BadValue, "Notify by: 'Sms' is not a way to notify - write InApp, Email, or both."), Assert.Single(Found()));
            ask.Inputs[PipelineApprovals.NotifyByInput] = "inapp, Email";
            Assert.Empty(Found());
        }

        // BL-229: a wait for a call needs a Call address in the pipeline (a switched-off one does not count), and its time out
        // is read as the step reads it.
        [Fact]
        public void A_wait_for_a_call_needs_a_call_address_and_a_time_out_it_can_read()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineCalls.WaitStepType, DisplayName = "Wait for a call", Kind = "Action", Inputs = { new() { Name = PipelineCalls.TimeOutInput, DisplayName = "Time out after", TypeName = "String" } } },
                new() { Type = PipelineCalls.AddressStepType, DisplayName = "Call address", Kind = "Action" },
            };
            var wait = Step("w", PipelineCalls.WaitStepType, "Wait for it");
            var graph = new PipelineGraph { Steps = { wait } };
            (string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.StepId == "w").Select(problem => (problem.Input, problem.Code, problem.Message)).ToArray();

            Assert.Equal(new[] { ((string)null, PipelineGraphProblems.NoCallAddress, "It needs a 'Call address' step before it: that makes the address another system calls.") }, Found());
            var address = Step("a", PipelineCalls.AddressStepType, "Address");
            address.Disabled = true;
            graph.Steps.Insert(0, address);
            graph.Connections.Add(Link("a", "w"));
            Assert.Equal(PipelineGraphProblems.NoCallAddress, Assert.Single(Found()).Item2);
            address.Disabled = false;
            Assert.Empty(Found());

            wait.Inputs[PipelineCalls.TimeOutInput] = "soonish";
            Assert.Equal((PipelineCalls.TimeOutInput, PipelineGraphProblems.BadValue), (Assert.Single(Found()).Item1, Assert.Single(Found()).Item2));
            wait.Inputs[PipelineCalls.TimeOutInput] = "10 minutes";
            Assert.Empty(Found());
        }

        // BL-229: a wait for a form is filled in at the address a Call address makes - unless it is the next page of a form before it
        // (n8n's multi-page forms), which needs a form before it: a Form trigger, or another Wait for a form (a switched-off one does not count).
        [Fact]
        public void A_wait_for_a_form_needs_a_call_address_or_to_be_the_next_page_of_a_form_before_it()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineForms.WaitStepType, DisplayName = "Wait for a form", Kind = "Action", Inputs = { new() { Name = PipelineForms.NextPageInput, DisplayName = "Next page", TypeName = "Boolean" } } },
                new() { Type = PipelineForms.StepType, DisplayName = "Form submitted", Kind = "Trigger" },
                new() { Type = PipelineCalls.AddressStepType, DisplayName = "Call address", Kind = "Action" },
            };
            var page = Step("p", PipelineForms.WaitStepType, "Page two");
            var graph = new PipelineGraph { Steps = { page } };
            (string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.StepId == "p").Select(problem => (problem.Code, problem.Severity, problem.Input)).ToArray();

            Assert.Equal(new[] { (PipelineGraphProblems.NoCallAddress, PipelineGraphProblems.Error, PipelineForms.NextPageInput) }, Found());
            page.Inputs[PipelineForms.NextPageInput] = "True"; // as the inspector's tick writes it
            Assert.Equal(new[] { (PipelineGraphProblems.NoFormBefore, PipelineGraphProblems.Warning, PipelineForms.NextPageInput) }, Found());
            graph.Steps.Insert(0, Step("l", "Stomme.LogMessage", "Log")); // a step before it that is no form
            graph.Connections.Add(Link("l", "p"));
            Assert.Equal(PipelineGraphProblems.NoFormBefore, Assert.Single(Found()).Item1);
            graph.Steps.RemoveAt(0);
            graph.Connections.Clear();

            var trigger = Step("t", PipelineForms.StepType, "Sign up");
            trigger.Disabled = true;
            graph.Steps.Insert(0, trigger);
            graph.Connections.Add(Link("t", "p"));
            Assert.Equal(PipelineGraphProblems.NoFormBefore, Assert.Single(Found()).Item1);
            trigger.Disabled = false;
            Assert.Empty(Found());

            // Unticked again, a Call address is what it needs - and with one, nothing is missing.
            page.Inputs[PipelineForms.NextPageInput] = "false";
            Assert.Equal(PipelineGraphProblems.NoCallAddress, Assert.Single(Found()).Item1);
            graph.Steps.Insert(1, Step("a", PipelineCalls.AddressStepType, "Address"));
            Assert.Empty(Found());
            Assert.True(PipelineForms.IsNextPage(new PipelineGraphStep { Type = PipelineForms.WaitStepType, Inputs = { [PipelineForms.NextPageInput] = " true " } }));
            Assert.False(PipelineForms.IsNextPage(new PipelineGraphStep { Type = PipelineCalls.WaitStepType, Inputs = { [PipelineForms.NextPageInput] = "true" } }));
        }

        // BL-229, n8n's HTML node: a Make HTML template puts a value in as it is, so one put in without the html filter is said - a
        // warning; one encoded, one marked | raw (HTML the author made), or a template with no value in it, says nothing.
        [Fact]
        public void A_value_put_in_html_without_the_html_filter_is_said()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = "Stomme.MakeHtml", DisplayName = "Make HTML", Kind = "Action", Inputs = { new() { Name = "Html", DisplayName = "Html", TypeName = "String" } } },
            };
            var make = Step("h", "Stomme.MakeHtml", "Make it");
            make.InputExpressions["Html"] = "<p>{{ order.id | html }}</p><p>{{ order.customer | upper }}</p>";
            var graph = new PipelineGraph { Steps = { make } };
            (string, string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.StepId == "h" && problem.Code == PipelineGraphProblems.UnencodedInHtml).Select(problem => (problem.Input, problem.Code, problem.Severity, problem.Message)).ToArray();

            Assert.Equal(new[] { ("Html", PipelineGraphProblems.UnencodedInHtml, PipelineGraphProblems.Warning, "{{ order.customer | upper }} is put in the HTML as it is - add | html to encode it, or | raw if it is HTML you made.") }, Found());
            make.InputExpressions["Html"] = "<p>{{ order.id | html }}</p><p>{{ order.customer | upper | html }}</p>{{ table | raw }}";
            Assert.Empty(Found());
            make.InputExpressions.Clear();
            make.Inputs["Html"] = "<p>Plain</p>";
            Assert.Empty(Found());
        }

        // BL-229, Respond to webhook: an answer with no Webhook answering from the pipeline reaches nobody, and a Webhook that
        // answers from the pipeline with no Respond step keeps its caller waiting - each a warning; together, or switched off, none.
        [Fact]
        public void A_webhook_answer_needs_a_webhook_that_waits_for_it_and_the_other_way_round()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineWebhookAnswering.WebhookStepType, DisplayName = "Webhook", Kind = "Trigger", Inputs = { new() { Name = PipelineWebhookAnswering.AnswerInput, DisplayName = "Answer", TypeName = "String" } } },
                new() { Type = PipelineWebhookAnswering.RespondStepType, DisplayName = "Respond to webhook", Kind = "Action" },
            };
            var hook = Step("h", PipelineWebhookAnswering.WebhookStepType, "When called");
            var reply = Step("r", PipelineWebhookAnswering.RespondStepType, "Answer");
            var graph = new PipelineGraph { Steps = { hook, reply }, Connections = { Link("h", "r") } };
            string[] Codes() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.Severity == PipelineGraphProblems.Warning && problem.Code.StartsWith("NoWebhook", System.StringComparison.Ordinal)).Select(problem => problem.StepId + ":" + problem.Code).ToArray();

            Assert.Equal(new[] { "r:" + PipelineGraphProblems.NoWebhookWaits }, Codes());
            hook.Inputs[PipelineWebhookAnswering.AnswerInput] = PipelineWebhookAnswering.FromThePipeline;
            Assert.Empty(Codes());
            reply.Disabled = true;
            Assert.Equal(new[] { "h:" + PipelineGraphProblems.NoWebhookAnswer }, Codes());
        }

        // BL-229, Filter records: its rules are held as the input's value. Text that is not a condition the editor wrote, and an
        // incomplete one, are errors before publish; no rule at all is none (it keeps every record), and the names a complete
        // condition reads are the records' fields - never "no variable is named", and never the placeholder-in-a-value warning.
        [Fact]
        public void A_record_condition_held_as_a_value_is_one_the_editor_wrote_and_complete()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = "Stomme.FilterRecords", DisplayName = "Filter records", Kind = "Action", Inputs = { new() { Name = "Condition", DisplayName = "Condition", TypeName = "String", UIHint = PipelineInputHints.RecordCondition } } },
            };
            var filter = Step("f", "Stomme.FilterRecords", "Keep some");
            var graph = new PipelineGraph { Steps = { filter }, Variables = { new PipelineGraphVariable { Id = "v-status", Name = "status", Value = "x" } } };
            (string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.StepId == "f").Select(problem => (problem.Input, problem.Code)).ToArray();

            Assert.Empty(Found());
            filter.Inputs["Condition"] = "status = paid";
            Assert.Equal(new[] { ("Condition", PipelineGraphProblems.BadValue) }, Found());
            filter.Inputs["Condition"] = PipelineConditions.Write(new PipelineCondition { Rules = { new PipelineConditionRule { Operator = "equals", Right = "paid" } } });
            Assert.Equal(new[] { ("Condition", PipelineGraphProblems.BadCondition) }, Found());
            filter.Inputs["Condition"] = PipelineConditions.Write(new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ status }}", Operator = "equals", Right = "{{ limit }}" } } });
            Assert.Empty(Found());
        }

        // BL-229, the Form trigger: its fields are held as the input's value; text the editor did not write, and each thing that keeps
        // the fields from being a form, is an error before publish - and a form's labels are never read as placeholders.
        [Fact]
        public void A_forms_fields_are_ones_the_editor_wrote_and_make_a_form()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new() { Type = PipelineForms.StepType, DisplayName = "Form submitted", Kind = "Trigger", Inputs = { new() { Name = "Fields", DisplayName = "Fields", TypeName = "String", UIHint = PipelineInputHints.FormFields, IsRequired = true } } },
            };
            var form = Step("f", PipelineForms.StepType, "Contact");
            var graph = new PipelineGraph { Steps = { form } };
            (string, string, string)[] Found() => PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.StepId == "f").Select(problem => (problem.Input, problem.Code, problem.Message)).ToArray();

            Assert.Equal(new[] { ("Fields", PipelineGraphProblems.MissingInput, "Fields needs a value.") }, Found());
            form.Inputs["Fields"] = "name, email";
            Assert.Equal(PipelineGraphProblems.BadValue, Assert.Single(Found()).Item2);
            form.Inputs["Fields"] = PipelineForms.Serialize([new() { Label = "Pick {{ who }}", Type = PipelineFormFieldTypes.Dropdown }, new() { Label = "Pick {{ who }}" }]);
            Assert.Equal(new[]
            {
                ("Fields", PipelineGraphProblems.BadValue, "Fields: 'Pick {{ who }}' is a dropdown with no choices."),
                ("Fields", PipelineGraphProblems.BadValue, "Fields: Two fields are kept under 'pick_who': give one a name of its own."),
            }, Found());
            form.Inputs["Fields"] = PipelineForms.Serialize([new() { Label = "Pick {{ who }}" }]);
            Assert.Empty(Found());
        }

        // BL-229, found driving: "Hello {{ who }}" typed as a VALUE went out as typed. When a value names one of the pipeline's
        // variables in a placeholder, that is a warning (it publishes) saying to make it an expression; braces naming no variable
        // are left alone (a value may hold them on purpose), and the same text as an expression is no problem.
        [Fact]
        public void A_value_holding_a_placeholder_of_a_variable_is_a_warning_to_make_it_an_expression()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.WriteLine", "Say") },
                Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "world" } },
            };
            graph.Steps[0].Inputs["Text"] = "Hello {{ who.name }}";

            var problem = Assert.Single(PipelineGraphProblems.Find(graph, Catalog));
            Assert.Equal(("a", "Text", PipelineGraphProblems.Warning, PipelineGraphProblems.PlaceholderInValue), (problem.StepId, problem.Input, problem.Severity, problem.Code));
            Assert.Equal("Text is a value, so {{ who }} is sent as typed, not filled in - make it an expression.", problem.Message);

            graph.Steps[0].Inputs["Text"] = "Hello {{ nobody }}";
            Assert.Empty(Found(graph));

            graph.Steps[0].Inputs.Clear();
            graph.Steps[0].InputExpressions["Text"] = "Hello {{ who }}";
            Assert.Empty(Found(graph));
        }

        // BL-229: an expression writing a value into a SQL statement splices it into the SQL, where a value from a run can end the
        // string and say something else. A warning (a table's name cannot be bound), naming the bound way; the same statement as
        // typed, or with its value bound, is no problem.
        [Fact]
        public void An_expression_writing_a_value_into_a_sql_statement_is_a_warning_to_bind_it()
        {
            var catalog = new List<StepCatalogEntry>(Catalog)
            {
                new()
                {
                    Type = "Stomme.ExtractFromConnection", DisplayName = "Extract from connection", Kind = "Action",
                    Inputs =
                    {
                        new() { Name = "Query", DisplayName = "Query", TypeName = "String", UIHint = PipelineInputHints.SqlQuery },
                        new() { Name = "Parameters", DisplayName = "Parameters", TypeName = "String", UIHint = "multiline" },
                    },
                },
            };
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.ExtractFromConnection", "Orders") },
                Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "Ada" } },
            };
            graph.Steps[0].InputExpressions["Query"] = "SELECT * FROM people WHERE name = '{{ who | upper }}'";

            var problem = Assert.Single(PipelineGraphProblems.Find(graph, catalog));
            Assert.Equal(("a", "Query", PipelineGraphProblems.Warning, PipelineGraphProblems.ValueInSql), (problem.StepId, problem.Input, problem.Severity, problem.Code));
            Assert.Equal("Query writes {{ who | upper }} into the SQL itself, where a value from a run can change the statement - write @name there and give the value in Parameters as '@name = {{ who | upper }}': bound, never spliced.", problem.Message);

            graph.Steps[0].InputExpressions.Clear();
            graph.Steps[0].Inputs["Query"] = "SELECT * FROM people WHERE name = @who";
            graph.Steps[0].InputExpressions["Parameters"] = "@who = {{ who }}";
            Assert.Empty(PipelineGraphProblems.Find(graph, catalog));
        }

        [Fact]
        public void An_input_the_step_needs_left_empty_is_an_error_and_a_value_or_a_variable_fills_it()
        {
            var graph = new PipelineGraph { Steps = { Step("a", "Stomme.WriteLine", "Say") } };
            graph.Steps[0].Inputs["Text"] = "   ";

            var problem = Assert.Single(PipelineGraphProblems.Find(graph, Catalog));
            Assert.Equal(("a", "Say", "Text", PipelineGraphProblems.Error, PipelineGraphProblems.MissingInput), (problem.StepId, problem.Step, problem.Input, problem.Severity, problem.Code));
            Assert.Equal("Text needs a value.", problem.Message);

            graph.Steps[0].Inputs["Text"] = "hi";
            Assert.Empty(Found(graph));

            // Read from a variable that holds something, the input is filled.
            graph.Steps[0].Inputs.Clear();
            graph.Variables.Add(new PipelineGraphVariable { Id = "v", Name = "greeting", TypeName = "String", Value = "hello" });
            graph.Steps[0].InputVariables["Text"] = "v";
            Assert.Empty(Found(graph));
        }

        [Theory]
        [InlineData("Seconds", "five", false)]
        [InlineData("Seconds", "5", true)]
        [InlineData("Seconds", "5.5", false)]
        [InlineData("Ratio", "0.25", true)]
        [InlineData("Ratio", "0,25", false)]
        [InlineData("Loud", "yes", false)]
        [InlineData("Loud", "True", true)]
        [InlineData("Level", "warning", true)]
        [InlineData("Level", "Loud", false)]
        public void A_value_the_inputs_type_cannot_hold_is_an_error(string input, string text, bool holds)
        {
            var type = input == "Seconds" ? "Stomme.Delay" : "Stomme.Log";
            var graph = new PipelineGraph { Steps = { Step("a", type) } };
            graph.Steps[0].Inputs["Message"] = "m";
            graph.Steps[0].Inputs[input] = text;

            var found = Found(graph);
            if (holds)
            {
                Assert.Empty(found);
            }
            else
            {
                Assert.Equal(new[] { ("a", input, PipelineGraphProblems.Error, PipelineGraphProblems.BadValue) }, found);
            }
        }

        [Fact]
        public void A_variable_that_is_gone_is_an_error_and_one_never_set_is_a_warning()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.Log"), Step("b", "Stomme.WriteLine") },
                Connections = { Link("a", "b") },
                Variables = { new PipelineGraphVariable { Id = "empty", Name = "nothing", TypeName = "String" } },
            };
            graph.Steps[0].InputVariables["Message"] = "gone";
            graph.Steps[0].OutputVariables["Result"] = "also-gone";
            graph.Steps[1].InputVariables["Text"] = "empty";

            Assert.Equal(new[]
            {
                ("a", "Message", PipelineGraphProblems.Error, PipelineGraphProblems.MissingVariable),
                ("a", (string)null, PipelineGraphProblems.Error, PipelineGraphProblems.MissingVariable),
                ("b", "Text", PipelineGraphProblems.Warning, PipelineGraphProblems.VariableNeverSet),
            }, Found(graph));

            // A step that saves to it, or a starting value, sets it.
            graph.Steps[0].OutputVariables["Result"] = "empty";
            Assert.DoesNotContain(Found(graph), problem => problem.Code == PipelineGraphProblems.VariableNeverSet);
            graph.Steps[0].OutputVariables.Clear();
            graph.Variables[0].Value = "x";
            Assert.DoesNotContain(Found(graph), problem => problem.Code == PipelineGraphProblems.VariableNeverSet);
        }

        [Fact]
        public void A_step_nothing_leads_to_never_runs_and_is_a_warning()
        {
            // The pipeline starts at the first step nothing leads to (a); c is a second chain nothing reaches.
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.Delay", "First"), Step("b", "Stomme.Delay"), Step("c", "Stomme.Delay"), Step("d", "Stomme.Delay") },
                Connections = { Link("a", "b"), Link("c", "d") },
            };

            var problems = PipelineGraphProblems.Find(graph, Catalog);
            Assert.Equal(new[] { "c", "d" }, problems.Select(problem => problem.StepId));
            Assert.All(problems, problem => Assert.Equal((PipelineGraphProblems.Warning, PipelineGraphProblems.NeverRuns), (problem.Severity, problem.Code)));
            Assert.Equal("Nothing leads here from 'First', where the pipeline starts, so this step never runs.", problems[0].Message);
        }

        // BL-229 - n8n's Evaluations: a "When evaluating" step starts an evaluation's runs - a start of its own, what follows it
        // reached - and is never the start the other runs take, even placed first (the engine skips it too).
        [Fact]
        public void A_when_evaluating_step_is_a_start_of_its_own_and_never_the_pipelines_start()
        {
            var catalog = new List<StepCatalogEntry>(Catalog) { new() { Type = PipelineEvaluations.TriggerType, DisplayName = "When evaluating", Kind = "Action" } };
            var graph = new PipelineGraph
            {
                Steps = { Step("cases", PipelineEvaluations.TriggerType, "Test cases"), Step("prep", "Stomme.Delay"), Step("a", "Stomme.Delay", "First"), Step("b", "Stomme.Delay"), Step("c", "Stomme.Delay") },
                Connections = { Link("cases", "prep"), Link("prep", "b"), Link("a", "b") },
            };

            var problems = PipelineGraphProblems.Find(graph, catalog);
            Assert.Equal(new[] { "c" }, problems.Select(problem => problem.StepId));
            Assert.Equal("Nothing leads here from 'First', where the pipeline starts, so this step never runs.", problems[0].Message);

            // Switched off, it starts nothing: what only it led to never runs.
            graph.Steps[0].Disabled = true;
            Assert.Equal(new[] { "cases", "prep", "c" }, PipelineGraphProblems.Find(graph, catalog).Select(problem => problem.StepId));
        }

        [Fact]
        public void Each_trigger_starts_runs_of_its_own_and_a_Start_step_is_where_a_flowchart_starts()
        {
            var triggered = new PipelineGraph
            {
                Steps = { Step("plain", "Stomme.Delay"), Step("hook", "Stomme.Webhook"), Step("timer", "Stomme.Schedule"), Step("x", "Stomme.Delay"), Step("y", "Stomme.Delay") },
                Connections = { Link("hook", "x"), Link("timer", "y") },
            };
            // Both triggers' chains run; the plain step before them is not where a triggered pipeline starts.
            Assert.Equal(new[] { "plain" }, PipelineGraphProblems.Find(triggered, Catalog).Select(problem => problem.StepId));

            var marked = new PipelineGraph
            {
                Steps = { Step("first", "Stomme.Delay"), Step("start", PipelineGraphProblems.StartStepType), Step("next", "Stomme.Delay") },
                Connections = { Link("start", "next") },
            };
            Assert.Equal(new[] { "first" }, PipelineGraphProblems.Find(marked, Catalog).Select(problem => problem.StepId));
        }

        [Fact]
        public void A_body_is_a_flowchart_of_its_own_and_an_empty_one_is_a_warning()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    Step("loop", "Stomme.ForEach"),
                    Step("in1", "Stomme.Delay", parent: "loop", port: "Body"),
                    Step("in2", "Stomme.Delay", parent: "loop", port: "Body"),
                    Step("if", "Stomme.If"),
                },
                Connections = { Link("loop", "if") },
            };

            // in2 is in the loop's body with nothing leading to it from in1; the If holds nothing.
            Assert.Equal(new[]
            {
                ("if", (string)null, PipelineGraphProblems.Warning, PipelineGraphProblems.EmptyBody),
                ("in2", (string)null, PipelineGraphProblems.Warning, PipelineGraphProblems.NeverRuns),
            }, Found(graph));
            Assert.Contains("where this body starts", PipelineGraphProblems.Find(graph, Catalog).Single(problem => problem.StepId == "in2").Message);
            Assert.Equal("Its Then and Else hold no step, so it does nothing.", PipelineGraphProblems.Find(graph, Catalog).Single(problem => problem.StepId == "if").Message);

            graph.Connections.Add(Link("in1", "in2"));
            graph.Steps.Add(Step("then", "Stomme.Delay", parent: "if", port: "Then"));
            Assert.Empty(Found(graph));
        }

        // BL-229 increment 5: an expression fills its input; each name it reads must be a variable - by its exact name, as the
        // engine finds one - and one that never holds a value is a warning, as for a variable binding.
        [Fact]
        public void An_expression_fills_its_input_and_every_name_it_reads_must_be_a_variable()
        {
            var graph = new PipelineGraph
            {
                Steps = { Step("a", "Stomme.WriteLine") },
                Variables =
                {
                    new PipelineGraphVariable { Id = "v1", Name = "person", TypeName = "Object", Value = "{\"name\":\"Ada\"}" },
                    new PipelineGraphVariable { Id = "v2", Name = "later", TypeName = "String" },
                },
            };
            graph.Steps[0].InputExpressions["Text"] = "Hi {{ person.name }}";
            Assert.Empty(Found(graph));

            graph.Steps[0].InputExpressions["Text"] = "Hi {{ Person.name }} and {{ later }}";
            var problems = PipelineGraphProblems.Find(graph, Catalog);
            Assert.Equal(new[]
            {
                ("a", "Text", PipelineGraphProblems.Error, PipelineGraphProblems.UnknownVariable),
                ("a", "Text", PipelineGraphProblems.Warning, PipelineGraphProblems.VariableNeverSet),
            }, Found(graph));
            Assert.Equal("Text reads {{ Person }}, but no variable is named 'Person'.", problems[0].Message);

            // An empty expression leaves a required input empty.
            graph.Steps[0].InputExpressions["Text"] = " ";
            Assert.Equal(new[] { ("a", "Text", PipelineGraphProblems.Error, PipelineGraphProblems.MissingInput) }, Found(graph));
        }

        [Fact]
        public void A_step_the_catalog_does_not_list_is_left_to_the_save()
        {
            var graph = new PipelineGraph { Steps = { Step("a", "Stomme.Switch") } };
            graph.Steps[0].Inputs["Mode"] = "not a mode";

            Assert.Empty(Found(graph));
        }
    }
}
