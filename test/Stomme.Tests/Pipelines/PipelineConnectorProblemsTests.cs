using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// An operation's required parameters, as n8n marks a node's required fields: a connector step whose chosen
    /// operation - or its connector's first - needs a parameter with no default that its Parameters lines do not give, or give
    /// no value, is warned of on the step, naming the line to add; the Query box gives the query; an operation chosen by an
    /// expression, parameters written as a script, a switched-off step and a connection whose connector is not known are left
    /// to the run.
    /// </summary>
    public sealed class PipelineConnectorProblemsTests
    {
        private const string Connection = "6f0d6c1e-0000-4000-8000-000000000001";

        private static readonly List<StepCatalogEntry> Catalog = new()
        {
            new()
            {
                Type = "Stomme.ExtractFromConnection", DisplayName = "Extract from connection", Kind = "Action",
                Inputs =
                {
                    new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", IsRequired = true, UIHint = PipelineInputHints.ConnectionPicker },
                    new() { Name = "Operation", DisplayName = "Operation", TypeName = "String", UIHint = PipelineInputHints.ConnectorOperation },
                    new() { Name = "Query", DisplayName = "Query", TypeName = "String" },
                    new() { Name = "Parameters", DisplayName = "Parameters", TypeName = "String" },
                },
            },
            new()
            {
                Type = "Stomme.SendThroughConnection", DisplayName = "Send through connection", Kind = "Action",
                Inputs =
                {
                    new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", IsRequired = true, UIHint = PipelineInputHints.ConnectionPicker },
                    new() { Name = "Parameters", DisplayName = "Parameters", TypeName = "String" },
                },
            },
        };

        private static ConnectorFieldView Field(string name, string display, bool required, string defaultValue = null) =>
            new(name, display, "Text", required, null, null, defaultValue, null, null, Array.Empty<ConnectorFieldOptionView>(), null);

        private static readonly ConnectorView Snowflake = new(
            "snowflake", "Snowflake", "Bearer", "Url", false, Array.Empty<ConnectorFieldView>(), Array.Empty<ConnectorAuthFieldView>(), Array.Empty<ConnectorCategoryView>(),
            new[]
            {
                new ConnectorOperationView("query", "Run a query", null, new[] { Field("query", "Query", true), Field("max", "At most", false, "1000") }),
                new ConnectorOperationView("update_rows", "Update rows", null, new[] { Field("table", "Table", true), Field("key", "Key column", true, "ID"), Field("rows", "Rows", true), Field("columns", "Columns", false) }),
            },
            null,
            null,
            "");

        private static PipelineGraphStep Step(string operation, string parameters, string name = "Pay")
        {
            var step = new PipelineGraphStep { Id = "s1", Type = "Stomme.ExtractFromConnection", Name = name };
            step.Inputs["Connection"] = Connection;
            if (operation is not null)
            {
                step.Inputs["Operation"] = operation;
            }

            if (parameters is not null)
            {
                step.Inputs["Parameters"] = parameters;
            }

            return step;
        }

        private static List<(string StepId, string Step, string Input, string Severity, string Code, string Message)> Found(params PipelineGraphStep[] steps)
        {
            var graph = new PipelineGraph();
            graph.Steps.AddRange(steps);
            return PipelineConnectorProblems.Find(graph, Catalog, id => id == Connection ? Snowflake : null)
                .Select(problem => (problem.StepId, problem.Step, problem.Input, problem.Severity, problem.Code, problem.Message)).ToList();
        }

        [Fact]
        public void A_required_parameter_with_no_default_missing_from_the_lines_is_a_warning_naming_the_line_to_add()
        {
            Assert.Equal(new[] { ("s1", "Pay", "Parameters", PipelineGraphProblems.Warning, PipelineConnectorProblems.MissingParameter, "Update rows needs Rows - add 'rows = ...' to Parameters.") },
                Found(Step("update_rows", "table = ORDERS\ncolumns = STATUS")));
            Assert.Equal(new[] { "Update rows needs Table - add 'table = ...' to Parameters.", "Update rows needs Rows - add 'rows = ...' to Parameters." },
                Found(Step("Update_Rows", "rows =  \nTABLE")).Select(problem => problem.Message)); // a line with no value gives nothing, an operation named in any case
            Assert.Empty(Found(Step("update_rows", "TABLE = ORDERS\nRows = {\"ID\": 8}"))); // a name in any case; the key has a default
            Assert.Empty(Found(Step("update_rows", "table = ORDERS\nrows = {{ order }}"))); // a value the run fills is a value
        }

        [Fact]
        public void The_query_box_gives_the_query_and_no_operation_means_the_connectors_first()
        {
            Assert.Equal(new[] { "Run a query needs Query - write it in Query." }, Found(Step(null, null, name: null)).Select(problem => problem.Message));
            Assert.Equal("Extract from connection", Found(Step(null, null, name: null)).Single().Step); // a step with no name by its type's
            var queried = Step(null, null);
            queried.Inputs["Query"] = "SELECT 1";
            Assert.Empty(Found(queried));
            var written = Step("query", null);
            written.InputExpressions["Query"] = "SELECT {{ x }}";
            Assert.Empty(Found(written));
            Assert.Empty(Found(Step("query", "query = SELECT 1")));
        }

        [Fact]
        public void What_only_the_run_can_say_is_left_to_it()
        {
            var chosen = Step(null, "table = ORDERS");
            chosen.InputExpressions["Operation"] = "{{ operation }}";
            Assert.Empty(Found(chosen)); // an operation chosen by an expression

            var scripted = Step("update_rows", null);
            scripted.InputScripts["Parameters"] = "return 'rows = x';";
            Assert.Empty(Found(scripted)); // parameters written as a script

            var expression = Step("update_rows", null);
            expression.InputExpressions["Parameters"] = "table = {{ table }}";
            Assert.Equal(new[] { "Update rows needs Rows - add 'rows = ...' to Parameters." }, Found(expression).Select(problem => problem.Message)); // an expression's lines still name their parameters

            var off = Step("update_rows", "");
            off.Disabled = true;
            Assert.Empty(Found(off));
            var unknown = Step("update_rows", "");
            unknown.Inputs["Connection"] = "6f0d6c1e-0000-4000-8000-000000000002";
            Assert.Empty(Found(unknown)); // a connection whose connector is not known
            Assert.Empty(Found(Step("delete_everything", ""))); // an operation the connector does not have: the step's own problem
            var send = new PipelineGraphStep { Id = "s2", Type = "Stomme.SendThroughConnection" };
            send.Inputs["Connection"] = Connection;
            Assert.Empty(Found(send)); // a step that chooses no operation
        }
    }
}
