#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>A pipeline on the map of the site's pipelines.</summary>
    public sealed class PipelineMapNode
    {
        public string DefinitionId { get; set; } = "";

        public string? Name { get; set; }

        public string Folder { get; set; } = "";

        public bool IsPublished { get; set; }

        /// <summary>It starts when ANY pipeline of the site fails (a "Pipeline failed" watching every one).</summary>
        public bool WatchesEvery { get; set; }

        /// <summary>An AI agent may run it as a tool (it starts with "AI agent tool").</summary>
        public bool IsTool { get; set; }
    }

    /// <summary>One pipeline starting another: <see cref="From"/> leads to <see cref="To"/> running, as <see cref="Kind"/> says.</summary>
    public sealed class PipelineMapLink
    {
        public string From { get; set; } = "";

        public string To { get; set; } = "";

        public string Kind { get; set; } = "";
    }

    /// <summary>The site's pipelines and how they start one another.</summary>
    public sealed class PipelineMapView
    {
        public List<PipelineMapNode> Nodes { get; set; } = new();

        public List<PipelineMapLink> Links { get; set; } = new();
    }

    /// <summary>
    /// The map of the site's pipelines (beyond n8n, where a workflow calling another is seen only inside it): which
    /// pipeline runs which - Execute pipeline CALLS (and waits), Dispatch and Bulk dispatch START, a failure STARTS a "Pipeline
    /// failed" watching it, and an AI agent may run a tool pipeline. Read from what each graph says as written - a pipeline
    /// named by an expression is the run's to know, and a step switched off starts nothing. A link to a pipeline the map does
    /// not hold is left out.
    /// </summary>
    public static class PipelineLinks
    {
        public const string Calls = "calls";

        public const string Starts = "starts";

        public const string OnFailure = "on failure";

        public const string AsTool = "as a tool";

        public const string AgentToolType = "Stomme.AgentTool";

        public const string AgentType = "Stomme.AiAgent";

        public static PipelineMapView Map(IEnumerable<(string DefinitionId, string? Name, string? Folder, bool IsPublished, PipelineGraph? Graph)> pipelines)
        {
            var all = (pipelines ?? Enumerable.Empty<(string, string?, string?, bool, PipelineGraph?)>()).Where(pipeline => !string.IsNullOrEmpty(pipeline.DefinitionId)).ToList();
            var map = new PipelineMapView
            {
                Nodes = all.Select(pipeline => new PipelineMapNode { DefinitionId = pipeline.DefinitionId, Name = pipeline.Name, Folder = pipeline.Folder ?? "", IsPublished = pipeline.IsPublished }).ToList(),
            };
            var nodes = map.Nodes.ToDictionary(node => node.DefinitionId, StringComparer.Ordinal);
            var links = new HashSet<(string, string, string)>();
            void Link(string from, string to, string kind)
            {
                if (from != to && nodes.ContainsKey(from) && nodes.ContainsKey(to))
                {
                    links.Add((from, to, kind));
                }
            }

            // The tools first: an agent names them by their tool name.
            var tools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, _, _, _, graph) in all)
            {
                foreach (var step in On(graph).Where(step => step.Type == AgentToolType))
                {
                    nodes[id].IsTool = true;
                    if (Literal(step, "ToolName") is { } name)
                    {
                        (tools.TryGetValue(name, out var named) ? named : tools[name] = new List<string>()).Add(id);
                    }
                }
            }

            foreach (var (id, _, _, _, graph) in all)
            {
                foreach (var step in On(graph))
                {
                    switch (step.Type)
                    {
                        case "Stomme.ExecutePipeline":
                            if (Literal(step, PipelineCallContracts.CalledInput) is { } called)
                            {
                                Link(id, called, Calls);
                            }

                            break;
                        case "Stomme.DispatchPipeline":
                        case "Stomme.BulkDispatchPipelines":
                            if (Literal(step, PipelineCallContracts.CalledInput) is { } started)
                            {
                                Link(id, started, Starts);
                            }

                            break;
                        case PipelineFailure.StepType:
                            if (step.InputExpressions.ContainsKey("Pipeline") || step.InputVariables.ContainsKey("Pipeline"))
                            {
                                break;
                            }

                            if (Literal(step, "Pipeline") is { } watched)
                            {
                                Link(watched, id, OnFailure);
                            }
                            else
                            {
                                nodes[id].WatchesEvery = true;
                            }

                            break;
                        case AgentType:
                            var named = (Literal(step, "Tools") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            var usable = named.Length == 0 ? tools.Values.SelectMany(ids => ids) : named.SelectMany(name => tools.TryGetValue(name, out var ids) ? ids : Enumerable.Empty<string>());
                            foreach (var tool in usable)
                            {
                                Link(id, tool, AsTool);
                            }

                            break;
                    }
                }
            }

            map.Links = links.Select(link => new PipelineMapLink { From = link.Item1, To = link.Item2, Kind = link.Item3 })
                .OrderBy(link => nodes[link.From].Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(link => nodes[link.To].Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return map;
        }

        /// <summary>
        /// What stops working when these pipelines stop running (archived or retracted - the engine runs a published
        /// version only): every link INTO one of them from a pipeline that stays - a call or a start that will fail, a tool an
        /// agent is no longer offered, a pipeline's failures no longer handled - and a handler of every failure leaving. Links
        /// among the ones leaving break nothing that stays. Each said in a sentence, in the map's order; none when nothing does.
        /// </summary>
        public static List<string> LeftBehind(PipelineMapView? map, IEnumerable<string> leaving)
        {
            var gone = new HashSet<string>(leaving ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (map is null || gone.Count == 0)
            {
                return new List<string>();
            }

            var names = map.Nodes.ToDictionary(node => node.DefinitionId, node => string.IsNullOrWhiteSpace(node.Name) ? node.DefinitionId : node.Name!, StringComparer.Ordinal);
            string Name(string id) => names.TryGetValue(id, out var name) ? name : id;
            var said = map.Links.Where(link => gone.Contains(link.To) && !gone.Contains(link.From)).Select(link => link.Kind switch
            {
                Calls => $"'{Name(link.From)}' calls '{Name(link.To)}' - that call will fail.",
                Starts => $"'{Name(link.From)}' starts '{Name(link.To)}' - that start will fail.",
                OnFailure => $"'{Name(link.To)}' handles the failures of '{Name(link.From)}' - they will go unhandled.",
                AsTool => $"'{Name(link.From)}' may use '{Name(link.To)}' as an AI tool - it will not be offered.",
                _ => $"'{Name(link.From)}' {link.Kind} '{Name(link.To)}'.",
            }).ToList();
            said.AddRange(map.Nodes.Where(node => node.WatchesEvery && gone.Contains(node.DefinitionId))
                .Select(node => $"'{Name(node.DefinitionId)}' handles the failures of every pipeline - not while it does not run."));
            return said;
        }

        // The steps that start something when the run reaches them: switched-off ones start nothing.
        private static IEnumerable<PipelineGraphStep> On(PipelineGraph? graph) =>
            (graph?.Steps ?? new List<PipelineGraphStep>()).Where(step => step is not null && !step.Disabled);

        // An input as written - not one an expression or a variable gives, which only the run knows.
        private static string? Literal(PipelineGraphStep step, string input) =>
            !step.InputExpressions.ContainsKey(input) && !step.InputVariables.ContainsKey(input) && !step.InputScripts.ContainsKey(input)
            && step.Inputs.TryGetValue(input, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}
