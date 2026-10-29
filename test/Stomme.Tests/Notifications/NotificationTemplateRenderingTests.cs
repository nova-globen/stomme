using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Tenancy.Domain;
using Fluid;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// <c>NotificationTemplateRenderingTests</c>: the hardened Fluid renderer. Encoding is
    /// chosen by the CHANNEL (HTML-encoded for e-mail, in-app and preview; raw then JSON-escaped once, by the webhook
    /// envelope); a parameter value is data, never re-evaluated; a runaway loop is a render ERROR (MaxSteps), not an
    /// exception; an undeclared placeholder is rejected at save; <c>{% include %}</c> reads no file.
    /// <para>
    /// Reconciliation of the earlier renderer tests (4 in / 4 accounted for):
    /// <c>Validate_DeclaredPlaceholders_PassesClean</c>, <c>Validate_UndeclaredPlaceholder_ReportsError</c> - carried;
    /// <c>Render_SubstitutesValues</c> - carried, async, through a render target;
    /// <c>Catalogue_BuiltIns_AreConsistentAndDistinct</c> - CHANGED: the catalogue is <c>generic.message</c> only,
    /// per site.
    /// </para>
    /// </summary>
    public sealed class NotificationTemplateRenderingTests
    {
        private const string Script = "<script>alert('x')</script>";

        private readonly NotificationTemplateRenderer _renderer = new();

        private static Dictionary<string, string> Parameters(params (string Name, string Value)[] values) =>
            values.ToDictionary(value => value.Name, value => value.Value);

        private async Task<Payload> RenderOkAsync(Content content, IReadOnlyDictionary<string, string> parameters, NotificationRenderTarget target)
        {
            var result = await _renderer.RenderAsync(content, parameters, target, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
            Assert.NotNull(result.Payload);
            return result.Payload;
        }

        // ---- renderer facts ------------------------------------------------------------------------------------------------------

        [Fact]
        public void Validate_DeclaredPlaceholders_PassesClean()
        {
            var contents = new List<LocalizedContent> { new(new Locale("en"), new Content("Hi {{ name }}", "Run {{ runId }} done.")) };
            Assert.Empty(_renderer.Validate(contents, new PlaceholderSchema(["name", "runId"])));
        }

        [Fact]
        public void Validate_UndeclaredPlaceholder_ReportsError()
        {
            var contents = new List<LocalizedContent> { new(new Locale("en"), new Content("Hi {{ name }}", "From {{ company }}.")) };
            Assert.Contains(_renderer.Validate(contents, new PlaceholderSchema(["name"])), error => error.Contains("company"));
        }

        [Fact]
        public async Task Render_SubstitutesValues()
        {
            var payload = await RenderOkAsync(
                new Content("Hi {{ name }}", "Run {{ runId }} of {{ name }} ok."),
                Parameters(("name", "Ada"), ("runId", "42")),
                NotificationRenderTarget.Email);

            Assert.Equal("Hi Ada", payload.Subject);
            Assert.Equal("Run 42 of Ada ok.", payload.Body);
        }

        [Fact]
        public void Catalogue_BuiltIns_AreConsistentAndDistinct()
        {
            var templates = NotificationTemplateCatalogue.All(TenantId.Default, NotificationTestData.SiteId);

            var generic = Assert.Single(templates);
            Assert.Equal(NotificationTemplateCatalogue.GenericMessageKey, generic.Key);
            Assert.Equal("generic.message", generic.Key);
            Assert.Equal(NotificationTestData.SiteId, generic.SiteId);
            Assert.Equal(["subject", "body"], generic.Placeholders.Placeholders);
            Assert.Equal(templates.Count, templates.Select(template => template.Key).Distinct().Count());
            foreach (var template in templates)
            {
                Assert.Empty(_renderer.Validate(template.Contents, template.Placeholders));
            }

            // Per site: two sites get distinct aggregates with the same key.
            Assert.NotEqual(generic.Id, Assert.Single(NotificationTemplateCatalogue.All(TenantId.Default, NotificationTestData.SiteId + 1)).Id);
        }

        // ---- encoding by channel --------------------------------------------------------------------------------

        [Theory]
        [InlineData(NotificationRenderTarget.Email)]
        [InlineData(NotificationRenderTarget.InApp)]
        [InlineData(NotificationRenderTarget.Preview)]
        public async Task A_script_parameter_renders_html_encoded_for_email_in_app_and_preview(NotificationRenderTarget target)
        {
            var payload = await RenderOkAsync(new Content("Re: {{ subject }}", "<p>{{ body }}</p>"),
                Parameters(("subject", Script), ("body", Script)), target);

            // The subject is text wherever it is shown (an e-mail header; a line the app escapes itself): never HTML-encoded,
            // or an apostrophe reads "&#x27;" in every inbox. The body is HTML and is encoded.
            Assert.Equal("Re: " + Script, payload.Subject);
            Assert.DoesNotContain("<script>", payload.Body);
            Assert.Contains("&lt;script&gt;", payload.Body);
            Assert.StartsWith("<p>", payload.Body); // the template's own markup is not encoded - only the parameter
        }

        [Fact]
        public async Task A_script_parameter_renders_raw_for_webhook_and_is_json_escaped_once_by_the_envelope()
        {
            var payload = await RenderOkAsync(new Content("Re: {{ subject }}", "{{ body }}"),
                Parameters(("subject", Script), ("body", Script + " \"quoted\"")), NotificationRenderTarget.Webhook);

            Assert.Equal("Re: " + Script, payload.Subject);
            Assert.Equal(Script + " \"quoted\"", payload.Body);

            var envelope = WebhookNotificationChannel.BuildEnvelope(NotificationTestData.Email(), payload);
            Assert.DoesNotContain("<script>", envelope);
            Assert.DoesNotContain("&lt;", envelope); // not HTML-encoded on the way
            using var json = JsonDocument.Parse(envelope);
            Assert.Equal(Script + " \"quoted\"", json.RootElement.GetProperty("body").GetString()); // escaped exactly once
        }

        [Theory]
        [InlineData(ChannelKind.Email, NotificationRenderTarget.Email)]
        [InlineData(ChannelKind.InApp, NotificationRenderTarget.InApp)]
        [InlineData(ChannelKind.Webhook, NotificationRenderTarget.Webhook)]
        public void The_render_target_is_chosen_by_the_channel(ChannelKind kind, NotificationRenderTarget expected) =>
            Assert.Equal(expected, NotificationTemplateRenderer.TargetFor(kind));

        // ---- a parameter is data ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(NotificationRenderTarget.Email)]
        [InlineData(NotificationRenderTarget.Webhook)]
        public async Task A_liquid_expression_inside_a_parameter_value_is_not_re_evaluated(NotificationRenderTarget target)
        {
            var payload = await RenderOkAsync(new Content("{{ subject }}", "{{ body }}"),
                Parameters(("subject", "{{ 7 }}"), ("body", "{% for i in (1..3) %}x{% endfor %}")), target);

            Assert.Equal("{{ 7 }}", payload.Subject);
            Assert.Equal("{% for i in (1..3) %}x{% endfor %}", payload.Body);
            Assert.DoesNotContain("xxx", payload.Body);
        }

        // ---- bounded ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_runaway_for_loop_hits_MaxSteps_and_is_a_render_error()
        {
            var result = await _renderer.RenderAsync(
                new Content("s", "{% for i in (1..100000000) %}{% for j in (1..100000000) %}x{% endfor %}{% endfor %}"),
                Parameters(), NotificationRenderTarget.Email, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Payload);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Fact]
        public async Task A_huge_literal_range_is_refused_BEFORE_it_is_built()
        {
            // Measured: Fluid builds a range's whole array before the loop takes a step, so MaxSteps cannot stop
            // (1..100000000) - it cost gigabytes per render (out of memory under a 4 GB heap limit). Refused from the tree.
            // The counter is the PROCESS's, so tests running beside this one add to it (51.7 MB seen in one gate run, under
            // the budget when run alone). They only ever add: the smallest of three renders is this render's own cost.
            var allocated = long.MaxValue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var result = await _renderer.RenderAsync(
                    new Content("s", "{% for i in (1..100000000) %}x{% endfor %}"), Parameters(), NotificationRenderTarget.Email, CancellationToken.None);
                allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);

                Assert.False(result.Succeeded);
                Assert.Contains("range of 100000000 items", result.Error, StringComparison.Ordinal);
            }

            Assert.True(allocated < 50_000_000, $"the refusal allocated {allocated:N0} bytes");
        }

        [Fact]
        public async Task A_range_inside_the_budget_still_renders_and_a_nested_one_is_found_too()
        {
            var fine = await _renderer.RenderAsync(new Content("s", "{% for i in (1..3) %}{{ i }}{% endfor %}"), Parameters(), NotificationRenderTarget.Email, CancellationToken.None);
            Assert.True(fine.Succeeded, fine.Error);
            Assert.Equal("123", fine.Payload!.Body);

            // Deep in the tree: inside an if, inside a loop, as the SUBJECT.
            var nested = await _renderer.RenderAsync(
                new Content("{% if true %}{% for a in (1..2) %}{% for b in (5..20000) %}.{% endfor %}{% endfor %}{% endif %}", "body"),
                Parameters(), NotificationRenderTarget.Email, CancellationToken.None);
            Assert.False(nested.Succeeded);
            Assert.Contains("range of 19996 items", nested.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_huge_literal_range_is_refused_at_SAVE_too()
        {
            var contents = new List<LocalizedContent> { new(new Locale("en"), new Content("s", "{% for i in (1..50000) %}x{% endfor %}")) };
            Assert.Contains(_renderer.Validate(contents, new PlaceholderSchema([])), error => error.Contains("range of 50000 items", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_cancelled_render_is_a_render_error()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var result = await _renderer.RenderAsync(new Content("s", "b"), Parameters(), NotificationRenderTarget.Email, cancelled.Token);
            Assert.False(result.Succeeded);

            using var budget = NotificationTemplateRenderer.CreateRenderCancellation();
            Assert.False(budget.IsCancellationRequested);
        }

        [Fact]
        public async Task An_invalid_template_is_a_render_error_not_an_exception()
        {
            var result = await _renderer.RenderAsync(new Content("s", "{% if %}"), Parameters(), NotificationRenderTarget.Email, CancellationToken.None);
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task A_template_rendering_an_empty_body_is_a_render_error()
        {
            var result = await _renderer.RenderAsync(new Content("s", "{{ body }}"), Parameters(("body", "   ")), NotificationRenderTarget.Email, CancellationToken.None);
            Assert.False(result.Succeeded);
        }

        // ---- validate at save ------------------------------------------------------------------------------------

        [Fact]
        public void An_undeclared_placeholder_is_rejected_at_save()
        {
            var template = NotificationTemplateAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "ops.alert",
                [ChannelKind.Email],
                [new LocalizedContent(new Locale("en"), new Content("{{ subject }}", "Hello {{- secretToken | upcase }}"))],
                new PlaceholderSchema(["subject"]));

            var errors = _renderer.Validate(template.Contents, template.Placeholders);

            // {{- (whitespace control) is still an output tag; a check that matched only plain {{ missed it.
            Assert.Contains(errors, error => error.Contains("secretToken"));
            Assert.Throws<ArgumentException>(() => _renderer.EnsureValidForSave(template));
        }

        [Fact]
        public void A_syntax_error_is_rejected_at_save_and_control_tags_stay_unvalidated()
        {
            var broken = new List<LocalizedContent> { new(new Locale("en"), new Content("s", "{% if %}")) };
            Assert.NotEmpty(_renderer.Validate(broken, new PlaceholderSchema([])));

            // {% %} tags are not placeholder-validated.
            var loop = new List<LocalizedContent> { new(new Locale("en"), new Content("s", "{% for x in items %}.{% endfor %}")) };
            Assert.Empty(_renderer.Validate(loop, new PlaceholderSchema([])));
        }

        // ---- includes ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_include_reads_no_file()
        {
            var result = await _renderer.RenderAsync(
                new Content("s", "before{% include 'appsettings.json' %}after"), Parameters(), NotificationRenderTarget.Email, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("beforeafter", result.Payload.Body);
        }

        [Fact]
        public async Task Measured_fluid_with_a_null_FileProvider_on_include()
        {
            // Characterisation of the third-party behaviour the renderer is built around (not of the renderer).
            var options = new TemplateOptions { FileProvider = null };
            Assert.True(new FluidParser().TryParse("a{% include 'x' %}b", out var template, out _));

            var exception = await Record.ExceptionAsync(async () => await template.RenderAsync(new TemplateContext(options)));

            Assert.IsType<NullReferenceException>(exception);
        }

        [Fact]
        public void The_renderer_options_are_dedicated_and_bounded()
        {
            var options = NotificationTemplateRenderer.Options;
            Assert.NotSame(TemplateOptions.Default, options);
            Assert.Equal(NotificationTemplateRenderer.MaxSteps, options.MaxSteps);
            Assert.Equal(NotificationTemplateRenderer.MaxRecursion, options.MaxRecursion);
            Assert.True(options.MaxSteps > 0 && options.MaxRecursion > 0);
            Assert.IsNotType<UnsafeMemberAccessStrategy>(options.MemberAccessStrategy);
        }
    }
}
