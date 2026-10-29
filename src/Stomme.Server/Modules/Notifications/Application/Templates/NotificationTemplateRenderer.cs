#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;
using Fluid;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Stomme.Modules.Notifications.Application.Templates
{
    /// <summary>Where a rendered payload is going - which decides how parameter values are encoded.</summary>
    public enum NotificationRenderTarget
    {
        /// <summary>HTML-encoded: the inherited <c>NotificationJob</c> sends every body as <c>text/html</c>.</summary>
        Email = 0,

        /// <summary>HTML-encoded: the notification center renders the body as markup.</summary>
        InApp = 1,

        /// <summary>Raw; JSON-escaped exactly once, by the webhook envelope serializer.</summary>
        Webhook = 2,

        /// <summary>HTML-encoded: an operator previewing a template in the admin UI.</summary>
        Preview = 3,
    }

    /// <summary>A render outcome: the payload, or a render error (never an exception).</summary>
    public sealed record NotificationRenderResult(bool Succeeded, Payload? Payload, string? Error)
    {
        public static NotificationRenderResult Ok(Payload payload) => new(true, payload, null);

        public static NotificationRenderResult Fail(string error) => new(false, null, error);
    }

    /// <summary>
    /// The hardened Liquid renderer for notification templates, lifted from the predecessor platform
    /// <c>LiquidTemplateRenderer</c> and closed against what the survey measured there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encoding by channel.</b> the predecessor platform rendered UNENCODED and every e-mail path set <c>IsBodyHtml=true</c> - a
    /// parameter carrying markup was injected into the message. Here the encoder is chosen by the
    /// <see cref="NotificationRenderTarget"/>: <see cref="HtmlEncoder.Default"/> for the BODY of e-mail, in-app and preview
    /// (only the PARAMETER values are encoded; the template's own markup is the operator's), <see cref="NullEncoder"/> for
    /// webhook, whose envelope JSON-escapes the rendered text exactly once. The SUBJECT is text everywhere it is shown and
    /// is never HTML-encoded. A parameter value is data: Fluid never re-parses an output value, so <c>{{ 7 }}</c> inside
    /// one stays literal.
    /// </para>
    /// <para>
    /// <b>Bounded.</b> A DEDICATED <see cref="TemplateOptions"/> - never the process-wide
    /// <see cref="TemplateOptions.Default"/> another component could loosen - with <see cref="MaxSteps"/> and
    /// <see cref="MaxRecursion"/>; exceeding either is a render ERROR. Fluid has no timeout, and
    /// <c>HostedServiceBase.ExecuteJobAsync</c> passes no cancellation token (BL-78 F1), so a caller creates a per-render
    /// budget with <see cref="CreateRenderCancellation"/> and the renderer checks it before and after rendering.
    /// Member access is a fresh <see cref="DefaultMemberAccessStrategy"/> with NOTHING registered: parameters are a
    /// string-to-string map, so no CLR member is ever reachable from a template.
    /// </para>
    /// <para>
    /// <b>Includes read no file (F15, MEASURED).</b> With <c>FileProvider = null</c> Fluid 2.31.0 throws
    /// <see cref="NullReferenceException"/> on <c>{% include %}</c> (pinned by
    /// <c>Measured_fluid_with_a_null_FileProvider_on_include</c>) - a crash, not inertness. The options therefore carry
    /// <see cref="EmptyFileProvider"/>, which answers every path with an existing EMPTY file: an include renders
    /// nothing and touches no disk.
    /// </para>
    /// <para>
    /// <b>Validate at save.</b> <see cref="Validate"/> parses (<c>TryParse</c>) and checks every <c>{{ root }}</c> output
    /// root (whitespace-control <c>{{-</c> included, which the predecessor platform's regex missed) against the template's
    /// <see cref="PlaceholderSchema"/>; an undeclared root is rejected. <c>{% %}</c> tags stay unvalidated, as the predecessor platform
    /// documents. The save command (a later BL-78 step) calls <see cref="EnsureValidForSave"/>.
    /// </para>
    /// </remarks>
    public sealed partial class NotificationTemplateRenderer
    {
        /// <summary>The statement budget per render; a runaway loop exceeds it and fails as a render error.</summary>
        public const int MaxSteps = 10_000;

        /// <summary>The include/call nesting budget per render.</summary>
        public const int MaxRecursion = 10;

        /// <summary>The fixed wall-clock budget a caller gives one render (see <see cref="CreateRenderCancellation"/>).</summary>
        public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(5);

        private static readonly FluidParser Parser = new();

        /// <summary>The dedicated, bounded options every render uses. Never mutated after construction.</summary>
        public static TemplateOptions Options { get; } = CreateOptions();

        private static TemplateOptions CreateOptions()
        {
            var options = new TemplateOptions
            {
                MaxSteps = MaxSteps,
                MaxRecursion = MaxRecursion,
                FileProvider = new EmptyFileProvider(),
                MemberAccessStrategy = new DefaultMemberAccessStrategy(),
                CultureInfo = CultureInfo.InvariantCulture,
                TimeZone = TimeZoneInfo.Utc,
            };
            return options;
        }

        /// <summary>A per-render cancellation budget of <see cref="RenderTimeout"/>, optionally linked to an outer token.</summary>
        public static CancellationTokenSource CreateRenderCancellation(CancellationToken linkedToken = default)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            source.CancelAfter(RenderTimeout);
            return source;
        }

        /// <summary>The render target a channel kind delivers through.</summary>
        public static NotificationRenderTarget TargetFor(ChannelKind kind) => kind switch
        {
            ChannelKind.Email => NotificationRenderTarget.Email,
            ChannelKind.InApp => NotificationRenderTarget.InApp,
            ChannelKind.Webhook => NotificationRenderTarget.Webhook,
            // No SMS transport ships; text messages are not HTML, so raw is the correct target when one does.
            ChannelKind.Sms => NotificationRenderTarget.Webhook,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown channel kind."),
        };

        private static TextEncoder EncoderFor(NotificationRenderTarget target) => target switch
        {
            NotificationRenderTarget.Webhook => NullEncoder.Default,
            NotificationRenderTarget.Email or NotificationRenderTarget.InApp or NotificationRenderTarget.Preview => HtmlEncoder.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown render target."),
        };

        /// <summary>Validates localized contents against the schema and Liquid syntax; one message per problem, empty when valid.</summary>
        public IReadOnlyList<string> Validate(IReadOnlyList<LocalizedContent> contents, PlaceholderSchema schema)
        {
            ArgumentNullException.ThrowIfNull(contents);
            ArgumentNullException.ThrowIfNull(schema);

            var errors = new List<string>();
            foreach (var localized in contents)
            {
                var locale = localized.Locale.Code;
                ValidateOne(localized.Content.Subject, $"{locale} subject", schema, errors);
                ValidateOne(localized.Content.Body, $"{locale} body", schema, errors);
            }

            return errors;
        }

        /// <summary>Throws <see cref="ArgumentException"/> listing every problem when the template may not be saved.</summary>
        public void EnsureValidForSave(NotificationTemplateAggregate template)
        {
            ArgumentNullException.ThrowIfNull(template);
            var errors = Validate(template.Contents, template.Placeholders);
            if (errors.Count > 0)
            {
                throw new ArgumentException($"The template '{template.Key}' cannot be saved: {string.Join(" ", errors)}", nameof(template));
            }
        }

        private static void ValidateOne(string source, string where, PlaceholderSchema schema, List<string> errors)
        {
            if (!Parser.TryParse(source, out var parsed, out var parseError))
            {
                errors.Add($"The {where} is not a valid Liquid template: {parseError}");
                return;
            }

            if (OversizedRange(parsed) is { } range)
            {
                errors.Add($"The {where} {range}");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in OutputRootRegex().Matches(source))
            {
                var name = match.Groups[1].Value;
                if (seen.Add(name) && !schema.Declares(name))
                {
                    errors.Add($"The {where} references undeclared placeholder '{name}'.");
                }
            }
        }

        /// <summary>
        /// Renders <paramref name="content"/> with <paramref name="parameters"/> for <paramref name="target"/>. A parse
        /// error, an exceeded budget, a cancelled token or an empty rendered body is a failed result, never an exception.
        /// </summary>
        public async Task<NotificationRenderResult> RenderAsync(
            Content content, IReadOnlyDictionary<string, string?> parameters, NotificationRenderTarget target, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(parameters);
            var encoder = EncoderFor(target);

            if (ct.IsCancellationRequested)
            {
                return NotificationRenderResult.Fail("The render was cancelled or exceeded its time budget before it started.");
            }

            // The subject is TEXT on every target - an e-mail's Subject header, and in the app a line Blazor escapes itself - so it
            // is never HTML-encoded (2026-10-10: a mention's subject arrived as "host mentioned you on &#x27;Orders&#x27;", and
            // every export-ready mail read "Your export &#x27;Q3 invoices&#x27; is ready"). The body keeps its target's encoder.
            var subject = await RenderOneAsync(content.Subject, parameters, NullEncoder.Default);
            if (subject.Error is not null)
            {
                return NotificationRenderResult.Fail($"The subject could not be rendered: {subject.Error}");
            }

            if (ct.IsCancellationRequested)
            {
                return NotificationRenderResult.Fail("The render was cancelled or exceeded its time budget.");
            }

            var body = await RenderOneAsync(content.Body, parameters, encoder);
            if (body.Error is not null)
            {
                return NotificationRenderResult.Fail($"The body could not be rendered: {body.Error}");
            }

            if (ct.IsCancellationRequested)
            {
                return NotificationRenderResult.Fail("The render was cancelled or exceeded its time budget.");
            }

            if (string.IsNullOrWhiteSpace(body.Output))
            {
                return NotificationRenderResult.Fail("The body rendered empty.");
            }

            return NotificationRenderResult.Ok(new Payload(subject.Output!, body.Output!));
        }

        private static async Task<(string? Output, string? Error)> RenderOneAsync(
            string source, IReadOnlyDictionary<string, string?> parameters, TextEncoder encoder)
        {
            if (!Parser.TryParse(source, out var template, out var parseError))
            {
                return (null, $"invalid Liquid: {parseError}");
            }

            // BEFORE rendering: Fluid builds a range's whole array before a loop takes its first step, so MaxSteps
            // cannot stop it (measured - see OversizedRange).
            if (OversizedRange(template) is { } range)
            {
                return (null, range);
            }

            var context = new TemplateContext(Options);
            foreach (var (name, value) in parameters)
            {
                context.SetValue(name, value ?? string.Empty);
            }

            try
            {
                return (await template.RenderAsync(context, encoder), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // MaxSteps/MaxRecursion surface as InvalidOperationException; a filter can throw anything. The message
                // is Fluid's own (it names the budget, never a parameter value).
                return (null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>The widest LITERAL range a template may hold: a wider one could never finish inside <see cref="MaxSteps"/>.</summary>
        public const int MaxRangeItems = MaxSteps;

        /// <summary>
        /// Refuses a literal range wider than <see cref="MaxRangeItems"/>, anywhere in the parsed template.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why MaxSteps is not enough.</b> Fluid 2.31 evaluates <c>(1..100000000)</c> into a complete array of values
        /// BEFORE the loop over it takes a single step, so the step budget never gets a say: measured, the two runaway-loop
        /// tests ran out of memory under a 4 GB heap limit, and without one they pushed the test host past 12 GB. Any
        /// operator allowed to edit a template could do the same to a live host. <c>RangeExpression</c> is sealed, with
        /// read-only bounds, and <see cref="TemplateOptions"/> has no range limit, so the parsed tree is inspected instead.
        /// </para>
        /// <para>
        /// <b>What it cannot see:</b> a range whose bound is not a literal - <c>(1..count)</c>, <c>(1..i)</c> inside another
        /// loop - is known only while rendering. Parameters are server-derived strings; that residue is recorded on BL-155.
        /// </para>
        /// </remarks>
        internal static string? OversizedRange(object template)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return Visit(template);

            string? Visit(object? node)
            {
                if (node is null || node is string || !visited.Add(node))
                {
                    return null;
                }

                if (node is Fluid.Ast.RangeExpression range
                    && range.From is Fluid.Ast.LiteralExpression from
                    && range.To is Fluid.Ast.LiteralExpression to)
                {
                    var items = to.Value.ToNumberValue() - from.Value.ToNumberValue() + 1;
                    if (items > MaxRangeItems)
                    {
                        return $"holds a range of {items:0} items; at most {MaxRangeItems} are allowed (a wider one could never finish inside the render budget).";
                    }
                }

                if (node is System.Collections.IEnumerable items2)
                {
                    foreach (var item in items2)
                    {
                        if (Visit(item) is { } found)
                        {
                            return found;
                        }
                    }

                    return null;
                }

                // Walk the syntax tree only - the template and its statements and expressions - never a runtime value.
                var ns = node.GetType().Namespace;
                if (ns != "Fluid.Ast" && ns != "Fluid.Parser")
                {
                    return null;
                }

                foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length == 0 && Visit(property.GetValue(node)) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }
        }

        // The leading root identifier of an output tag: {{ name }}, {{- name | filter }}, {{ user.email }}.
        [GeneratedRegex(@"\{\{-?\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
        private static partial Regex OutputRootRegex();

        /// <summary>A file provider in which every path is an existing, EMPTY file - so an include renders nothing and reads no disk.</summary>
        private sealed class EmptyFileProvider : IFileProvider
        {
            public IFileInfo GetFileInfo(string subpath) => new EmptyFile(subpath);

            public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

            public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

            private sealed class EmptyFile(string name) : IFileInfo
            {
                public bool Exists => true;

                public long Length => 0;

                public string? PhysicalPath => null;

                public string Name => name ?? string.Empty;

                public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

                public bool IsDirectory => false;

                public Stream CreateReadStream() => new MemoryStream([], writable: false);
            }
        }
    }

    /// <summary>
    /// The built-in notification templates: <c>generic.message</c> ONLY - a subject and a body placeholder
    /// that a producer fills verbatim. Every later module brings its own templates. Created per SITE (the template key
    /// is site-unique); the boot/upgrade reconciler (a later BL-78 step) creates an absent one and never overwrites an
    /// operator's edit.
    /// </summary>
    public static class NotificationTemplateCatalogue
    {
        public const string GenericMessageKey = "generic.message";

        /// <summary>The built-in templates for a site, each with a fresh id.</summary>
        public static IReadOnlyList<NotificationTemplateAggregate> All(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return
            [
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, GenericMessageKey,
                    supportedChannels: [ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook],
                    contents: [new LocalizedContent(new Locale("en"), new Content("{{ subject }}", "{{ body }}"))],
                    placeholders: new PlaceholderSchema(["subject", "body"])),
            ];
        }
    }
}
