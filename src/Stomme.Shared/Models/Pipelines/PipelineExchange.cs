#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A pipeline as a FILE (BL-229 increment 10): its name, its description and the editor's graph - steps, connections,
    /// variables, notes - in a JSON document another site (or this one) can import. The graph is the editor's own contract, so a
    /// file carries exactly what the canvas shows and nothing engine-specific; the importing site's save judges it like any
    /// other graph. Credentials never travel: a step reaches a secret through one of the site's connections by name.
    /// </summary>
    public class PipelineExchangeDocument
    {
        public string Format { get; set; } = PipelineExchange.FormatName;

        public int FormatVersion { get; set; } = PipelineExchange.CurrentVersion;

        public string? Name { get; set; }

        public string? Description { get; set; }

        public PipelineGraph? Graph { get; set; }

        /// <summary>
        /// BL-229, in a file of many: the pipeline's definition id where it was exported - what the others in the file call it by,
        /// so their calls are pointed at the pipeline it becomes (<see cref="PipelineExchange.Repoint"/>). Nothing for one file.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>BL-229, in a file of many: its folder below the folder exported - placed below the folder it is imported into.</summary>
        public string? Folder { get; set; }

        /// <summary>BL-229: where a file came from when it was not this site's own format - <c>n8n</c> - and nothing when it was.</summary>
        [JsonIgnore]
        public string? ConvertedFrom { get; set; }
    }

    /// <summary>A file that is not a pipeline this site can read, with the reason in words.</summary>
    public sealed class PipelineExchangeException(string message) : Exception(message);

    public static class PipelineExchange
    {
        public const string FormatName = "stomme-pipeline";

        public const int CurrentVersion = 1;

        /// <summary>The largest file an import reads.</summary>
        public const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>The most steps a file may hold (bodies included).</summary>
        public const int MaxSteps = 1000;

        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>The file's text.</summary>
        public static string Write(string? name, string? description, PipelineGraph graph) =>
            JsonSerializer.Serialize(new PipelineExchangeDocument { Name = name, Description = description, Graph = Shared(graph) }, Options);

        /// <summary>
        /// BL-229 - many pipelines as ONE file (a folder exported): a list of documents, each as <see cref="Write"/> writes one, with
        /// its id - so calls among them are pointed at the pipelines they become - and its folder below the one exported.
        /// <see cref="ReadMany"/> reads it back, as it reads n8n's export of many workflows.
        /// </summary>
        public static string WriteMany(IEnumerable<PipelineExchangeDocument> documents)
        {
            var list = (documents ?? Enumerable.Empty<PipelineExchangeDocument>()).Where(document => document?.Graph is not null).Select(document => new PipelineExchangeDocument
            {
                Name = document.Name,
                Description = document.Description,
                Id = string.IsNullOrWhiteSpace(document.Id) ? null : document.Id,
                Folder = string.IsNullOrWhiteSpace(document.Folder) ? null : document.Folder,
                Graph = Shared(document.Graph!),
            }).ToList();
            return JsonSerializer.Serialize(list, Options);
        }

        /// <summary>
        /// BL-229 - the pipelines a graph names by definition id: the one each call step runs (Execute, Dispatch, Bulk dispatch
        /// pipeline), the one a "Pipeline failed" watches, and the ones it lets call it. An expression names what the run decides.
        /// </summary>
        public static IReadOnlyList<string> References(PipelineGraph? graph)
        {
            var named = new List<string>();
            foreach (var step in graph?.Steps ?? new List<PipelineGraphStep>())
            {
                if (step is not null && ReferenceInput(step.Type) is { } input && step.Inputs.TryGetValue(input, out var id) && !string.IsNullOrWhiteSpace(id))
                {
                    named.Add(id);
                }
            }

            named.AddRange(PipelineCallers.Ids(graph?.CalledBy));
            return named.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// BL-229 - the pipelines a graph names, pointed at the ones they became (<paramref name="becameOf"/>, old definition id to
        /// new): a file of many imported, the calls among them run the copies, not the pipelines they were exported from. A
        /// pipeline the file did not hold is left as named. How many references changed.
        /// </summary>
        public static int Repoint(PipelineGraph? graph, IReadOnlyDictionary<string, string> becameOf)
        {
            if (graph is null || becameOf is null || becameOf.Count == 0)
            {
                return 0;
            }

            var changed = 0;
            foreach (var step in graph.Steps ?? new List<PipelineGraphStep>())
            {
                if (step is not null && ReferenceInput(step.Type) is { } input && step.Inputs.TryGetValue(input, out var id) && id is not null && becameOf.TryGetValue(id, out var now))
                {
                    step.Inputs[input] = now;
                    changed++;
                }
            }

            var callers = PipelineCallers.Ids(graph.CalledBy);
            if (callers.Any(becameOf.ContainsKey))
            {
                changed += callers.Count(becameOf.ContainsKey);
                graph.CalledBy = PipelineCallers.Only(callers.Select(id => becameOf.TryGetValue(id, out var now) ? now : id));
            }

            return changed;
        }

        // The input naming another pipeline by definition id, for a step type that names one.
        private static string? ReferenceInput(string? type) => type switch
        {
            "Stomme.ExecutePipeline" or "Stomme.DispatchPipeline" or "Stomme.BulkDispatchPipelines" => PipelineCallContracts.CalledInput,
            PipelineFailure.StepType => "Pipeline",
            _ => null,
        };

        // BL-229: a pinned test value can be real data, and a file travels - so it stays behind (the caller's graph untouched).
        private static PipelineGraph Shared(PipelineGraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);
            return new PipelineGraph
            {
                // BL-229: nothing pinned leaves in a file - a pinned result can be real data, as a test value can.
                Steps = (graph.Steps ?? new List<PipelineGraphStep>()).Select(step => step?.PinnedResult is null ? step : step.WithoutPinnedResult()).ToList(),
                Connections = graph.Connections,
                Notes = graph.Notes,
                Variables = (graph.Variables ?? new List<PipelineGraphVariable>()).Where(variable => variable is not null)
                    .Select(variable => new PipelineGraphVariable { Id = variable.Id, Name = variable.Name, TypeName = variable.TypeName, Value = variable.Value })
                    .ToList(),
                RunsAtOnce = graph.RunsAtOnce, // BL-229: how many runs may go at once travels with the pipeline
                KeepSuccessfulRuns = graph.KeepSuccessfulRuns, // and whether it keeps the runs that finish well
                SaveProgress = graph.SaveProgress == true ? true : null, // and whether it stores a run after each step
                KeepStepData = PipelineStepDataKeeping.Stored(graph.KeepStepData), // and what its runs keep of their steps
                MinutesSavedPerRun = PipelineInsights.MinutesSaved(graph.MinutesSavedPerRun), // and the time a run saves
                TimeoutMinutes = PipelineRunTimeout.Minutes(graph.TimeoutMinutes), // and how long a run may be busy
                // And which pipelines may call it. The ones it names are this site's: on another they admit nobody until the
                // author names that site's - closed, never open to every caller - unless they travel in the same file, and are
                // pointed at the pipelines they become (Repoint).
                CalledBy = PipelineCallers.Normalize(graph.CalledBy),
                TimeZone = PipelineTimeZone.Normalize(graph.TimeZone), // and the zone it reads time in
            };
        }

        /// <summary>A file name for the pipeline: its name in letters, digits and dashes, then <c>.pipeline.json</c>.</summary>
        public static string FileName(string? name)
        {
            var text = new StringBuilder();
            foreach (var letter in (name ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsAsciiLetterOrDigit(letter))
                {
                    text.Append(letter);
                }
                else if (text.Length > 0 && text[^1] != '-')
                {
                    text.Append('-');
                }
            }

            var slug = text.ToString().Trim('-');
            return (slug.Length == 0 ? "pipeline" : slug.Length > 80 ? slug[..80].TrimEnd('-') : slug) + ".pipeline.json";
        }

        /// <summary>The most workflows one file of n8n's may bring over at once (<see cref="ReadMany"/>).</summary>
        public const int MaxWorkflows = 200;

        /// <summary>The largest file of n8n's many workflows read at once.</summary>
        public const int MaxManyBytes = 16 * 1024 * 1024;

        /// <summary>
        /// BL-229 - the documents a file holds: the one <see cref="Read"/> reads; or, from n8n's own export of many workflows - a JSON
        /// list, as <c>n8n export:workflow --all</c> writes it - each of them, read and judged as one file is (each up to
        /// <see cref="MaxBytes"/>, at most <see cref="MaxWorkflows"/>), so a team leaving n8n brings its workflows over at once. One
        /// that is not a pipeline refuses the file, saying which.
        /// </summary>
        public static IReadOnlyList<PipelineExchangeDocument> ReadMany(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('['))
            {
                return new[] { Read(json) };
            }

            if (Encoding.UTF8.GetByteCount(json) > MaxManyBytes)
            {
                throw new PipelineExchangeException($"The file is larger than {MaxManyBytes / (1024 * 1024)} MB.");
            }

            List<string> items;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                items = parsed.RootElement.EnumerateArray().Select(item => item.GetRawText()).ToList();
            }
            catch (JsonException ex)
            {
                throw new PipelineExchangeException("The file is not a pipeline's JSON: " + ex.Message);
            }

            if (items.Count == 0)
            {
                throw new PipelineExchangeException("The file holds no pipeline.");
            }

            if (items.Count > MaxWorkflows)
            {
                throw new PipelineExchangeException($"The file holds {items.Count} workflows; at most {MaxWorkflows} come over at once.");
            }

            var documents = new List<PipelineExchangeDocument>();
            for (var index = 0; index < items.Count; index++)
            {
                try
                {
                    documents.Add(Read(items[index]));
                }
                catch (PipelineExchangeException ex)
                {
                    throw new PipelineExchangeException($"Its workflow {index + 1}: {ex.Message}");
                }
            }

            return documents;
        }

        /// <summary>
        /// The document a file holds - an n8n workflow export brought over (<see cref="N8nWorkflowImport"/>) - its lists never null,
        /// or <see cref="PipelineExchangeException"/> saying why it is not one:
        /// not JSON, another format, a newer format version, no graph, too many steps, a step without an id or a type, an id twice,
        /// a connection or a body naming a step the file does not hold. What the save judges (a step type this site lacks, an input
        /// that will not convert) is left to the save, which says it on the canvas.
        /// </summary>
        public static PipelineExchangeDocument Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new PipelineExchangeException("The file is empty.");
            }

            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            {
                throw new PipelineExchangeException($"The file is larger than {MaxBytes / (1024 * 1024)} MB.");
            }

            PipelineExchangeDocument? document;
            try
            {
                // BL-229: an n8n workflow export is brought over as this site's pipeline (N8nWorkflowImport), then judged as any file is.
                using var parsed = JsonDocument.Parse(json);
                document = N8nWorkflowImport.IsWorkflow(parsed.RootElement)
                    ? N8nWorkflowImport.Convert(parsed.RootElement)
                    : JsonSerializer.Deserialize<PipelineExchangeDocument>(json, Options);
            }
            catch (JsonException ex)
            {
                throw new PipelineExchangeException("The file is not a pipeline's JSON: " + ex.Message);
            }

            if (document is null || !string.Equals(document.Format, FormatName, StringComparison.Ordinal))
            {
                throw new PipelineExchangeException($"The file is not a Nova Globen Stomme pipeline (its format is '{document?.Format}', not '{FormatName}').");
            }

            if (document.FormatVersion > CurrentVersion)
            {
                throw new PipelineExchangeException($"The file was written by a newer Nova Globen Stomme (format version {document.FormatVersion}); this site reads up to version {CurrentVersion}.");
            }

            var graph = document.Graph ?? throw new PipelineExchangeException("The file holds no pipeline.");
            graph.Steps = (graph.Steps ?? new List<PipelineGraphStep>()).Where(step => step is not null).ToList();
            graph.Connections = (graph.Connections ?? new List<PipelineGraphConnection>()).Where(connection => connection is not null).ToList();
            graph.Variables = (graph.Variables ?? new List<PipelineGraphVariable>()).Where(variable => variable is not null).ToList();
            graph.Notes = (graph.Notes ?? new List<PipelineGraphNote>()).Where(note => note is not null).ToList();
            foreach (var variable in graph.Variables)
            {
                variable.TestValue = null; // BL-229: a file carries no pinned test values, whatever wrote it
            }

            foreach (var step in graph.Steps)
            {
                step.PinnedResult = null; // nor a pinned result
            }

            if (graph.Steps.Count > MaxSteps)
            {
                throw new PipelineExchangeException($"The file holds {graph.Steps.Count} steps; a pipeline holds at most {MaxSteps}.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < graph.Steps.Count; index++)
            {
                var step = graph.Steps[index];
                if (string.IsNullOrWhiteSpace(step.Id) || string.IsNullOrWhiteSpace(step.Type))
                {
                    throw new PipelineExchangeException($"Step {index + 1} in the file has no {(string.IsNullOrWhiteSpace(step.Id) ? "id" : "type")}.");
                }

                if (!ids.Add(step.Id))
                {
                    throw new PipelineExchangeException($"Two steps in the file have the id '{step.Id}'.");
                }

                step.Position ??= new GraphPosition();
                step.Inputs ??= new Dictionary<string, string>();
                step.InputVariables ??= new Dictionary<string, string>();
                step.InputExpressions ??= new Dictionary<string, string>();
                step.InputScripts ??= new Dictionary<string, string>();
                step.InputConditions ??= new Dictionary<string, PipelineCondition>();
                step.Cases = PipelineOutcomes.Copy(step.Cases);
                step.OutputVariables ??= new Dictionary<string, string>();
            }

            foreach (var step in graph.Steps.Where(step => step.Parent is not null && !ids.Contains(step.Parent)))
            {
                throw new PipelineExchangeException($"Step '{step.Id}' sits inside '{step.Parent}', which the file does not hold.");
            }

            foreach (var connection in graph.Connections.Where(connection => !ids.Contains(connection.SourceId ?? "") || !ids.Contains(connection.TargetId ?? "")))
            {
                throw new PipelineExchangeException($"A connection from '{connection.SourceId}' to '{connection.TargetId}' names a step the file does not hold.");
            }

            var variables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in graph.Variables)
            {
                if (string.IsNullOrWhiteSpace(variable.Id) || !variables.Add(variable.Id))
                {
                    throw new PipelineExchangeException(string.IsNullOrWhiteSpace(variable.Id) ? "A variable in the file has no id." : $"Two variables in the file have the id '{variable.Id}'.");
                }
            }

            foreach (var note in graph.Notes)
            {
                note.Id = string.IsNullOrWhiteSpace(note.Id) ? "note-" + Guid.NewGuid().ToString("N")[..8] : note.Id;
                note.Position ??= new GraphPosition();
                note.Color = PipelineNoteColors.Normalize(note.Color); // a colour the editor does not offer is the accent
                if (note.Parent is not null && !ids.Contains(note.Parent))
                {
                    note.Parent = null;
                    note.ParentPort = null;
                }
            }

            // BL-229: a folder below the one imported into, as the folder rule allows - one that cannot be one is none.
            var folder = PipelineFolders.Normalize(document.Folder, out var folderError);
            document.Folder = folderError is null && folder.Length > 0 ? folder : null;
            document.Id = string.IsNullOrWhiteSpace(document.Id) ? null : document.Id.Trim();
            return document;
        }

        /// <summary>
        /// BL-229 - where a pipeline of a file of many goes: its folder (<see cref="PipelineExchangeDocument.Folder"/>) below the
        /// folder it is imported into - or that folder itself when the two together would be deeper than a folder may be.
        /// </summary>
        public static string FolderIn(string? into, string? below)
        {
            if (string.IsNullOrEmpty(below))
            {
                return into ?? "";
            }

            var joined = PipelineFolders.Normalize(string.IsNullOrEmpty(into) ? below : into + PipelineFolders.Separator + below, out var error);
            return error is null ? joined : into ?? "";
        }
    }
}
