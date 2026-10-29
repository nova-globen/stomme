#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.ServiceNow;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The ServiceNow connector cases (11), carried over by name over the port; a record got, created,
    /// updated and deleted in any table, an incident created and updated by its own fields, a table read no further than asked.</summary>
    public sealed class ServiceNowConnectorTests
    {
        private const string Instance = "https://acme.service-now.com";

        private const string PageOne = """
            {"result":[{"sys_id":"a1","name":"PC-1","install_status":"1","active":"true",
               "assigned_to":{"display_value":"Ada Lovelace","value":"u1","link":"https://acme.service-now.com/api/now/table/sys_user/u1"},
               "location":{"value":"loc-9","link":"https://acme.service-now.com/api/now/table/cmn_location/loc-9"}}]}
            """;

        private const string PageTwo = """{"result":[{"sys_id":"a2","name":"PC-2","install_status":"7","active":"false"}]}""";
        private const string EmptyPage = """{"result":[]}""";

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "api.user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, string endpoint = Instance, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "table") =>
            Contexts.Extract(new StubEgress(handler), endpoint, operation, parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = "cmdb_ci_computer" }, settings, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheTableApiPort()
        {
            var connector = new ServiceNowConnector();

            Assert.Equal(("servicenow", AuthenticationType.Basic), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "table", "record", "create_record", "update_record", "delete_record", "create_incident", "update_incident" }, connector.Operations.Select(operation => operation.Name));
            Assert.Equal(("https://acme.service-now.com", "Integration user's name (User Administration > Users - with the roles its steps need)"), (connector.EndpointExample, connector.CredentialLabels!["username"]));
        }

        [Fact]
        public async Task Extract_PagesByOffsetAndCollapsesReferences()
        {
            var handler = new RecordingHandler(request => Json(request.RequestUri!.Query.Contains("sysparm_offset=0") ? PageOne : request.RequestUri!.Query.Contains("sysparm_offset=1") ? PageTwo : EmptyPage));

            var records = await Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["api_limit"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal("PC-1", records[0].Fields["name"]);
            Assert.Equal("Ada Lovelace", records[0].Fields["assigned_to_value"]);
            Assert.Equal("loc-9", records[0].Fields["location_value"]);
            Assert.False(records[0].Fields.ContainsKey("assigned_to"));

            Assert.Equal(3, handler.Calls.Count);
            var firstUrl = handler.Calls[0].Request.RequestUri!.AbsoluteUri;
            Assert.StartsWith($"{Instance}/api/now/table/cmdb_ci_computer?", firstUrl);
            Assert.Contains("sysparm_exclude_reference_link=true", firstUrl);
            Assert.Contains("sysparm_display_value=false", firstUrl);
            Assert.Contains("sysparm_limit=1", firstUrl);
            Assert.Contains("sysparm_offset=1", handler.Calls[1].Request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", handler.Calls[0].Request.Headers.Authorization?.Scheme);
        }

        [Fact]
        public async Task Extract_EmptyFirstPage_IsACleanStopNotAnError()
        {
            var handler = new RecordingHandler(_ => Json(EmptyPage));

            var records = await Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler));

            Assert.Empty(records);
            Assert.Single(handler.Calls);
        }

        [Fact]
        public async Task Extract_FieldsAndQueryParameters_AreEscapedIntoTheUrl()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            await Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["table"] = "incident", ["fields"] = "sys_id,short_description", ["query"] = "active=true^priority<=2" }));

            var url = handler.Calls[0].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("/api/now/table/incident?", url);
            Assert.Contains("sysparm_fields=sys_id%2Cshort_description", url);
            Assert.Contains("sysparm_query=active%3Dtrue%5Epriority%3C%3D2", url);
        }

        [Fact]
        public async Task Extract_PastedApiUrlEndpoint_IsStrippedToTheInstance()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            await Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler, endpoint: $"{Instance}/api/now/table/incident"));

            Assert.StartsWith($"{Instance}/api/now/table/cmdb_ci_computer?", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_DisplayValueSetting_FlowsIntoTheQuery()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            await Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["display_value"] = "true" }));

            Assert.Contains("sysparm_display_value=true", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_ApiError_SurfacesTheMessage()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ServiceNowConnector(), Extract(new RecordingHandler(_ => Json("""{"error":{"message":"User Not Authenticated","detail":"..."},"status":"failure"}""")))));

            Assert.Contains("User Not Authenticated", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingTable_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ServiceNowConnector(), Extract(new RecordingHandler(_ => Json(PageTwo)), parameters: new(StringComparer.Ordinal))));

            Assert.Contains("'table' parameter", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingEndpoint_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ServiceNowConnector(), Extract(new RecordingHandler(_ => Json(PageTwo)), endpoint: string.Empty)));

            Assert.Contains("instance base URL", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesOneUser()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            var result = await new ServiceNowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Instance, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/api/now/table/sys_user?sysparm_limit=1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_Unauthorized_MentionsTheDeferredModes()
        {
            var result = await new ServiceNowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new RecordingHandler(_ => Json("""{"error":{}}""", HttpStatusCode.Unauthorized))), Instance, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("401", result.Message);
            Assert.Contains("OAuth and MFA", result.Message);
        }

        private const string IncidentId = "9d385017c611228701d22104cc95c371";

        private static Task<List<ConnectorRecord>> Run(RecordingHandler handler, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new ServiceNowConnector(), Extract(handler, settings: settings, parameters: parameters, operation: operation));

        private static (string Method, string Url, string Body) Sent(RecordingHandler handler, int index = -1)
        {
            var call = handler.Calls[index < 0 ? handler.Calls.Count + index : index];
            return (call.Request.Method.Method, call.Request.RequestUri!.AbsoluteUri, call.Body);
        }

        // A table read no further than asked, a step's own display values.
        [Fact]
        public async Task Table_IsReadNoFurtherThanAsked_WithAStepsOwnDisplayValues()
        {
            var handler = new RecordingHandler(request => Json(request.RequestUri!.Query.EndsWith("sysparm_offset=0", StringComparison.Ordinal) ? """{"result":[{"sys_id":"a1"},{"sys_id":"a2"}]}"""
                : request.RequestUri!.Query.EndsWith("sysparm_offset=2", StringComparison.Ordinal) ? """{"result":[{"sys_id":"a3"},{"sys_id":"a4"}]}""" : """{"result":[{"sys_id":"a5"}]}"""));

            Assert.Equal(new object?[] { "a1", "a2", "a3" }, (await Run(handler, "table", new() { ["table"] = "incident", ["max"] = "3", ["display_value"] = "All" }, new() { ["api_limit"] = "2" })).Select(record => record.Fields["sys_id"]));
            Assert.Equal(2, handler.Calls.Count);
            Assert.Contains("sysparm_display_value=all&sysparm_limit=2&sysparm_offset=0", Sent(handler, 0).Url);
            Assert.Contains("sysparm_limit=2&sysparm_offset=2", Sent(handler, 1).Url);
            handler.Calls.Clear();
            Assert.Single(await Run(handler, "table", new() { ["table"] = "incident", ["max"] = "1" }));
            Assert.Contains("sysparm_display_value=false&sysparm_limit=1&sysparm_offset=0", Assert.Single(handler.Calls).Request.RequestUri!.AbsoluteUri); // a page no larger than asked
            handler.Calls.Clear();
            Assert.Single(await Run(handler, "", new() { ["table"] = "incident", ["max"] = "1" })); // no operation: a table read
            Assert.StartsWith($"{Instance}/api/now/table/incident?", Assert.Single(handler.Calls).Request.RequestUri!.AbsoluteUri);
            handler.Calls.Clear();
            Assert.Equal(5, (await Run(handler, "table", new() { ["table"] = "incident", ["display_value"] = "false" }, new() { ["api_limit"] = "2", ["display_value"] = "true" })).Count); // no max: every row - the step's display values over the connection's
            Assert.Equal(3, handler.Calls.Count);
            Assert.Contains("sysparm_display_value=false", Sent(handler, 0).Url);
        }

        // A record got, created, updated and deleted in any table, as ServiceNow keeps it now.
        [Fact]
        public async Task A_record_is_got_created_updated_and_deleted_in_any_table()
        {
            const string Kept = """{"result":{"sys_id":"9d385017c611228701d22104cc95c371","number":"INC0010001","short_description":"Kassan är nere","caller_id":{"display_value":"Ada Lovelace","value":"62826bf03710200044e0bfc8bcbe5df1"},"active":"true"}}""";
            var handler = new RecordingHandler(request => request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(Kept, request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK));

            var got = Assert.Single(await Run(handler, "record", new() { ["table"] = "incident", ["id"] = IncidentId, ["fields"] = "number,short_description,caller_id", ["display_value"] = "all" })).Fields;
            Assert.Equal(("GET", $"{Instance}/api/now/table/incident/{IncidentId}?sysparm_exclude_reference_link=true&sysparm_display_value=all&sysparm_fields=number%2Cshort_description%2Ccaller_id", ""), Sent(handler));
            Assert.Equal(("INC0010001", "Kassan är nere", "Ada Lovelace"), ((string?)got["number"], (string?)got["short_description"], (string?)got["caller_id_value"]));
            Assert.Equal("Basic", handler.Calls[0].Request.Headers.Authorization?.Scheme);

            var created = Assert.Single(await Run(handler, "create_record", new() { ["table"] = "u_kafe_order", ["values"] = """{"u_name": "Fika & bröd", "u_count": 3, "u_paid": true}""" })).Fields;
            Assert.Equal(("POST", $"{Instance}/api/now/table/u_kafe_order?sysparm_exclude_reference_link=true&sysparm_display_value=false", """{"u_name":"Fika & bröd","u_count":3,"u_paid":true}"""), Sent(handler));
            Assert.Equal("INC0010001", created["number"]);
            Assert.Equal("application/json; charset=utf-8", handler.Calls[^1].Request.Content!.Headers.ContentType!.ToString());
            await Run(handler, "create_record", new() { ["table"] = "u_kafe_order" });
            Assert.Equal("{}", Sent(handler).Body); // an empty record - n8n's Nothing

            await Run(handler, "update_record", new() { ["table"] = "incident", ["id"] = IncidentId, ["values"] = """{"state": "2", "work_notes": "Ringde kassan\nOmstart på gång"}""" });
            Assert.Equal(("PATCH", $"{Instance}/api/now/table/incident/{IncidentId}?sysparm_exclude_reference_link=true&sysparm_display_value=false", """{"state":"2","work_notes":"Ringde kassan\nOmstart på gång"}"""), Sent(handler));

            var deleted = Assert.Single(await Run(handler, "Delete_Record", new() { ["table"] = "incident", ["id"] = IncidentId })).Fields;
            Assert.Equal(("DELETE", $"{Instance}/api/now/table/incident/{IncidentId}", ""), Sent(handler));
            Assert.Equal(((object?)IncidentId, (object?)true), (deleted["sys_id"], deleted["deleted"]));
        }

        // An incident created and updated by its own fields, any other column in its values.
        [Fact]
        public async Task An_incident_is_created_and_updated_by_its_own_fields()
        {
            var handler = new RecordingHandler(_ => Json("""{"result":{"sys_id":"9d385017c611228701d22104cc95c371","number":"INC0010002"}}"""));

            var created = Assert.Single(await Run(handler, "create_incident", new()
            {
                ["short_description"] = "Kassan är nere", ["caller_id"] = "Ada Lovelace", ["urgency"] = "2", ["impact"] = "3", ["category"] = "hardware", ["contact_type"] = "walk-in",
                ["assignment_group"] = "Service Desk", ["work_notes"] = "Ringde kassan\nOmstart på gång", ["values"] = """{"u_site": "Kafé Söder"}""",
            })).Fields;
            Assert.Equal(("POST", $"{Instance}/api/now/table/incident?sysparm_exclude_reference_link=true&sysparm_display_value=false",
                """{"u_site":"Kafé Söder","short_description":"Kassan är nere","caller_id":"Ada Lovelace","urgency":"2","impact":"3","category":"hardware","assignment_group":"Service Desk","contact_type":"walk-in","work_notes":"Ringde kassan\nOmstart på gång"}"""), Sent(handler));
            Assert.Equal("INC0010002", created["number"]);
            await Run(handler, "create_incident", new() { ["values"] = """{"short_description": "Skrivaren"}""" });
            Assert.Equal("""{"short_description":"Skrivaren"}""", Sent(handler).Body); // its short description in the values will do

            await Run(handler, "update_incident", new() { ["id"] = IncidentId, ["state"] = "6", ["close_code"] = "Solved (Permanently)", ["close_notes"] = "Omstartad", ["hold_reason"] = "1", ["subcategory"] = "cpu", ["description"] = "Hela kvällen", ["business_service"] = "Kassa", ["cmdb_ci"] = "PC-1", ["assigned_to"] = "Bo" });
            Assert.Equal(("PATCH", $"{Instance}/api/now/table/incident/{IncidentId}?sysparm_exclude_reference_link=true&sysparm_display_value=false",
                """{"description":"Hela kvällen","state":"6","subcategory":"cpu","assigned_to":"Bo","business_service":"Kassa","cmdb_ci":"PC-1","hold_reason":"1","close_code":"Solved (Permanently)","close_notes":"Omstartad"}"""), Sent(handler));
        }

        // What could go wrong refused before anything is sent; ServiceNow's message and detail on a failure.
        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_servicenows_words_on_a_failure()
        {
            var handler = new RecordingHandler(_ => Json("""{"result":{}}"""));
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(handler, operation, parameters))).Message;
            var id = new Dictionary<string, string> { ["id"] = IncidentId };
            Dictionary<string, string> With(params (string Name, string Value)[] more) => new[] { ("id", IncidentId) }.Concat(more).ToDictionary(pair => pair.Item1, pair => pair.Item2);

            Assert.Equal("ServiceNow has no operation 'incidents' - table, record, create_record, update_record, delete_record, create_incident or update_incident.", await Refused("incidents", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("table", new() { ["table"] = "incident", ["max"] = "0" }));
            Assert.Equal("'both' is not false, true or all (display_value).", await Refused("record", new() { ["table"] = "incident", ["id"] = IncidentId, ["display_value"] = "both" }));
            Assert.Equal("Give the table (table).", await Refused("record", id));
            Assert.Equal("'Incident' is not a table's name - incident, cmdb_ci_computer (table).", await Refused("record", new() { ["table"] = "Incident", ["id"] = IncidentId }));
            Assert.Equal("'incident/../sys_user' is not a table's name - incident, cmdb_ci_computer (table).", await Refused("delete_record", new() { ["table"] = "incident/../sys_user", ["id"] = IncidentId }));
            Assert.Equal("Give the record's sys_id (id).", await Refused("delete_record", new() { ["table"] = "incident" }));
            Assert.Equal("'INC0010001' is not a record's sys_id - 32 hexadecimal characters (id).", await Refused("record", new() { ["table"] = "incident", ["id"] = "INC0010001" }));
            Assert.Equal("'9D385017C611228701D22104CC95C371' is not a record's sys_id - 32 hexadecimal characters (id).", await Refused("update_incident", new() { ["id"] = "9D385017C611228701D22104CC95C371", ["state"] = "2" }));
            const string NotAnObject = "The values are not a JSON object of columns and values - {\"short_description\": \"Printer down\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["table"] = "incident", ["values"] = "short_description = Printer down" }));
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["table"] = "incident", ["values"] = "[{\"a\": 1}]" }));
            Assert.Equal("'Short Description' is not a column's name - lower-case letters, digits and _ (values).", await Refused("create_record", new() { ["table"] = "incident", ["values"] = "{\"Short Description\": \"x\"}" }));
            Assert.Equal("'caller_id.name' is not a column's name - lower-case letters, digits and _ (values).", await Refused("update_record", new() { ["table"] = "incident", ["id"] = IncidentId, ["values"] = "{\"caller_id.name\": \"Ada\"}" }));
            Assert.Equal("The value of 'caller_id' is not a text, a number, true or false (values).", await Refused("create_record", new() { ["table"] = "incident", ["values"] = "{\"caller_id\": {\"value\": \"u1\"}}" }));
            Assert.Equal("The value of 'caller_id' is not a text, a number, true or false (values).", await Refused("create_record", new() { ["table"] = "incident", ["values"] = "{\"caller_id\": null}" }));
            Assert.Equal("The value of 'cmdb_ci' is not a text, a number, true or false (values).", await Refused("create_record", new() { ["table"] = "incident", ["values"] = "{\"cmdb_ci\": [\"PC-1\"]}" }));
            Assert.Equal("The description is not a text of 4,000 characters at most, without control characters (description).", await Refused("create_record", new() { ["table"] = "incident", ["values"] = "{\"description\": \"Kafé\\u0001\"}" }));
            Assert.Equal("The description is not a text of 4,000 characters at most, without control characters (description).", await Refused("update_incident", With(("description", new string('a', 4001)))));
            Assert.Equal("Give what to change (values).", await Refused("update_record", new() { ["table"] = "incident", ["id"] = IncidentId }));
            Assert.Equal("Give what to change (values).", await Refused("update_record", new() { ["table"] = "incident", ["id"] = IncidentId, ["values"] = "{}" }));
            Assert.Equal("Give what to change (values).", await Refused("update_incident", id));
            Assert.Equal("Give the short description (short_description).", await Refused("create_incident", new() { ["urgency"] = "1" }));
            Assert.Equal("Give the short description (short_description).", await Refused("create_incident", new() { ["values"] = "{\"short_description\": \"  \"}" }));
            Assert.Equal("'4' is not 1 (high), 2 (medium) or 3 (low) (urgency).", await Refused("update_incident", With(("urgency", "4"))));
            Assert.Equal("'high' is not 1 (high), 2 (medium) or 3 (low) (impact).", await Refused("update_incident", With(("impact", "high"))));
            Assert.Equal("'In Progress' is not a choice's value - letters, digits, - and _ (state).", await Refused("update_incident", With(("state", "In Progress"))));
            Assert.Equal("'walk in?' is not a choice's value - letters, digits, - and _ (contact_type).", await Refused("update_incident", With(("contact_type", "walk in?"))));
            Assert.Equal("'urgency' is given twice - by itself and in the values.", await Refused("update_incident", With(("urgency", "1"), ("values", "{\"urgency\": \"2\"}"))));
            Assert.Empty(handler.Calls); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "create_record") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RecordingHandler(_ => Json(answer, status)), operation, new() { ["table"] = "incident", ["id"] = IncidentId, ["values"] = "{\"state\": \"2\"}" }))).Message;
            Assert.Equal("HTTP 403 Operation Failed: ACL Exception Insert Failed due to security constraints", await Failed(HttpStatusCode.Forbidden, """{"error":{"message":"Operation Failed","detail":"ACL Exception Insert Failed due to security constraints"},"status":"failure"}"""));
            Assert.Equal("HTTP 404 No Record found", await Failed(HttpStatusCode.NotFound, """{"error":{"message":"No Record found"},"status":"failure"}""", "delete_record"));
            Assert.Equal("HTTP 400 Invalid: " + new string('e', 300 - "Invalid: ".Length), await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid\",\"detail\":\"" + new string('e', 400) + "\"}}", "update_record")); // a long one cut
            Assert.Equal("ServiceNow answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Instance hibernating</body></html>", "record"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("ServiceNow answered HTTP 201 with nothing.", await Failed(HttpStatusCode.Created, ""));
            Assert.Equal("ServiceNow answered with no record.", await Failed(HttpStatusCode.OK, """{"result":[]}""", "update_record"));
        }
    }
}
