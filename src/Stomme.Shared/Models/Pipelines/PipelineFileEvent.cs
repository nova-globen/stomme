#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What a "File added" trigger hands the pipeline it starts (n8n's Local File Trigger): the file put in the watched
    /// folder, as JSON text, read with an expression - <c>{{ file.path }}</c> for a file step, <c>{{ file.name }}</c>. Its
    /// <c>event</c> is n8n's own word for what happened, <c>add</c> or <c>change</c>, and <c>path</c> is n8n's field too. ONE
    /// writer, used by the server when a file is seen and by the editor for the sample an author tests the next steps with, so
    /// the fields tested against are the fields a run gets - and the one rule for the trigger's folder and its names to match.
    /// </summary>
    public static class PipelineFileEvent
    {
        /// <summary>The trigger's step type.</summary>
        public const string StepType = "Stomme.FileAdded";

        /// <summary>A file the folder did not hold before (n8n's <c>add</c>).</summary>
        public const string Added = "add";

        /// <summary>A file written again - its size or its time not what they were (n8n's <c>change</c>).</summary>
        public const string Changed = "change";

        /// <summary>The most patterns one trigger matches names against.</summary>
        public const int MaxPatterns = 20;

        public static string Write(string path, long size, DateTimeOffset modifiedAt, string change)
        {
            var normalized = PipelineFilePaths.Normalize(path);
            return new JsonObject
            {
                ["path"] = normalized,
                ["name"] = PipelineFilePaths.Name(normalized),
                ["folder"] = PipelineFilePaths.Parent(normalized),
                ["size"] = size,
                ["modifiedAt"] = modifiedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["event"] = change,
            }.ToJsonString(Readable);
        }

        // A name says 'a&b <1>.pdf' as it is: the text is JSON to read, not markup.
        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>A file to test with in the editor, before any is put there: one the trigger would take, in its folder.</summary>
        public static string Sample(string? folder, string? pattern, DateTimeOffset now)
        {
            var first = Patterns(pattern).FirstOrDefault();
            var name = first is null ? "invoice-1001.pdf" : first.Replace("*", "invoice-1001", StringComparison.Ordinal).Replace('?', '1');
            return Write(PipelineFilePaths.Combine(folder ?? "", name), 48_213, now, Added);
        }

        /// <summary>Why a folder cannot be the one a trigger watches - null when it can (empty: the whole pipeline folder).</summary>
        public static string? FolderProblem(string? folder)
        {
            foreach (var segment in PipelineFilePaths.Normalize(folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (PipelineFilePaths.NameProblem(segment) is { } problem)
                {
                    return $"'{folder}' cannot be a folder of the site's pipeline folder: {problem}";
                }
            }

            return null;
        }

        /// <summary>The names to match, as written: split at ',' and ';', each trimmed, the same one once (case aside).</summary>
        public static IReadOnlyList<string> Patterns(string? pattern) =>
            (pattern ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The patterns as a trigger is indexed by them: one way of writing each list, so the same list is the same trigger.</summary>
        public static string NormalizePattern(string? pattern) => string.Join(", ", Patterns(pattern));

        /// <summary>
        /// Why the patterns cannot be matched - null when they can: each a file's name with <c>*</c> for any characters and <c>?</c>
        /// for one, never a path, at most <see cref="MaxPatterns"/>.
        /// </summary>
        public static string? PatternProblem(string? pattern)
        {
            var patterns = Patterns(pattern);
            if (patterns.Count > MaxPatterns)
            {
                return $"At most {MaxPatterns} patterns - this has {patterns.Count}.";
            }

            foreach (var glob in patterns)
            {
                // A wildcard stands for characters a name may hold, so what is left is held to the rule for a name.
                if (PipelineFilePaths.NameProblem(glob.Replace('*', 'x').Replace('?', 'x')) is { } problem)
                {
                    return $"'{glob}' is not a pattern of file names - a name with * for any characters and ? for one, such as *.pdf: {problem}";
                }
            }

            return null;
        }

        /// <summary>Whether a file's name matches one of the patterns - case aside - or there are none.</summary>
        public static bool Matches(string name, IReadOnlyList<string> patterns) => patterns.Count == 0 || patterns.Any(glob => Glob(glob, name));

        // '*' any run of characters, '?' one; the rest as it is, case aside. Each '*' backtracked to once at most: linear in practice.
        private static bool Glob(string pattern, string name)
        {
            int p = 0, n = 0, star = -1, resume = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
                {
                    p++;
                    n++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    resume = n;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    n = ++resume;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }
    }
}
