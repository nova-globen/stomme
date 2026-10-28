#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>A field of a pipeline's form (BL-229, n8n's Form trigger): what the page asks, and the key the answer is kept under.</summary>
    public sealed class PipelineFormField
    {
        public string Label { get; set; } = "";

        /// <summary>The key the submission carries the answer under; empty: made from the label (<see cref="PipelineForms.KeyOf"/>).</summary>
        public string? Name { get; set; }

        /// <summary>One of <see cref="PipelineFormFieldTypes"/>.</summary>
        public string Type { get; set; } = PipelineFormFieldTypes.Text;

        public bool Required { get; set; }

        /// <summary>A dropdown's choices.</summary>
        public List<string> Options { get; set; } = new();

        public string? Placeholder { get; set; }

        /// <summary>A file field's kinds of file, as extensions (<c>.pdf</c>, <c>.png</c>); none: any file.</summary>
        public List<string> Accept { get; set; } = new();

        /// <summary>A hidden field's value when the form's address gives none (<c>?key=value</c>).</summary>
        public string? Value { get; set; }
    }

    /// <summary>What a form field asks for.</summary>
    public static class PipelineFormFieldTypes
    {
        public const string Text = "text";
        public const string LongText = "long text";
        public const string Email = "email";
        public const string Number = "number";
        public const string Date = "date";
        public const string Dropdown = "dropdown";
        public const string Checkbox = "checkbox";

        /// <summary>A file the person uploads (n8n's File field): kept in the site's pipeline folder, its path in the answer.</summary>
        public const string File = "file";

        /// <summary>
        /// A value the page carries unseen (n8n's Hidden Field): the form's address gives it (<c>?source=newsletter</c>), else the
        /// field's own <see cref="PipelineFormField.Value"/>. Whoever sends the form can change it - it says where a person came from,
        /// never who they are.
        /// </summary>
        public const string Hidden = "hidden";

        public static readonly IReadOnlyList<string> All = [Text, LongText, Email, Number, Date, Dropdown, Checkbox, File, Hidden];
    }

    /// <summary>
    /// BL-229 - n8n's Form trigger: a page the site serves for a pipeline (<c>api/Pipelines/forms/{name}</c>, behind
    /// <c>Pipelines:Forms:Enabled</c>) whose submission starts it, the answers as its result - one JSON object, a key per field.
    /// The rules here are the editor's, the publish's and the receiver's alike.
    /// </summary>
    public static class PipelineForms
    {
        public const string StepType = "Stomme.Form";

        /// <summary>Wait for a form: a run paused on a form a person fills in at the run's form address - or, with <see cref="NextPageInput"/>, the next page of the form before it.</summary>
        public const string WaitStepType = "Stomme.WaitForForm";

        /// <summary>BL-229, n8n's multi-page forms: Wait for a form's tick that takes the person who sent the form before it straight on to it.</summary>
        public const string NextPageInput = "NextPage";

        /// <summary>Whether a step is a Wait for a form with Next page ticked.</summary>
        public static bool IsNextPage(PipelineGraphStep step) =>
            step.Type == WaitStepType && step.Inputs.TryGetValue(NextPageInput, out var ticked) && bool.TryParse(ticked?.Trim(), out var nextPage) && nextPage;

        public const int MaxFields = 30;

        public const int MaxLabelLength = 200;

        public const int MaxValueLength = 4000;

        public const int MaxOptions = 100;

        /// <summary>The most file fields a form takes - each one file.</summary>
        public const int MaxFileFields = 5;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // The answers as the run keeps them: letters as typed ("Kundén", not "Kundén") - they are data, read by the run's
        // steps and shown encoded by whatever displays them, never written into a page as they are.
        private static readonly JsonSerializerOptions AnswersJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>The answers as the run's result.</summary>
        public static string ToJson(JsonObject answers) => answers.ToJsonString(AnswersJson);

        /// <summary>The key an answer is kept under: the field's name, else its label in lower case, words joined by '_'.</summary>
        public static string KeyOf(PipelineFormField field)
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                return field.Name.Trim();
            }

            var key = new StringBuilder();
            foreach (var c in (field.Label ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    key.Append(c);
                }
                else if (key.Length > 0 && key[^1] != '_')
                {
                    key.Append('_');
                }
            }

            var made = key.ToString().Trim('_');
            return made.Length == 0 ? "field" : made;
        }

        /// <summary>The fields as the step keeps them (JSON); none for an empty value; null with the reason for one that is not fields.</summary>
        public static List<PipelineFormField>? Parse(string? json, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<PipelineFormField>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<PipelineFormField>>(json, Json) ?? new List<PipelineFormField>();
            }
            catch (JsonException ex)
            {
                error = "The form's fields are not a list of fields: " + ex.Message;
                return null;
            }
        }

        public static string Serialize(IEnumerable<PipelineFormField> fields) => JsonSerializer.Serialize(fields, Json);

        /// <summary>What keeps the fields from being a form: none at all, a field with no label, two with one key, an unknown type, a dropdown with no choices.</summary>
        public static List<string> Problems(IReadOnlyList<PipelineFormField>? fields)
        {
            var problems = new List<string>();
            if (fields is null || fields.Count == 0)
            {
                problems.Add("The form has no fields.");
                return problems;
            }

            if (fields.Count > MaxFields)
            {
                problems.Add($"A form takes at most {MaxFields} fields.");
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var label = (field.Label ?? "").Trim();
                var which = label.Length == 0 ? $"Field {index + 1}" : $"'{label}'";
                if (label.Length == 0 && field.Type == PipelineFormFieldTypes.Hidden)
                {
                    // Nobody sees a hidden field: a name is all it needs.
                    if (string.IsNullOrWhiteSpace(field.Name))
                    {
                        problems.Add($"Field {index + 1} is hidden and has no name to be kept as.");
                    }
                }
                else if (label.Length == 0)
                {
                    problems.Add($"Field {index + 1} has no label.");
                }
                else if (label.Length > MaxLabelLength)
                {
                    problems.Add($"{which}'s label is over {MaxLabelLength} characters.");
                }

                if (!PipelineFormFieldTypes.All.Contains(field.Type ?? ""))
                {
                    problems.Add($"{which} is a '{field.Type}', which is not a kind of field ({string.Join(", ", PipelineFormFieldTypes.All)}).");
                }

                if (field.Type == PipelineFormFieldTypes.Dropdown)
                {
                    var options = (field.Options ?? new List<string>()).Where(option => !string.IsNullOrWhiteSpace(option)).ToList();
                    if (options.Count == 0)
                    {
                        problems.Add($"{which} is a dropdown with no choices.");
                    }
                    else if (options.Count > MaxOptions)
                    {
                        problems.Add($"{which} has more than {MaxOptions} choices.");
                    }
                }

                if (field.Type == PipelineFormFieldTypes.File)
                {
                    foreach (var kind in field.Accept ?? new List<string>())
                    {
                        if (!IsExtension(kind))
                        {
                            problems.Add($"{which} takes '{kind}', which is not a kind of file - write one like .pdf.");
                        }
                    }
                }

                var key = KeyOf(field);
                if (key.StartsWith("__", StringComparison.Ordinal))
                {
                    problems.Add($"{which} is kept as '{key}': a name starting with '__' is the form's own.");
                }
                else if (!keys.Add(key))
                {
                    problems.Add($"Two fields are kept under '{key}': give one a name of its own.");
                }
            }

            if (fields.Count(field => field.Type == PipelineFormFieldTypes.File) > MaxFileFields)
            {
                problems.Add($"A form takes at most {MaxFileFields} file fields.");
            }

            return problems;
        }

        /// <summary>Whether a file field takes a file of this name: any, when it names no kinds; else one whose extension it names, in any case.</summary>
        public static bool Accepts(PipelineFormField field, string fileName)
        {
            var kinds = (field.Accept ?? new List<string>()).Where(IsExtension).ToList();
            var extension = System.IO.Path.GetExtension(fileName ?? "");
            return kinds.Count == 0 || kinds.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A kind of file as a field names it: a dot and up to ten letters or digits.</summary>
        public static bool IsExtension(string? kind) =>
            kind is { Length: > 1 and <= 11 } && kind[0] == '.' && kind.Skip(1).All(char.IsAsciiLetterOrDigit);

        /// <summary>
        /// The answers a submission gives, as the run's result: a key per field in the form's order - text as text, a number as a
        /// number, a checkbox as true or false. Null, with what is wrong per key, when an answer is missing or not what its field asks.
        /// </summary>
        public static JsonObject? Read(IReadOnlyList<PipelineFormField> fields, Func<string, string?> posted, out Dictionary<string, string> errors)
        {
            errors = new Dictionary<string, string>(StringComparer.Ordinal);
            var answers = new JsonObject();
            foreach (var field in fields)
            {
                var key = KeyOf(field);
                var raw = posted(key);
                if (field.Type == PipelineFormFieldTypes.File)
                {
                    // What is posted under a file field is the file's name; the receiver keeps the file and makes the answer.
                    var name = (raw ?? "").Trim();
                    if (name.Length == 0)
                    {
                        if (field.Required)
                        {
                            errors[key] = "Choose a file.";
                        }

                        answers[key] = null;
                    }
                    else if (!Accepts(field, name))
                    {
                        errors[key] = "Only " + string.Join(", ", (field.Accept ?? new List<string>()).Where(IsExtension)) + " files.";
                    }
                    else
                    {
                        answers[key] = new JsonObject { ["name"] = name };
                    }

                    continue;
                }

                if (field.Type == PipelineFormFieldTypes.Hidden)
                {
                    // Never required - nobody can fill it in - and kept as sent, at most so long.
                    var carried = (raw ?? "").Trim();
                    if (carried.Length > MaxValueLength)
                    {
                        errors[key] = $"At most {MaxValueLength} characters.";
                        continue;
                    }

                    answers[key] = carried;
                    continue;
                }

                if (field.Type == PipelineFormFieldTypes.Checkbox)
                {
                    var ticked = raw is not null && (raw == "on" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
                    if (field.Required && !ticked)
                    {
                        errors[key] = "Tick this to go on.";
                    }

                    answers[key] = ticked;
                    continue;
                }

                var value = (raw ?? "").Trim();
                if (value.Length == 0)
                {
                    if (field.Required)
                    {
                        errors[key] = "This is required.";
                    }

                    answers[key] = field.Type == PipelineFormFieldTypes.Number ? null : "";
                    continue;
                }

                if (value.Length > MaxValueLength)
                {
                    errors[key] = $"At most {MaxValueLength} characters.";
                    continue;
                }

                switch (field.Type)
                {
                    case PipelineFormFieldTypes.Email when !IsEmail(value):
                        errors[key] = "Not an e-mail address.";
                        break;
                    case PipelineFormFieldTypes.Number:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        {
                            answers[key] = number;
                        }
                        else
                        {
                            errors[key] = "Not a number.";
                        }

                        break;
                    case PipelineFormFieldTypes.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                        errors[key] = "Not a date (yyyy-mm-dd).";
                        break;
                    case PipelineFormFieldTypes.Dropdown when !(field.Options ?? new List<string>()).Contains(value, StringComparer.Ordinal):
                        errors[key] = "Not one of the choices.";
                        break;
                    default:
                        answers[key] = value;
                        break;
                }
            }

            return errors.Count == 0 ? answers : null;
        }

        /// <summary>Made-up answers to test the next steps with (the editor's "Use a sample submission"): what a person might send.</summary>
        public static string Sample(IReadOnlyList<PipelineFormField> fields)
        {
            var answers = new JsonObject();
            foreach (var field in fields)
            {
                var key = KeyOf(field);
                answers[key] = field.Type switch
                {
                    PipelineFormFieldTypes.Checkbox => true,
                    PipelineFormFieldTypes.Hidden => field.Value ?? "",
                    PipelineFormFieldTypes.Number => 42,
                    PipelineFormFieldTypes.Email => "ada@example.com",
                    PipelineFormFieldTypes.Date => "2026-01-15",
                    PipelineFormFieldTypes.Dropdown => (field.Options ?? new List<string>()).FirstOrDefault(option => !string.IsNullOrWhiteSpace(option)) ?? "",
                    PipelineFormFieldTypes.LongText => "A longer answer, as a person might write it.",
                    PipelineFormFieldTypes.File => new JsonObject
                    {
                        ["path"] = "forms/sample/" + SampleFileName(field),
                        ["name"] = SampleFileName(field),
                        ["size"] = 1024,
                        ["contentType"] = "application/octet-stream",
                    },
                    _ => key.Contains("name", StringComparison.Ordinal) ? "Ada Lovelace" : "An answer to " + (field.Label ?? key).Trim(),
                };
            }

            return ToJson(answers);
        }

        private static string SampleFileName(PipelineFormField field) =>
            "document" + ((field.Accept ?? new List<string>()).FirstOrDefault(IsExtension) ?? ".pdf");

        // One '@' with something before it, and a dot in what follows it; no spaces. The rest is the receiving system's to judge.
        private static bool IsEmail(string value)
        {
            var at = value.IndexOf('@');
            return at > 0 && at == value.LastIndexOf('@') && value.IndexOf('.', at) > at + 1 && !value.EndsWith('.') && !value.Any(char.IsWhiteSpace);
        }
    }
}
