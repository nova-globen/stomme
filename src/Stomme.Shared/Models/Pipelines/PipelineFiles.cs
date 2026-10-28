using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - the site's pipeline folder, one folder of it at a time: what the file steps (Read and Write text file, the
    /// spreadsheet, ZIP and HTTP file steps) read and write, by paths relative to it. n8n keeps a run's files in its execution
    /// data; here they are files of the site's own folder, so the Files page lists them, takes a file to read and gives back
    /// one a run wrote.
    /// </summary>
    public sealed class PipelineFolderView
    {
        /// <summary>The folder listed, '/'-separated and relative to the site's folder; empty for the site's folder itself.</summary>
        public string Folder { get; set; } = "";

        /// <summary>Its folders first, then its files, each by name.</summary>
        public List<PipelineFileEntryView> Entries { get; set; } = new();

        /// <summary>The most bytes one file may hold - the file steps' own bound (<c>Pipelines:Files:MaxFileBytes</c>).</summary>
        public long MaxFileBytes { get; set; }
    }

    /// <summary>A folder or a file in the site's pipeline folder.</summary>
    public sealed class PipelineFileEntryView
    {
        public string Name { get; set; }

        /// <summary>Its path as a file step reads it: relative to the site's folder, '/'-separated.</summary>
        public string Path { get; set; }

        public bool IsFolder { get; set; }

        /// <summary>A file's size in bytes; 0 for a folder.</summary>
        public long Length { get; set; }

        public DateTime ModifiedOnUtc { get; set; }
    }

    /// <summary>A folder to make, by its path.</summary>
    public sealed class PipelineFileFolderRequest
    {
        public string Path { get; set; }
    }

    /// <summary>
    /// Paths in the site's pipeline folder as the page and the API speak them: relative, '/'-separated, no leading or trailing
    /// separator - and the one rule for a file's or a folder's NAME, read by the page as a hint and by the server as the answer.
    /// Whether a path stays inside the site's folder is the server's (the file steps' own sandbox), never this class's.
    /// </summary>
    public static class PipelineFilePaths
    {
        /// <summary>The longest name a file or a folder may have.</summary>
        public const int MaxNameLength = 255;

        private static readonly char[] Forbidden = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>A path as the page and the API speak it: '\' read as '/', no empty segments, no separator at either end.</summary>
        public static string Normalize(string path) =>
            string.Join("/", (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.Trim()).Where(segment => segment.Length > 0));

        /// <summary>A name in a folder; the folder itself when the name is empty.</summary>
        public static string Combine(string folder, string name)
        {
            var parent = Normalize(folder);
            var child = Normalize(name);
            return parent.Length == 0 ? child : child.Length == 0 ? parent : parent + "/" + child;
        }

        /// <summary>The folder a path is in; empty for one in the site's folder itself.</summary>
        public static string Parent(string path)
        {
            var normalized = Normalize(path);
            var last = normalized.LastIndexOf('/');
            return last < 0 ? "" : normalized.Substring(0, last);
        }

        /// <summary>A path's last segment.</summary>
        public static string Name(string path)
        {
            var normalized = Normalize(path);
            return normalized.Substring(normalized.LastIndexOf('/') + 1);
        }

        /// <summary>The folders from the site's folder down to <paramref name="folder"/>, each with its path - the page's trail.</summary>
        public static IReadOnlyList<(string Name, string Path)> Trail(string folder)
        {
            var trail = new List<(string Name, string Path)>();
            var path = "";
            foreach (var segment in Normalize(folder).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = Combine(path, segment);
                trail.Add((segment, path));
            }

            return trail;
        }

        /// <summary>
        /// Why a name cannot be a file's or a folder's - null when it can. Held to what every system the host may run on accepts,
        /// so a folder copied between hosts keeps its files: no separator or ':', none of Windows' forbidden or control
        /// characters, not '.' or '..', not ending in a dot or a space, not a device name, at most <see cref="MaxNameLength"/>.
        /// </summary>
        public static string NameProblem(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "A name is needed.";
            }

            if (name.Length > MaxNameLength)
            {
                return $"A name has at most {MaxNameLength} characters.";
            }

            if (name == "." || name == "..")
            {
                return $"'{name}' cannot be a name.";
            }

            if (name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl))
            {
                return $"'{name}' holds a character a file name cannot: < > : \" / \\ | ? * or a control character.";
            }

            if (name != name.Trim() || name.EndsWith('.'))
            {
                return $"'{name}' starts or ends with a space, or ends with a dot - a name cannot.";
            }

            var stem = name.Split('.')[0];
            if (Reserved.Contains(stem))
            {
                return $"'{name}' is a device name on Windows and cannot be a file's name.";
            }

            return null;
        }

        /// <summary>A size as a person reads it: bytes, KB, MB or GB.</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
            }

            var units = new[] { "KB", "MB", "GB", "TB" };
            var value = bytes / 1024d;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }
}
