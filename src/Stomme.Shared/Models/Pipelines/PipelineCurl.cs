#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's "Import cURL" on its HTTP Request node: a curl command, as an API's documentation gives one or a browser's
    /// "Copy as cURL" writes it, read into what the HTTP request step sends - its URL, method, headers, body, content type,
    /// authorization and timeout, a form's file. What the step cannot do the same way is said (<see cref="PipelineCurlRequest.Notes"/>)
    /// rather than dropped without a word: skipping TLS checks, writing the response to a local file, a form's other fields.
    /// </summary>
    public static class PipelineCurl
    {
        /// <summary>The request a curl command makes; null, with the reason, for text that is no curl command with a URL.</summary>
        public static PipelineCurlRequest? Parse(string? command, out string? error)
        {
            error = null;
            var words = Words(command ?? string.Empty, out error);
            if (words is null)
            {
                return null;
            }

            if (words.Count == 0 || !string.Equals(words[0], "curl", StringComparison.OrdinalIgnoreCase) && !words[0].EndsWith("/curl", StringComparison.OrdinalIgnoreCase) && !string.Equals(words[0], "curl.exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "Paste a command that starts with curl.";
                return null;
            }

            var request = new PipelineCurlRequest();
            string? method = null;
            var data = new List<string>();
            var forms = new List<(string Name, string Value)>();
            var asQuery = false;
            var head = false;
            string? jsonBody = null;
            for (var at = 1; at < words.Count; at++)
            {
                var word = words[at];
                string Next(string option)
                {
                    if (at + 1 >= words.Count)
                    {
                        throw new FormatException($"'{option}' needs a value after it.");
                    }

                    return words[++at];
                }

                // --name=value is --name value.
                string? inline = null;
                if (word.StartsWith("--", StringComparison.Ordinal) && word.IndexOf('=') is > 2 and var equals)
                {
                    inline = word[(equals + 1)..];
                    word = word[..equals];
                }

                string Value(string option) => inline ?? Next(option);

                try
                {
                    switch (word)
                    {
                        case "-X":
                        case "--request":
                            method = Value(word).ToUpperInvariant();
                            break;
                        case "-H":
                        case "--header":
                            request.AddHeader(Value(word));
                            break;
                        case "-d":
                        case "--data":
                        case "--data-ascii":
                        case "--data-raw":
                        case "--data-binary":
                        case "--data-urlencode":
                            var datum = Value(word);
                            if (datum.StartsWith('@') && word != "--data-raw")
                            {
                                request.Notes.Add($"The body is read from the file '{datum[1..]}' by curl - paste the file's content into Body, or send a file from the pipeline folder.");
                            }
                            else
                            {
                                data.Add(word == "--data-urlencode" ? EncodeFormPair(datum) : datum);
                            }

                            break;
                        case "--json":
                            jsonBody = jsonBody is null ? Value(word) : jsonBody + Value(word);
                            break;
                        case "-F":
                        case "--form":
                            var field = Value(word);
                            var split = field.IndexOf('=');
                            if (split <= 0)
                            {
                                throw new FormatException($"'{field}' is no form field - write it as name=value or name=@file.");
                            }

                            forms.Add((field[..split], field[(split + 1)..]));
                            break;
                        case "-u":
                        case "--user":
                            request.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Value(word)));
                            break;
                        case "-A":
                        case "--user-agent":
                            request.AddHeader("User-Agent: " + Value(word));
                            break;
                        case "-e":
                        case "--referer":
                            request.AddHeader("Referer: " + Value(word));
                            break;
                        case "-b":
                        case "--cookie":
                            var cookie = Value(word);
                            if (cookie.Contains('='))
                            {
                                request.AddHeader("Cookie: " + cookie);
                            }
                            else
                            {
                                request.Notes.Add($"curl reads cookies from the file '{cookie}' - add a 'Cookie: …' header instead.");
                            }

                            break;
                        case "-m":
                        case "--max-time":
                            if (double.TryParse(Value(word), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                            {
                                request.TimeoutSeconds = (int)Math.Ceiling(seconds);
                            }

                            break;
                        case "--url":
                            request.Url = Value(word);
                            break;
                        case "-G":
                        case "--get":
                            asQuery = true;
                            break;
                        case "-I":
                        case "--head":
                            head = true;
                            break;
                        case "-k":
                        case "--insecure":
                            request.Notes.Add("curl was told not to check the server's certificate (-k); the step always checks it.");
                            break;
                        case "-o":
                        case "--output":
                            request.Notes.Add($"curl writes the response to '{Value(word)}' - set 'Save the response to' to a file in the pipeline folder to do the same.");
                            break;
                        case "--compressed":
                        case "-L":
                        case "--location":
                        case "-s":
                        case "--silent":
                        case "-S":
                        case "--show-error":
                        case "-v":
                        case "--verbose":
                        case "-i":
                        case "--include":
                        case "-f":
                        case "--fail":
                        case "-#":
                        case "--progress-bar":
                            break; // how curl shows or follows, which the step already does its way
                        default:
                            if (word.StartsWith('-') && word.Length > 1)
                            {
                                request.Notes.Add($"'{word}' is not read - set what it does in the step by hand.");
                                // An option the reader does not know may take a value; a value that is no URL is skipped with it.
                                if (at + 1 < words.Count && !words[at + 1].StartsWith('-') && !LooksLikeUrl(words[at + 1]))
                                {
                                    at++;
                                }
                            }
                            else if (request.Url is null)
                            {
                                request.Url = word;
                            }

                            break;
                    }
                }
                catch (FormatException ex)
                {
                    error = ex.Message;
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(request.Url))
            {
                error = "The command names no URL.";
                return null;
            }

            if (!request.Url.Contains("://", StringComparison.Ordinal))
            {
                request.Url = "https://" + request.Url; // as curl does with a bare host, but on https
            }

            if (jsonBody is not null)
            {
                request.Body = jsonBody;
                request.ContentType ??= "application/json";
                if (!request.Headers.Any(header => header.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase)))
                {
                    request.AddHeader("Accept: application/json");
                }
            }

            if (data.Count > 0)
            {
                var joined = string.Join("&", data);
                if (asQuery)
                {
                    request.Url += (request.Url.Contains('?') ? "&" : "?") + joined;
                }
                else
                {
                    request.Body = request.Body is null ? joined : request.Body + "&" + joined;
                    request.ContentType ??= "application/x-www-form-urlencoded"; // curl's own for -d
                }
            }

            var files = forms.Where(form => form.Value.StartsWith('@')).ToList();
            if (files.Count > 0)
            {
                var (name, value) = files[0];
                request.FileField = name;
                request.SendFile = value[1..].Split(';')[0];
                request.Notes.Add($"'{request.SendFile}' is sent from the site's pipeline folder - put the file there.");
                if (files.Count > 1 || forms.Count > 1)
                {
                    request.Notes.Add("The step sends one file in a form; the form's other fields are not sent.");
                }
            }
            else if (forms.Count > 0)
            {
                request.Notes.Add("A form without a file is sent as a URL-encoded body.");
                request.Body = string.Join("&", forms.Select(form => Uri.EscapeDataString(form.Name) + "=" + Uri.EscapeDataString(form.Value)));
                request.ContentType ??= "application/x-www-form-urlencoded";
            }

            request.Method = head ? "HEAD"
                : method ?? (asQuery ? "GET" : request.Body is not null || request.SendFile is not null ? "POST" : "GET");
            return request;
        }

        private static bool LooksLikeUrl(string word) => word.Contains("://", StringComparison.Ordinal);

        // --data-urlencode name=value encodes the value; a bare value is encoded whole.
        private static string EncodeFormPair(string datum)
        {
            var equals = datum.IndexOf('=');
            return equals < 0 ? Uri.EscapeDataString(datum) : datum[..equals] + "=" + Uri.EscapeDataString(datum[(equals + 1)..]);
        }

        // A shell's words: whitespace splits, 'single' and "double" quotes keep, a backslash escapes (and before a line end
        // continues the line, as does a caret, Windows' cmd); $'…' as single quotes with \n and the like.
        private static List<string>? Words(string command, out string? error)
        {
            error = null;
            var words = new List<string>();
            var word = new StringBuilder();
            var inWord = false;
            for (var at = 0; at < command.Length; at++)
            {
                var c = command[at];
                if ((c == '\\' || c == '^' || c == '`') && at + 1 < command.Length && (command[at + 1] == '\n' || command[at + 1] == '\r'))
                {
                    at++;
                    if (command[at] == '\r' && at + 1 < command.Length && command[at + 1] == '\n')
                    {
                        at++;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }

                    continue;
                }

                inWord = true;
                if (c == '\'' || c == '$' && at + 1 < command.Length && command[at + 1] == '\'')
                {
                    var ansi = c == '$';
                    at += ansi ? 2 : 1;
                    var end = command.IndexOf('\'', at);
                    if (end < 0)
                    {
                        error = "A quote is not closed.";
                        return null;
                    }

                    var quoted = command[at..end];
                    word.Append(ansi ? quoted.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\'", "'").Replace("\\\\", "\\") : quoted);
                    at = end;
                }
                else if (c == '"')
                {
                    at++;
                    var closed = false;
                    for (; at < command.Length; at++)
                    {
                        if (command[at] == '\\' && at + 1 < command.Length && command[at + 1] is '"' or '\\' or '$' or '`')
                        {
                            word.Append(command[++at]);
                        }
                        else if (command[at] == '"')
                        {
                            closed = true;
                            break;
                        }
                        else
                        {
                            word.Append(command[at]);
                        }
                    }

                    if (!closed)
                    {
                        error = "A quote is not closed.";
                        return null;
                    }
                }
                else if (c == '\\' && at + 1 < command.Length)
                {
                    word.Append(command[++at]);
                }
                else
                {
                    word.Append(c);
                }
            }

            if (inWord)
            {
                words.Add(word.ToString());
            }

            return words;
        }
    }

    /// <summary>What a curl command sends, as the HTTP request step's inputs hold it.</summary>
    public sealed class PipelineCurlRequest
    {
        public string? Url { get; set; }

        public string Method { get; set; } = "GET";

        /// <summary>Headers other than Content-Type and Authorization, each 'Name: value'.</summary>
        public List<string> Headers { get; } = new List<string>();

        public string? Body { get; set; }

        public string? ContentType { get; set; }

        public string? Authorization { get; set; }

        public int? TimeoutSeconds { get; set; }

        public string? SendFile { get; set; }

        public string? FileField { get; set; }

        /// <summary>What curl would do that the step does not do the same way - said, not dropped.</summary>
        public List<string> Notes { get; } = new List<string>();

        public void AddHeader(string? header)
        {
            var colon = header?.IndexOf(':') ?? -1;
            if (colon <= 0)
            {
                Notes.Add($"'{header}' is no header - write it as 'Name: value'.");
                return;
            }

            var name = header![..colon].Trim();
            var value = header[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                ContentType = value;
            }
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                Authorization = value;
            }
            else
            {
                Headers.Add(name + ": " + value);
            }
        }
    }
}
