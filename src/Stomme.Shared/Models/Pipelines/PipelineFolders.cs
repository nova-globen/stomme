#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A pipeline's FOLDER (n8n's folders): where it sits on the pipelines list - "Finance", "Finance/Invoices". The one
    /// rule for both sides: names separated by '/', each tidy (trimmed, inner spaces folded to one) and at most
    /// <see cref="MaxLength"/> letters, digits, spaces, '-', '_' or '.', at most <see cref="MaxDepth"/> deep; empty is the top of
    /// the list. A folder is there while a pipeline is in it, or in a folder inside it - one move makes it, the last move out
    /// ends it. Two spellings of a folder that differ only in case are one folder, named as the first pipeline in it names it.
    /// </summary>
    public static class PipelineFolders
    {
        public const int MaxDepth = 4;

        public const int MaxLength = 40;

        public const char Separator = '/';

        /// <summary>The folder made tidy; one that cannot be a folder refused with the reason (and the top given).</summary>
        public static string Normalize(string? path, out string? error)
        {
            error = null;
            var names = (path ?? "").Split(Separator).Select(name => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Where(name => name.Length > 0).ToList();
            if (names.FirstOrDefault(name => name.Length > MaxLength || !name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) is { } bad)
            {
                error = $"'{bad}' cannot be a folder's name: at most {MaxLength} letters, digits, spaces, '-', '_' or '.'.";
                return "";
            }

            if (names.Count > MaxDepth)
            {
                error = $"A folder is at most {MaxDepth} deep.";
                return "";
            }

            return string.Join(Separator, names);
        }

        /// <summary>A folder's own name: the last of its path.</summary>
        public static string Name(string folder) => folder[(folder.LastIndexOf(Separator) + 1)..];

        /// <summary>The folders above it and itself, from the top: "A", "A/B", "A/B/C".</summary>
        public static List<string> Path(string? folder)
        {
            var names = (folder ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            return Enumerable.Range(1, names.Length).Select(count => string.Join(Separator, names.Take(count))).ToList();
        }

        /// <summary>Whether a pipeline in <paramref name="folder"/> stands right in <paramref name="current"/> (the top when empty).</summary>
        public static bool IsIn(string? folder, string? current) => string.Equals(folder ?? "", current ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="folder"/> is <paramref name="current"/> or inside it.</summary>
        public static bool IsWithin(string? folder, string? current) =>
            string.IsNullOrEmpty(current) || IsIn(folder, current) || (folder ?? "").StartsWith(current + Separator, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The folders right inside <paramref name="current"/> (the top when empty), from the folders the pipelines are in: each
        /// one's path, its name, and how many pipelines are in it or in a folder inside it - by name.
        /// </summary>
        public static List<(string Path, string Name, int Count)> Children(IEnumerable<string?> folders, string? current)
        {
            var depth = string.IsNullOrEmpty(current) ? 0 : current.Split(Separator).Length;
            return (folders ?? Enumerable.Empty<string?>())
                .Where(folder => !string.IsNullOrEmpty(folder) && !IsIn(folder, current) && IsWithin(folder, current))
                .Select(folder => (Child: string.Join(Separator, folder!.Split(Separator).Take(depth + 1)), Folder: folder))
                .GroupBy(pair => pair.Child, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Path: group.First().Child, Name: Name(group.First().Child), Count: group.Count()))
                .OrderBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The folder spelled as the list already spells it, level by level - "walks/WITH calls/New" is "Walks/With calls/New" when
        /// "Walks" and "Walks/With calls" are there - so a folder typed in another case is the same folder, never a second one.
        /// </summary>
        public static string Spelled(string folder, IEnumerable<string?> known)
        {
            var all = All(known);
            var spelled = "";
            foreach (var name in (folder ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = spelled.Length == 0 ? name : spelled + Separator + name;
                var there = all.FirstOrDefault(candidate => string.Equals(candidate, next, StringComparison.OrdinalIgnoreCase));
                spelled = (spelled.Length == 0 ? "" : spelled + Separator) + (there is null ? name : Name(there));
            }

            return spelled;
        }

        /// <summary>The folder above it; the top (empty) for a folder at the top.</summary>
        public static string Parent(string? folder)
        {
            var at = (folder ?? "").LastIndexOf(Separator);
            return at < 0 ? "" : folder![..at];
        }

        /// <summary>
        /// Where a pipeline in <paramref name="folder"/> goes when the folder <paramref name="from"/> becomes <paramref name="to"/>
        /// (the top when empty): what is below <paramref name="from"/> stays below it. A pipeline not in it stays where it is.
        /// </summary>
        public static string Moved(string? folder, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || !IsWithin(folder, from))
            {
                return folder ?? "";
            }

            var below = folder![from.Length..].TrimStart(Separator);
            return string.IsNullOrEmpty(to) ? below : below.Length == 0 ? to : to + Separator + below;
        }

        /// <summary>
        /// Why the folder <paramref name="from"/> cannot become <paramref name="to"/> (renamed, or moved: a path), given the folders
        /// the pipelines are in - or null. Not into itself; not onto a folder that is there already (two folders are never merged
        /// by a rename - a pipeline is moved into one); and nothing in it deeper than <see cref="MaxDepth"/>.
        /// </summary>
        public static string? MoveProblem(IEnumerable<string?> folders, string from, string to)
        {
            var all = All(folders);
            if (string.IsNullOrEmpty(from) || !all.Any(folder => IsIn(folder, from)))
            {
                return "There is no such folder.";
            }

            if (string.IsNullOrEmpty(to))
            {
                return "A folder needs a name.";
            }

            if (!IsIn(to, from) && IsWithin(to, from))
            {
                return "A folder cannot go inside itself.";
            }

            if (!IsIn(to, from) && all.FirstOrDefault(folder => IsIn(folder, to)) is { } there)
            {
                return $"There is a folder '{there}' already.";
            }

            if (all.Where(folder => IsWithin(folder, from)).Select(folder => Moved(folder, from, to)).FirstOrDefault(folder => folder.Split(Separator).Length > MaxDepth) is not null)
            {
                return $"A folder is at most {MaxDepth} deep - a folder inside it would be deeper.";
            }

            return null;
        }

        /// <summary>Every folder the pipelines are in, and every folder above one, once whatever its case - in order.</summary>
        public static List<string> All(IEnumerable<string?> folders) =>
            (folders ?? Enumerable.Empty<string?>()).Where(folder => !string.IsNullOrEmpty(folder)).SelectMany(Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(folder => folder, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }
}
