#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Stomme.Modules.Mcp.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The registration and audit aggregates and the three reshaped value
    /// objects: what each refuses, and what each deliberately has no way to do.
    /// </summary>
    public class McpDomainTests
    {
        private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        private readonly ITestOutputHelper _output;

        public McpDomainTests(ITestOutputHelper output) => _output = output;

        private static McpServerRegistrationAggregate Registered(string name = "Docs server") =>
            McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 7, name, McpServerTransport.Create("https://mcp.example.com/v1"), null, Now);

        // ---- the transport ---------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("https://mcp.example.com/v1")]
        [InlineData("http://mcp.example.com:8080/")]
        [InlineData("  https://mcp.example.com/v1  ")]
        public void An_absolute_http_url_is_a_transport_and_round_trips_through_its_canonical_string(string url)
        {
            var transport = McpServerTransport.Create(url);

            Assert.Equal(url.Trim(), transport.Value);
            Assert.Equal(transport, McpServerTransport.Create(transport.Value));
            Assert.Equal(new Uri(url.Trim()), transport.Endpoint());
        }

        [Theory]
        [InlineData("")]
        [InlineData("mcp.example.com/v1")]                    // not absolute
        [InlineData("ftp://mcp.example.com/")]
        [InlineData("file:///etc/passwd")]
        [InlineData("stdio:npx some-server")]                 // a second, earlier transport: an admin-supplied COMMAND
        [InlineData("npx -y @some/mcp-server")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://user:secret@mcp.example.com/")]  // a credential in a URL ends up in logs and on the page
        [InlineData("https://token@mcp.example.com/")]
        public void Anything_else_is_REFUSED_and_there_is_nowhere_to_put_a_command(string value)
        {
            Assert.Throws<ArgumentException>(() => McpServerTransport.Create(value));
        }

        [Fact]
        public void A_url_longer_than_the_column_is_refused_rather_than_cut()
        {
            Assert.Throws<ArgumentException>(() => McpServerTransport.Create("https://mcp.example.com/" + new string('a', McpServerTransport.MaxLength)));
        }

        [Fact]
        public void The_transport_has_NO_public_member_that_could_carry_a_command()
        {
            // Structural: the day someone lifts the stdio half back in, this is what notices.
            var members = typeof(McpServerTransport).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(member => member.Name).ToList();
            _output.WriteLine(string.Join(", ", members));

            Assert.DoesNotContain(members, name =>
                name.Contains("Command", StringComparison.OrdinalIgnoreCase) || name.Contains("Stdio", StringComparison.OrdinalIgnoreCase) || name.Contains("Argument", StringComparison.OrdinalIgnoreCase));
        }

        // ---- the guardrail ---------------------------------------------------------------------------------------------

        [Fact]
        public void A_guardrail_round_trips_through_its_canonical_string()
        {
            Assert.Equal("rpm=-", McpGuardrail.Default.Value);
            Assert.Equal("rpm=60", McpGuardrail.Of(60).Value);
            Assert.Equal(McpGuardrail.Of(60), McpGuardrail.Create("rpm=60"));
            Assert.Null(McpGuardrail.Create("rpm=-").MaxRequestsPerMinute);
            Assert.True(McpGuardrail.Of(int.MaxValue).Value.Length <= McpGuardrail.MaxLength);
        }

        [Theory]
        [InlineData("")]
        [InlineData("60")]
        [InlineData("rpm=")]
        [InlineData("rpm=0")]
        [InlineData("rpm=-5")]
        [InlineData("rpm=+5")]
        [InlineData("rpm= 5")]
        [InlineData("rpm=5;sensitive=true")]     // a field this release does not have is not silently ignored
        [InlineData("RPM=5")]
        public void A_stored_guardrail_that_is_not_canonical_is_REFUSED(string stored)
        {
            Assert.Throws<ArgumentException>(() => McpGuardrail.Create(stored));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void A_cap_is_positive_when_set(int cap)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => McpGuardrail.Of(cap));
        }

        // ---- the discovered tools: said by SOMEBODY ELSE'S server ---------------------------------------------------------

        [Fact]
        public void A_tool_set_round_trips_and_is_empty_by_default()
        {
            var set = DiscoveredToolSet.Of(new[] { new DiscoveredTool(" search ", " Finds things. ", "{\"type\":\"object\"}"), new DiscoveredTool("fetch", null, " ") });

            Assert.Equal(new[] { "search", "fetch" }, set.Tools().Select(tool => tool.Name));
            Assert.Equal("Finds things.", set.Tools()[0].Description);
            Assert.Null(set.Tools()[1].ParametersJsonSchema);
            Assert.Equal(set, DiscoveredToolSet.Create(set.Value));

            Assert.Empty(DiscoveredToolSet.Empty.Tools());
            Assert.Same(DiscoveredToolSet.Empty, DiscoveredToolSet.Create(""));
            Assert.Same(DiscoveredToolSet.Empty, DiscoveredToolSet.Of(Array.Empty<DiscoveredTool>()));
        }

        [Fact]
        public void What_an_external_server_says_is_BOUNDED_before_it_is_kept()
        {
            var tooMany = Enumerable.Range(0, DiscoveredToolSet.MaximumTools + 1).Select(i => new DiscoveredTool("tool" + i, null, null));
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Of(tooMany));
            Assert.Equal(DiscoveredToolSet.MaximumTools, DiscoveredToolSet.Of(tooMany.Take(DiscoveredToolSet.MaximumTools)).Tools().Count);

            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Of(new[] { new DiscoveredTool(new string('n', DiscoveredToolSet.MaximumNameLength + 1), null, null) }));
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Of(new[] { new DiscoveredTool("  ", null, null) }));
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Of(new DiscoveredTool[] { null! }));

            var kept = DiscoveredToolSet.Of(new[]
            {
                new DiscoveredTool("wordy", new string('d', DiscoveredToolSet.MaximumDescriptionLength + 500), new string('s', DiscoveredToolSet.MaximumSchemaLength + 1)),
            }).Tools().Single();

            // A description is CUT; a schema is DROPPED - half a JSON schema is not a schema.
            Assert.Equal(DiscoveredToolSet.MaximumDescriptionLength, kept.Description!.Length);
            Assert.Null(kept.ParametersJsonSchema);
        }

        [Theory]
        [InlineData("search", "search")]
        [InlineData("search", "SEARCH")]
        [InlineData("search", " search ")]
        public void Two_tools_with_one_name_are_REFUSED(string first, string second)
        {
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Of(new[] { new DiscoveredTool(first, null, null), new DiscoveredTool(second, null, null) }));
        }

        [Fact]
        public void A_STORED_tool_set_is_held_to_the_same_bounds()
        {
            var oversized = "[" + string.Join(",", Enumerable.Range(0, DiscoveredToolSet.MaximumTools + 1).Select(i => $"{{\"Name\":\"t{i}\"}}")) + "]";
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Create(oversized));
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Create("[{\"Name\":\"a\"},{\"Name\":\"A\"}]"));
            Assert.Throws<ArgumentException>(() => DiscoveredToolSet.Create("not json"));
        }

        // ---- the registration ------------------------------------------------------------------------------------------

        [Fact]
        public void Registering_does_not_dial_so_a_new_registration_is_Unverified_with_no_tools()
        {
            var registration = Registered("  Docs server  ");

            Assert.Equal("Docs server", registration.Name);
            Assert.Equal("DOCS SERVER", registration.NameNormalized);
            Assert.Equal(7, registration.SiteId);
            Assert.True(registration.Enabled);
            Assert.False(registration.HasApiKey);
            Assert.Equal(McpServerTrustState.Unverified, registration.TrustState);
            Assert.Null(registration.LastRefreshedOnUtc);
            Assert.Empty(registration.ExposedTools());
            Assert.Equal(McpGuardrail.Default, registration.Guardrail);
            Assert.Equal(Now, registration.RegisteredOnUtc);
            Assert.Empty(registration.DomainEvents);     // audit is application code, never a handler
        }

        [Fact]
        public void A_refresh_verifies_it_and_a_DISABLED_server_exposes_nothing()
        {
            var registration = Registered();
            registration.ReplaceDiscoveredTools(DiscoveredToolSet.Of(new[] { new DiscoveredTool("search", null, null) }), Now.AddMinutes(5));

            Assert.Equal(McpServerTrustState.Verified, registration.TrustState);
            Assert.Equal(Now.AddMinutes(5), registration.LastRefreshedOnUtc);
            Assert.Single(registration.ExposedTools());

            registration.Disable();
            Assert.Empty(registration.ExposedTools());
            Assert.Single(registration.DiscoveredTools.Tools());      // kept - it says what re-enabling would expose

            registration.Enable();
            Assert.Single(registration.ExposedTools());
        }

        [Fact]
        public void A_server_that_offers_NOTHING_is_still_Verified_by_a_refresh()
        {
            // Found by reconciling an earlier suite's cases by name (ReplaceDiscoveredTools_AcceptsAnEmptySet_AndStillVerifies):
            // nothing here asserted it, and the distinction matters to an operator. A server that legitimately offers
            // no tools has still ANSWERED - if an empty answer left it Unverified, the admin page would read
            // "Unverified" forever and the only thing to do about it would be to press Refresh again.
            var registration = Registered();
            registration.ReplaceDiscoveredTools(DiscoveredToolSet.Empty, Now.AddMinutes(5));

            Assert.Equal(McpServerTrustState.Verified, registration.TrustState);
            Assert.Equal(Now.AddMinutes(5), registration.LastRefreshedOnUtc);
            Assert.Empty(registration.DiscoveredTools.Tools());
            Assert.Empty(registration.ExposedTools());

            // ...and a later refresh that DOES find something replaces wholesale, rather than merging into nothing.
            registration.ReplaceDiscoveredTools(DiscoveredToolSet.Of(new[] { new DiscoveredTool("search", null, null) }), Now.AddMinutes(6));
            Assert.Single(registration.DiscoveredTools.Tools());
        }

        [Fact]
        public void The_API_key_is_NAMED_by_the_row_and_never_held_by_it()
        {
            var registration = Registered();
            registration.MarkApiKey(true);

            Assert.True(registration.HasApiKey);
            Assert.Equal("mcp.server." + registration.Id.ToString("N") + ".apikey", registration.ApiKeySecretName());
            Assert.NotEqual(registration.ApiKeySecretName(), Registered().ApiKeySecretName());

            // Structural: no member of the row could hold a key, a token or a caller-supplied reference to one.
            var properties = typeof(McpServerRegistrationAggregate).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
            Assert.DoesNotContain(properties, name =>
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Password", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Ref", StringComparison.Ordinal)
                || (name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && name != nameof(McpServerRegistrationAggregate.HasApiKey)));
        }

        [Fact]
        public void The_endpoint_cannot_be_changed_because_Verified_is_a_statement_about_THAT_address()
        {
            var transport = typeof(McpServerRegistrationAggregate).GetProperty(nameof(McpServerRegistrationAggregate.Transport))!;
            Assert.False(transport.SetMethod?.IsPublic ?? false);

            var mutators = typeof(McpServerRegistrationAggregate).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(McpServerTransport)))
                .Select(method => method.Name).ToList();
            Assert.Empty(mutators);
        }

        [Fact]
        public void Every_computed_member_is_a_METHOD_so_a_document_store_does_not_write_it()
        {
            foreach (var type in new[] { typeof(McpServerRegistrationAggregate), typeof(McpAuditEntryAggregate) })
            {
                var computed = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(property => property.SetMethod is null).Select(p => p.Name).ToList();
                Assert.True(computed.Count == 0, $"{type.Name} has get-only properties a document resolver would serialise: {string.Join(", ", computed)}");
            }
        }

        [Fact]
        public void Nonsense_is_refused_at_registration()
        {
            var transport = McpServerTransport.Create("https://mcp.example.com/");
            Assert.Throws<ArgumentException>(() => McpServerRegistrationAggregate.Register(Guid.Empty, TenantId.Default, 1, "n", transport, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 0, "n", transport, null, Now));
            Assert.Throws<ArgumentException>(() => McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 1, "  ", transport, null, Now));
            Assert.Throws<ArgumentException>(() => McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 1, new string('n', 101), transport, null, Now));
            Assert.Throws<ArgumentNullException>(() => McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 1, "n", null!, null, Now));
            Assert.Throws<ArgumentException>(() => McpServerRegistrationAggregate.Register(Guid.NewGuid(), TenantId.Default, 1, "n", transport, null, DateTime.Now));
        }

        // ---- the audit entry -------------------------------------------------------------------------------------------

        [Fact]
        public void An_audit_entry_keeps_the_HASH_and_the_length_of_the_arguments_and_never_the_arguments()
        {
            const string arguments = "{\"query\":\"the salary of a named person\"}";
            var entry = McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ToolCall, "search_site", McpAuditOutcome.Denied, arguments, Now);

            Assert.Equal(64, entry.ArgumentsSha256.Length);
            Assert.Equal(entry.ArgumentsSha256, entry.ArgumentsSha256.ToLowerInvariant());
            Assert.Equal(arguments.Length, entry.ArgumentsLength);
            // The known SHA-256 of the empty string: "no arguments" is a value, not a null.
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ResourceRead, "stomme://site", McpAuditOutcome.Succeeded, null, Now).ArgumentsSha256);

            // The same arguments hash the same; different ones do not.
            Assert.Equal(entry.ArgumentsSha256, McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 43, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, arguments, Now).ArgumentsSha256);
            Assert.NotEqual(entry.ArgumentsSha256, McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, arguments + " ", Now).ArgumentsSha256);

            // Structural: nothing on the row is wide enough, or named, to carry them.
            var strings = typeof(McpAuditEntryAggregate).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { nameof(McpAuditEntryAggregate.ArgumentsSha256), nameof(McpAuditEntryAggregate.Subject) }, strings);
        }

        [Fact]
        public void An_over_long_subject_is_CUT_so_that_it_cannot_make_a_denied_call_leave_no_trace()
        {
            var entry = McpAuditEntryAggregate.Record(
                Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ToolCall, new string('x', 5000), McpAuditOutcome.Denied, null, Now);

            Assert.Equal(McpAuditEntryAggregate.MaximumSubjectLength, entry.Subject.Length);
            Assert.Equal(McpAuditOutcome.Denied, entry.Outcome);
        }

        [Fact]
        public void An_audit_entry_refuses_nonsense_and_has_no_mutator()
        {
            Assert.Throws<ArgumentException>(() => McpAuditEntryAggregate.Record(Guid.Empty, TenantId.Default, 7, 42, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 0, 42, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 0, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, (McpAuditVerb)9, "x", McpAuditOutcome.Succeeded, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ToolCall, "x", (McpAuditOutcome)9, null, Now));
            Assert.Throws<ArgumentException>(() => McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, 42, McpAuditVerb.ToolCall, "x", McpAuditOutcome.Succeeded, null, DateTime.Now));

            var mutators = typeof(McpAuditEntryAggregate).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).ToList();
            Assert.Empty(mutators);
        }

        [Fact]
        public void Every_value_object_here_is_a_STRING_value_object_which_is_the_only_kind_the_shared_context_maps()
        {
            foreach (var type in new[] { typeof(McpServerTransport), typeof(McpGuardrail), typeof(DiscoveredToolSet) })
            {
                Assert.True(typeof(IStringValueObject).IsAssignableFrom(type), $"{type.Name} is not an IStringValueObject");
                Assert.NotNull(type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) }));
            }
        }
    }
}
