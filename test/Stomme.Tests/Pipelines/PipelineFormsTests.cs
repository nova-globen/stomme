#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>BL-229 - n8n's Form trigger: the fields' rules (the editor's, the publish's and the receiver's alike) and the page the site serves.</summary>
    public sealed class PipelineFormsTests
    {
        private static List<PipelineFormField> Contact() =>
        [
            new() { Label = "Your name", Required = true },
            new() { Label = "E-mail", Type = PipelineFormFieldTypes.Email, Required = true },
            new() { Label = "Topic", Type = PipelineFormFieldTypes.Dropdown, Options = ["Question", "Other"] },
            new() { Label = "How many", Name = "count", Type = PipelineFormFieldTypes.Number },
            new() { Label = "When", Type = PipelineFormFieldTypes.Date },
            new() { Label = "Message", Type = PipelineFormFieldTypes.LongText },
            new() { Label = "I agree", Type = PipelineFormFieldTypes.Checkbox, Required = true },
        ];

        [Fact]
        public void A_field_is_kept_under_its_name_else_its_label_made_a_key()
        {
            Assert.Equal(new[] { "your_name", "e_mail", "topic", "count", "when", "message", "i_agree" }, Contact().Select(PipelineForms.KeyOf));
            Assert.Equal("field", PipelineForms.KeyOf(new PipelineFormField { Label = " -- " }));
            Assert.Equal("kundens_namn", PipelineForms.KeyOf(new PipelineFormField { Label = "Kundens  namn!" }));
            Assert.Equal("x", PipelineForms.KeyOf(new PipelineFormField { Label = "Anything", Name = " x " }));

            // What the editor writes is what the publish and the receiver read.
            var written = PipelineForms.Serialize(Contact());
            var read = PipelineForms.Parse(written, out var error)!;
            Assert.Null(error);
            Assert.Equal(Contact().Select(PipelineForms.KeyOf), read.Select(PipelineForms.KeyOf));
            Assert.Equal(new[] { "Question", "Other" }, read[2].Options);
            Assert.Empty(PipelineForms.Parse("", out _)!);
            Assert.Null(PipelineForms.Parse("{ not fields", out error));
            Assert.StartsWith("The form's fields are not a list of fields", error);
        }

        [Fact]
        public void What_keeps_fields_from_being_a_form_is_named()
        {
            Assert.Empty(PipelineForms.Problems(Contact()));
            Assert.Equal(new[] { "The form has no fields." }, PipelineForms.Problems([]));
            Assert.Equal(new[]
            {
                "Field 1 has no label.",
                "'Pick' is a dropdown with no choices.",
                "'Size' is a 'slider', which is not a kind of field (text, long text, email, number, date, dropdown, checkbox, file, hidden).",
                "Two fields are kept under 'pick': give one a name of its own.",
                "'Sneaky' is kept as '__website': a name starting with '__' is the form's own.",
            }, PipelineForms.Problems(
            [
                new() { Label = " " },
                new() { Label = "Pick", Type = PipelineFormFieldTypes.Dropdown, Options = [" "] },
                new() { Label = "Size", Type = "slider" },
                new() { Label = "Other", Name = "pick" },
                new() { Label = "Sneaky", Name = "__website" },
            ]));
            Assert.Contains("A form takes at most 30 fields.", PipelineForms.Problems(Enumerable.Range(1, 31).Select(i => new PipelineFormField { Label = "F" + i }).ToList()));
        }

        [Fact]
        public void A_submission_becomes_one_answer_per_field_or_says_what_is_wrong()
        {
            var answers = PipelineForms.Read(Contact(), new Dictionary<string, string?>
            {
                ["your_name"] = "  Ada ",
                ["e_mail"] = "ada@example.com",
                ["topic"] = "Question",
                ["count"] = "3.5",
                ["when"] = "2026-10-07",
                ["message"] = "Hello",
                ["i_agree"] = "on",
                ["not_a_field"] = "ignored",
            }.GetValueOrDefault, out var errors);
            Assert.Empty(errors);
            Assert.Equal("""{"your_name":"Ada","e_mail":"ada@example.com","topic":"Question","count":3.5,"when":"2026-10-07","message":"Hello","i_agree":true}""", answers!.ToJsonString());

            // The run keeps letters as typed - a Swedish name stays readable in its data and its files.
            var swedish = PipelineForms.Read([new() { Label = "Namn" }], new Dictionary<string, string?> { ["namn"] = "Åsa Kundén <AB>" }.GetValueOrDefault, out _)!;
            Assert.Equal("""{"namn":"Åsa Kundén <AB>"}""", PipelineForms.ToJson(swedish));

            // Optional fields left empty: an empty text, no number, an unticked box.
            var fewest = PipelineForms.Read(Contact(), new Dictionary<string, string?> { ["your_name"] = "Ada", ["e_mail"] = "a@b.se", ["i_agree"] = "true" }.GetValueOrDefault, out errors);
            Assert.Empty(errors);
            Assert.Equal("""{"your_name":"Ada","e_mail":"a@b.se","topic":"","count":null,"when":"","message":"","i_agree":true}""", fewest!.ToJsonString());

            Assert.Null(PipelineForms.Read(Contact(), new Dictionary<string, string?>
            {
                ["e_mail"] = "ada@example",
                ["topic"] = "Spam",
                ["count"] = "three",
                ["when"] = "07/10/2026",
                ["message"] = new string('x', PipelineForms.MaxValueLength + 1),
            }.GetValueOrDefault, out errors));
            Assert.Equal(new Dictionary<string, string>
            {
                ["your_name"] = "This is required.",
                ["e_mail"] = "Not an e-mail address.",
                ["topic"] = "Not one of the choices.",
                ["count"] = "Not a number.",
                ["when"] = "Not a date (yyyy-mm-dd).",
                ["message"] = "At most 4000 characters.",
                ["i_agree"] = "Tick this to go on.",
            }, errors);
            Assert.Null(PipelineForms.Read([new() { Label = "N", Type = PipelineFormFieldTypes.Number }], new Dictionary<string, string?> { ["n"] = "NaN" }.GetValueOrDefault, out errors));
            Assert.Equal("Not a number.", errors["n"]);
        }

        [Fact]
        public void A_sample_submission_answers_every_field_as_its_kind_asks()
        {
            var sample = JsonNode.Parse(PipelineForms.Sample(Contact()))!.AsObject();
            Assert.Equal(("Ada Lovelace", "ada@example.com", "Question", 42, "2026-01-15", true),
                (sample["your_name"]!.GetValue<string>(), sample["e_mail"]!.GetValue<string>(), sample["topic"]!.GetValue<string>(), sample["count"]!.GetValue<int>(),
                 sample["when"]!.GetValue<string>(), sample["i_agree"]!.GetValue<bool>()));
            // ...and it is a submission the form accepts.
            Assert.NotNull(PipelineForms.Read(Contact(), key => sample[key] is { } value ? (value.GetValueKind() == System.Text.Json.JsonValueKind.True ? "on" : value.ToString()) : null, out var errors));
            Assert.Empty(errors);
        }

        [Fact]
        public void The_page_encodes_every_text_keeps_what_was_given_and_marks_what_is_wrong()
        {
            var fields = Contact();
            fields[0].Placeholder = "\"><script>alert(1)</script>";
            var form = new PipelineFormSpec("contact", "Contact <us>", "Write to us.\n<script>alert(1)</script>", fields, "Send", null);

            var page = PipelineFormPage.Render(form);
            Assert.DoesNotContain("<script", page);
            Assert.Contains("<title>Contact &lt;us&gt;</title>", page);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
            Assert.Contains("<label for=\"field-0\">Your name <span class=\"required\">(required)</span></label><input type=\"text\" id=\"field-0\" name=\"your_name\" required", page);
            Assert.Contains("<input type=\"email\" id=\"field-1\" name=\"e_mail\" required", page);
            Assert.Contains("<select id=\"field-2\" name=\"topic\"><option value=\"\">(none)</option><option value=\"Question\">Question</option>", page);
            Assert.Contains("type=\"number\" step=\"any\" id=\"field-3\" name=\"count\"", page);
            Assert.Contains("<textarea id=\"field-5\" name=\"message\"", page);
            Assert.Contains("<input type=\"checkbox\" id=\"field-6\" name=\"i_agree\" required>", page);
            Assert.Contains("name=\"" + PipelineFormPage.HoneypotField + "\" tabindex=\"-1\"", page);
            Assert.Contains("<button type=\"submit\">Send</button>", page);
            Assert.DoesNotContain("role=\"alert\"", page);

            var again = PipelineFormPage.Render(form, new Dictionary<string, string?> { ["your_name"] = "Ada \"the first\"", ["topic"] = "Other", ["i_agree"] = "on" },
                new Dictionary<string, string> { ["e_mail"] = "This is required." });
            Assert.Contains("role=\"alert\"", again);
            Assert.Contains("value=\"Ada &quot;the first&quot;\"", again);
            Assert.Contains("<option value=\"Other\" selected>Other</option>", again);
            Assert.Contains("name=\"i_agree\" checked required>", again);
            Assert.Contains("name=\"e_mail\" required aria-invalid=\"true\" aria-describedby=\"field-1-error\"", again);
            Assert.Contains("<p class=\"error\" id=\"field-1-error\">This is required.</p>", again);

            Assert.Contains("Thank you - your answers were sent.", PipelineFormPage.Thanks(form));
            Assert.Contains("We write back within a day &amp; a half.", PipelineFormPage.Thanks(form with { Answer = "We write back within a day & a half." }));
            Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'", PipelineFormPage.ContentSecurityPolicy);
        }

        // BL-229 - n8n's File field: the kinds of file a field takes, refused when not one; at most five file fields; a submission
        // answers a file field with its file's name (the receiver makes the rest), refusing one missing or of another kind; a
        // sample answers with a path; the page sends multipart and asks for the kinds it takes.
        [Fact]
        public void A_file_field_takes_the_kinds_it_names_and_its_page_sends_multipart()
        {
            var invoice = new PipelineFormField { Label = "Invoice", Type = PipelineFormFieldTypes.File, Required = true, Accept = [".pdf", ".PNG"] };
            Assert.Empty(PipelineForms.Problems([invoice]));
            Assert.Contains("takes 'pdf', which is not a kind of file", string.Join(" ", PipelineForms.Problems([new() { Label = "Bad", Type = PipelineFormFieldTypes.File, Accept = ["pdf"] }])));
            var six = Enumerable.Range(1, 6).Select(n => new PipelineFormField { Label = "File " + n, Type = PipelineFormFieldTypes.File }).ToList();
            Assert.Contains("at most 5 file fields", string.Join(" ", PipelineForms.Problems(six)));
            Assert.True(PipelineForms.Accepts(invoice, "Scan.Png"));
            Assert.False(PipelineForms.Accepts(invoice, "scan.png.exe"));
            Assert.True(PipelineForms.Accepts(new PipelineFormField { Type = PipelineFormFieldTypes.File }, "anything.bin")); // no kinds: any

            Assert.Null(PipelineForms.Read([invoice], _ => null, out var missing));
            Assert.Equal("Choose a file.", missing["invoice"]);
            Assert.Null(PipelineForms.Read([invoice], _ => "setup.exe", out var wrong));
            Assert.Equal("Only .pdf, .PNG files.", wrong["invoice"]);
            Assert.Equal("{\"invoice\":{\"name\":\"a.pdf\"}}", PipelineForms.ToJson(PipelineForms.Read([invoice], _ => "a.pdf", out _)!));
            Assert.Equal("forms/sample/document.pdf", System.Text.Json.Nodes.JsonNode.Parse(PipelineForms.Sample([invoice]))!["invoice"]!["path"]!.GetValue<string>());

            var page = PipelineFormPage.Render(new PipelineFormSpec("in", "In", null, [invoice], "Send", null));
            Assert.Contains("<form method=\"post\" accept-charset=\"utf-8\" enctype=\"multipart/form-data\">", page);
            Assert.Contains("<input type=\"file\" id=\"field-0\" name=\"invoice\" required accept=\".pdf,.PNG\">", page);
            Assert.DoesNotContain("enctype", PipelineFormPage.Render(new PipelineFormSpec("in", "In", null, [new() { Label = "Name" }], "Send", null)));
        }

        // BL-229, n8n's redirect after a submission: the thank-you takes the person on - a refresh and a link, the address encoded; an
        // address that is not http(s) is never followed, and a form with none shows its Answer.
        [Fact]
        public void A_form_with_an_address_to_go_to_takes_the_person_there_from_its_thank_you()
        {
            var fields = new List<PipelineFormField> { new() { Label = "Name" } };
            var away = PipelineFormPage.Thanks(new PipelineFormSpec("signup", "Sign up", null, fields, "Join", "Welcome!") { Redirect = "https://example.com/welcome?a=1&b=2" });
            Assert.Contains("<meta http-equiv=\"refresh\" content=\"0; url=https://example.com/welcome?a=1&amp;b=2\">", away);
            Assert.Contains("<a href=\"https://example.com/welcome?a=1&amp;b=2\">Go on</a>", away);
            Assert.DoesNotContain("Welcome!", away);

            var answer = PipelineFormPage.Thanks(new PipelineFormSpec("signup", "Sign up", null, fields, "Join", "Welcome!"));
            Assert.Contains("Welcome!", answer);
            Assert.DoesNotContain("refresh", answer);
            Assert.DoesNotContain("refresh", PipelineFormPage.Thanks(new PipelineFormSpec("signup", "Sign up", null, fields, "Join", null) { Redirect = "javascript:alert(1)" }));
            Assert.Equal((true, true, false, false, false), (Stomme.Modules.Pipelines.Steps.Form.IsRedirect("https://example.com"), Stomme.Modules.Pipelines.Steps.Form.IsRedirect(" http://example.com/x "),
                Stomme.Modules.Pipelines.Steps.Form.IsRedirect("javascript:alert(1)"), Stomme.Modules.Pipelines.Steps.Form.IsRedirect("/welcome"), Stomme.Modules.Pipelines.Steps.Form.IsRedirect(null)));
        }

        // BL-229, n8n's Hidden Field: a value carried unseen - from the form's address, else its own - kept as sent, never required;
        // a hidden field needs a name, not a label; the address fills any field but a file field, never past the longest answer.
        [Fact]
        public void A_hidden_field_carries_its_value_and_the_address_fills_a_form()
        {
            var fields = new List<PipelineFormField>
            {
                new() { Label = "Name", Required = true },
                new() { Type = PipelineFormFieldTypes.Hidden, Name = "source", Value = "web", Required = true },
                new() { Label = "CV", Type = PipelineFormFieldTypes.File },
            };
            Assert.Empty(PipelineForms.Problems(fields));
            Assert.Equal(new[] { "Field 1 is hidden and has no name to be kept as." }, PipelineForms.Problems(new[] { new PipelineFormField { Type = PipelineFormFieldTypes.Hidden } }));

            var answers = PipelineForms.Read(fields, key => key == "name" ? "Ada" : key == "source" ? " newsletter " : null, out var errors)!;
            Assert.Empty(errors);
            Assert.Equal("newsletter", answers["source"]!.GetValue<string>());
            Assert.NotNull(PipelineForms.Read(fields, key => key == "name" ? "Ada" : null, out _)); // not required, though marked so
            Assert.Equal("web", JsonNode.Parse(PipelineForms.Sample(fields))!["source"]!.GetValue<string>());

            var form = new PipelineFormSpec("signup", "Sign up", null, fields, "Join", null);
            var given = PipelineFormPage.FromAddress(form, new Dictionary<string, string?> { ["name"] = "Ada", ["source"] = "<news>", ["cv"] = "x.pdf", ["other"] = "y", ["sent"] = "1" });
            Assert.Equal(new[] { "name", "source" }, given.Keys.OrderBy(key => key));
            var html = PipelineFormPage.Render(form, given);
            Assert.Contains("<input type=\"hidden\" name=\"source\" value=\"&lt;news&gt;\">", html);
            Assert.Contains("value=\"Ada\"", html);
            Assert.DoesNotContain(">source<", html); // nobody sees it
            Assert.Contains("<input type=\"hidden\" name=\"source\" value=\"web\">", PipelineFormPage.Render(form)); // no address: its own value
            Assert.Empty(PipelineFormPage.FromAddress(form, new Dictionary<string, string?> { ["name"] = new string('a', PipelineForms.MaxValueLength + 1) }));
        }
    }
}
