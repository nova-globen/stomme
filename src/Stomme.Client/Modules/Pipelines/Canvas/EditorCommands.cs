using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// n8n's command bar: the editor's own actions found by name in the same search a step is found with (Ctrl+K) -
    /// "save", "tidy", "versions", "export" - each with its keys, so the search teaches them. Which of them the person may run
    /// here the editor decides; this only says which a query names.
    /// </summary>
    public static class EditorCommands
    {
        /// <summary>At most this many actions are offered for one query, after the steps and the connections.</summary>
        public const int MaxShown = 6;

        /// <summary>An action: what it is called, its keys (empty when it has none), and other words it answers to.</summary>
        public sealed record Command(string Id, string Label, string Keys, params string[] Words);

        public static readonly IReadOnlyList<Command> All = new[]
        {
            new Command("save", "Save", "Ctrl+S", "keep", "store"),
            new Command("publish", "Save and publish", "", "release", "live"),
            new Command("test", "Test the pipeline", "", "try", "draft", "run"),
            new Command("run", "Run the published version", "Ctrl+Enter", "execute", "start"),
            new Command("undo", "Undo", "Ctrl+Z", "back"),
            new Command("redo", "Redo", "Ctrl+Y"),
            new Command("tidy", "Tidy up", "Shift+Alt+T", "layout", "arrange", "align"),
            new Command("fit", "Fit the pipeline to the canvas", "1", "zoom", "view", "all"),
            new Command("zoomReset", "Zoom to 100 %", "0", "actual", "size"),
            new Command("note", "Add a sticky note", "Shift+S", "comment", "annotate"),
            new Command("selectAll", "Select every step", "Ctrl+A", "all"),
            new Command("find", "Find a step on the canvas", "Ctrl+F", "search", "go", "locate"),
            new Command("export", "Download the pipeline file", "", "export", "json", "file"),
            new Command("versions", "Show the versions", "", "history", "compare", "restore"),
            new Command("insights", "Show how each step has done", "", "insights", "statistics", "failures", "figures"),
            new Command("ai", "Build with AI", "", "assistant", "generate", "ask"),
            new Command("shortcuts", "Show the keyboard shortcuts", "?", "keys", "help"),
        };

        /// <summary>
        /// The actions a query names: every word of it begins a word of the action's name or of the words it answers to (in any
        /// case) - "pub" finds Save and publish, "zoom fit" Fit the pipeline to the canvas. Nothing for a query under two letters,
        /// which is a step being looked for.
        /// </summary>
        public static List<Command> Match(string query)
        {
            var asked = (query ?? "").Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (asked.Length == 0 || asked.Sum(word => word.Length) < 2)
            {
                return new List<Command>();
            }

            return All
                .Where(command =>
                {
                    var words = command.Label.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Concat(command.Words).ToList();
                    return asked.All(word => words.Any(known => known.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
                })
                .Take(MaxShown)
                .ToList();
        }
    }
}
