#nullable enable
using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What a "Pipeline failed" trigger hands the pipeline it starts (n8n's error workflow): the run that failed, as JSON
    /// text, read with an expression - <c>{{ failure.pipeline }}</c>, <c>{{ failure.error }}</c>. ONE writer, used by the server
    /// when a run fails and by the editor for the sample an author tests the next steps with, so the fields tested against are the
    /// fields a run gets.
    /// </summary>
    public static class PipelineFailure
    {
        /// <summary>The trigger's step type.</summary>
        public const string StepType = "Stomme.PipelineFailed";

        /// <summary>What a trigger watching no pipeline in particular is indexed under: every pipeline on the site.</summary>
        public const string AnyPipeline = "*";

        /// <summary>The pipeline a trigger watches, as it is indexed and as a failure is matched against it.</summary>
        public static string Watched(string? definitionId) => string.IsNullOrWhiteSpace(definitionId) ? AnyPipeline : definitionId.Trim();

        public static string Write(string pipelineId, string? pipelineName, string runId, string? stepId, string? stepName, string? stepType, string? error, DateTimeOffset failedAt) =>
            new JsonObject
            {
                ["pipeline"] = string.IsNullOrWhiteSpace(pipelineName) ? pipelineId : pipelineName,
                ["pipelineId"] = pipelineId,
                ["run"] = runId,
                ["step"] = string.IsNullOrWhiteSpace(stepName) ? stepId : stepName,
                ["stepId"] = stepId,
                ["stepType"] = stepType,
                ["error"] = error,
                ["failedAt"] = failedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            }.ToJsonString(Readable);

        // An error says 'C:\file' or <tag> as it is: the text is JSON to read, not markup, so nothing is escaped that need not be.
        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>A failure to test with in the editor, before any run has failed.</summary>
        public static string Sample(string? pipelineName, DateTimeOffset now) =>
            Write("sample", string.IsNullOrWhiteSpace(pipelineName) ? "Some pipeline" : pipelineName, "sample-run", "send", "Send the report",
                "Stomme.SendHttpRequest", "The request failed: 503 Service Unavailable.", now);
    }
}
