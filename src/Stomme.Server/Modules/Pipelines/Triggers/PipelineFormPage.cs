#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>A published form as the site serves it (n8n's Form trigger): what the Form step says, literally.</summary>
    public sealed record PipelineFormSpec(string Name, string Title, string? Description, IReadOnlyList<PipelineFormField> Fields, string SubmitLabel, string? Answer)
    {
        /// <summary>a next page may follow it (a Wait for a form with Next page in its pipeline) - a submission waits to be taken on.</summary>
        public bool LeadsOn { get; init; }

        /// <summary>a next page's - the name of the Form trigger's form it follows, whose thank-you ends the pages; null for any other form.</summary>
        public string? StartedBy { get; init; }

        /// <summary>n8n's redirect after a submission: where the thank-you takes the person on to; null shows the Answer.</summary>
        public string? Redirect { get; init; }
    }

    /// <summary>
    /// The form's page: one standalone HTML document, nothing from the site's theme or scripts - every text the author wrote is
    /// ENCODED (a label is never markup), the page loads nothing (its policy allows its own inline style and posting back to
    /// itself, nothing else), and it adapts to light and dark through the browser's own colours. The field the honeypot hides
    /// is filled only by robots: a submission that fills it is thanked and dropped.
    /// </summary>
    public static class PipelineFormPage
    {
        public const string HoneypotField = "__website";

        /// <summary>The pages' rate-limit policy (<c>Pipelines:Forms:RequestsPerMinute</c>), apart from the webhooks'.</summary>
        public const string PolicyName = "pipelines-forms";

        /// <summary>The page's Content-Security-Policy: its inline style, a post back to itself, nothing else - no script, no frame around it.</summary>
        public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'";

        private const string Style = """
            :root { color-scheme: light dark; }
            body { margin: 0; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: Canvas; color: CanvasText; }
            main { box-sizing: border-box; max-width: 36rem; margin: 0 auto; padding: 2rem 16px; }
            h1 { font-size: 1.5rem; margin: 0 0 0.5rem; }
            .description { margin: 0 0 1.5rem; white-space: pre-line; }
            .field { margin: 0 0 1rem; }
            .field > label, .field > .label { display: block; font-weight: 600; margin: 0 0 0.25rem; }
            input[type=text], input[type=email], input[type=number], input[type=date], select, textarea { box-sizing: border-box; width: 100%; font: inherit; padding: 0.45rem 0.55rem; border: 1px solid GrayText; border-radius: 0.375rem; background: Field; color: FieldText; }
            textarea { min-height: 6rem; }
            .check { display: flex; gap: 0.5rem; align-items: flex-start; font-weight: 400; }
            .required { font-weight: 400; color: GrayText; }
            .error { margin: 0.25rem 0 0; font-weight: 600; }
            .note { margin: 0.25rem 0 0; color: GrayText; }
            .errors { margin: 0 0 1rem; padding: 0.5rem 0.75rem; border: 2px solid CanvasText; border-radius: 0.375rem; }
            button { font: inherit; font-weight: 600; padding: 0.5rem 1.25rem; border: 1px solid ButtonBorder; border-radius: 0.375rem; background: ButtonFace; color: ButtonText; cursor: pointer; }
            .hp { position: absolute; left: -10000px; width: 1px; height: 1px; overflow: hidden; }
            """;

        /// <summary>
        /// What the form's address gives its fields (n8n's prefilled fields): <c>?name=Ada&amp;source=newsletter</c> - a field's
        /// key and a value, a file field never; none when the address names no field.
        /// </summary>
        public static Dictionary<string, string?> FromAddress(PipelineFormSpec form, IEnumerable<KeyValuePair<string, string?>> query)
        {
            var keys = form.Fields.Where(field => field.Type != PipelineFormFieldTypes.File).Select(PipelineForms.KeyOf).ToHashSet(StringComparer.Ordinal);
            var given = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in query)
            {
                if (keys.Contains(key) && value is { Length: <= PipelineForms.MaxValueLength })
                {
                    given[key] = value;
                }
            }

            return given;
        }

        /// <summary>The form, with what was given and what was wrong with it when a submission was refused.</summary>
        public static string Render(PipelineFormSpec form, IReadOnlyDictionary<string, string?>? given = null, IReadOnlyDictionary<string, string>? errors = null)
        {
            var html = new StringBuilder();
            Start(html, form.Title);
            html.Append("<h1>").Append(Encode(form.Title)).Append("</h1>");
            if (!string.IsNullOrWhiteSpace(form.Description))
            {
                html.Append("<p class=\"description\">").Append(Encode(form.Description)).Append("</p>");
            }

            if (errors is { Count: > 0 })
            {
                html.Append("<p class=\"errors\" role=\"alert\">Please check the answers marked below.</p>");
            }

            // A file field's form sends its files: multipart, which the receiver reads part by part.
            var files = form.Fields.Any(field => field.Type == PipelineFormFieldTypes.File);
            html.Append("<form method=\"post\" accept-charset=\"utf-8\"").Append(files ? " enctype=\"multipart/form-data\"" : "").Append('>');
            for (var index = 0; index < form.Fields.Count; index++)
            {
                Field(html, form.Fields[index], index, given, errors);
            }

            html.Append("<p class=\"hp\" aria-hidden=\"true\"><label>Leave this empty <input type=\"text\" name=\"").Append(HoneypotField)
                .Append("\" tabindex=\"-1\" autocomplete=\"off\"></label></p>");
            html.Append("<button type=\"submit\">").Append(Encode(string.IsNullOrWhiteSpace(form.SubmitLabel) ? "Submit" : form.SubmitLabel)).Append("</button>");
            html.Append("</form>");
            return End(html);
        }

        /// <summary>A page that only says something: a form closed, a form sent.</summary>
        public static string Notice(string title, string text)
        {
            var html = new StringBuilder();
            Start(html, title);
            html.Append("<h1>").Append(Encode(title)).Append("</h1>");
            html.Append("<p class=\"description\" role=\"status\">").Append(Encode(text)).Append("</p>");
            return End(html);
        }

        /// <summary>
        /// The page after a submission: the form's answer, or a plain thank-you - or, with an address to go on to, a page that takes
        /// the person there (a refresh, and a link for a browser that does not follow one). The POST cannot redirect off the site
        /// itself: the page's own policy (form-action 'self') would refuse it; the thank-you is a page of its own.
        /// </summary>
        public static string Thanks(PipelineFormSpec form)
        {
            var html = new StringBuilder();
            if (form.Redirect is { } redirect && Modules.Pipelines.Steps.Form.IsRedirect(redirect))
            {
                var address = redirect.Trim();
                Start(html, form.Title, "<meta http-equiv=\"refresh\" content=\"0; url=" + Encode(address) + "\">");
                html.Append("<h1>").Append(Encode(form.Title)).Append("</h1>");
                html.Append("<p class=\"description\" role=\"status\">Thank you - your answers were sent. <a href=\"").Append(Encode(address)).Append("\">Go on</a>.</p>");
                return End(html);
            }

            Start(html, form.Title);
            html.Append("<h1>").Append(Encode(form.Title)).Append("</h1>");
            html.Append("<p class=\"description\" role=\"status\">").Append(Encode(string.IsNullOrWhiteSpace(form.Answer) ? "Thank you - your answers were sent." : form.Answer)).Append("</p>");
            return End(html);
        }

        private static void Field(StringBuilder html, PipelineFormField field, int index, IReadOnlyDictionary<string, string?>? given, IReadOnlyDictionary<string, string>? errors)
        {
            var key = PipelineForms.KeyOf(field);
            var id = "field-" + index;
            var value = given is not null && given.TryGetValue(key, out var typed) ? typed ?? "" : "";
            if (field.Type == PipelineFormFieldTypes.Hidden)
            {
                // n8n's Hidden Field: carried unseen - from the address, else its own value - encoded as every value here is.
                var carried = given is not null && given.ContainsKey(key) ? value : field.Value ?? "";
                html.Append("<input type=\"hidden\" name=\"").Append(Encode(key)).Append("\" value=\"").Append(Encode(carried)).Append("\">");
                return;
            }

            var error = errors is not null && errors.TryGetValue(key, out var wrong) ? wrong : null;
            var described = error is null ? "" : $" aria-invalid=\"true\" aria-describedby=\"{id}-error\"";
            var required = field.Required ? " required" : "";
            var placeholder = string.IsNullOrWhiteSpace(field.Placeholder) ? "" : " placeholder=\"" + Encode(field.Placeholder) + "\"";
            html.Append("<div class=\"field\">");
            if (field.Type == PipelineFormFieldTypes.Checkbox)
            {
                var ticked = value is "on" or "true" ? " checked" : "";
                html.Append("<label class=\"check\" for=\"").Append(id).Append("\"><input type=\"checkbox\" id=\"").Append(id).Append("\" name=\"").Append(Encode(key))
                    .Append("\"").Append(ticked).Append(required).Append(described).Append("> <span>").Append(Encode(field.Label)).Append(RequiredMark(field)).Append("</span></label>");
            }
            else
            {
                html.Append("<label for=\"").Append(id).Append("\">").Append(Encode(field.Label)).Append(RequiredMark(field)).Append("</label>");
                var common = $" id=\"{id}\" name=\"{Encode(key)}\"{required}{described}";
                switch (field.Type)
                {
                    case PipelineFormFieldTypes.LongText:
                        html.Append("<textarea").Append(common).Append(placeholder).Append(" maxlength=\"").Append(PipelineForms.MaxValueLength).Append("\">")
                            .Append(Encode(value)).Append("</textarea>");
                        break;
                    case PipelineFormFieldTypes.File:
                        // A browser never fills a file field again: after a refusal the person chooses the file anew - said, unless the field
                        // has its own error to say.
                        var accept = (field.Accept ?? new List<string>()).Where(PipelineForms.IsExtension).ToList();
                        html.Append("<input type=\"file\"").Append(common).Append(accept.Count == 0 ? "" : " accept=\"" + Encode(string.Join(",", accept)) + "\"").Append('>');
                        if (errors is { Count: > 0 } && error is null)
                        {
                            html.Append("<p class=\"note\">Choose the file again - a form sent back keeps none.</p>");
                        }

                        break;
                    case PipelineFormFieldTypes.Dropdown:
                        html.Append("<select").Append(common).Append('>');
                        html.Append("<option value=\"\">").Append(field.Required ? "Choose…" : "(none)").Append("</option>");
                        foreach (var option in (field.Options ?? new List<string>()).Where(option => !string.IsNullOrWhiteSpace(option)))
                        {
                            html.Append("<option value=\"").Append(Encode(option)).Append('"').Append(option == value ? " selected" : "").Append('>').Append(Encode(option)).Append("</option>");
                        }

                        html.Append("</select>");
                        break;
                    default:
                        var type = field.Type switch
                        {
                            PipelineFormFieldTypes.Email => "email",
                            PipelineFormFieldTypes.Number => "number\" step=\"any",
                            PipelineFormFieldTypes.Date => "date",
                            _ => "text",
                        };
                        html.Append("<input type=\"").Append(type).Append('"').Append(common).Append(placeholder).Append(" maxlength=\"").Append(PipelineForms.MaxValueLength)
                            .Append("\" value=\"").Append(Encode(value)).Append("\">");
                        break;
                }
            }

            if (error is not null)
            {
                html.Append("<p class=\"error\" id=\"").Append(id).Append("-error\">").Append(Encode(error)).Append("</p>");
            }

            html.Append("</div>");
        }

        private static string RequiredMark(PipelineFormField field) => field.Required ? " <span class=\"required\">(required)</span>" : "";

        private static void Start(StringBuilder html, string title, string? head = null) =>
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\">").Append(head ?? "").Append("<title>")
                .Append(Encode(title)).Append("</title><style>").Append(Style).Append("</style></head><body><main>");

        private static string End(StringBuilder html) => html.Append("</main></body></html>").ToString();

        private static string Encode(string? text) => HtmlEncoder.Default.Encode(text ?? "");
    }
}
