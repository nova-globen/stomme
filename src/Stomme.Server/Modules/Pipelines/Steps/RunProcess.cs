#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A process action as an engine step: launch a program with arguments, feed it the input text on standard
    /// input, capture standard output as the result. Refused unless <c>Pipelines:Processes:Enabled</c> AND the resolved
    /// executable lives inside one of <c>Pipelines:Processes:AllowedRoots</c> - there is no open-ended "run anything"
    /// path. The launch is wall-clock bounded (the tree is killed on overrun), output-capped, and works out of the
    /// site's pipeline folder. Arguments are never built from the input: the input goes to stdin only.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Scripting", "Run an approved program (Pipelines:Processes); its standard output is the result.", DisplayName = "Run process")]
    public class RunProcess : DataStep
    {
        [Input(Description = "The executable's full path; it must be under an allowed root.", Order = 1)]
        public Input<string> Executable { get; set; } = null!;

        [Input(Description = "The command-line arguments.", Order = 2)]
        public Input<string?>? Arguments { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Written to the program's standard input. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Input { get; set; }

        [Output(DisplayName = "Exit code")]
        public Output<int>? ExitCode { get; set; }

        [Output(DisplayName = "Standard error")]
        public Output<string>? StandardError { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var options = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Processes ?? new ProcessStepOptions();
            if (!options.Enabled)
            {
                await FailAsync(context, "PROCESSES_DISABLED", "Process launches are disabled (Pipelines:Processes:Enabled=false).").ConfigureAwait(false);
                return;
            }

            var configured = Required(context, Executable, "Executable");
            if (options.AllowedRoots.Count == 0)
            {
                await FailAsync(context, "PROCESS_ROOTS_EMPTY", "No root is allowed; list the directories approved programs live in under Pipelines:Processes:AllowedRoots.").ConfigureAwait(false);
                return;
            }

            var executable = Path.GetFullPath(configured);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var allowed = options.AllowedRoots.Any(root =>
                executable.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison));
            if (!allowed)
            {
                await FailAsync(context, "EXECUTABLE_OUTSIDE_ROOTS", $"'{configured}' is outside every allowed root.").ConfigureAwait(false);
                return;
            }

            if (!File.Exists(executable))
            {
                await FailAsync(context, "EXECUTABLE_NOT_FOUND", $"'{configured}' does not exist.").ConfigureAwait(false);
                return;
            }

            var workingDirectory = PipelineFileSandbox.SiteRoot(context);
            Directory.CreateDirectory(workingDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = Arguments?.GetOrDefault(context) ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    await FailAsync(context, "PROCESS_START_FAILED", $"'{configured}' did not start.").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                await FailAsync(context, "PROCESS_START_FAILED", ex.Message).ConfigureAwait(false);
                return;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(context.CancellationToken);
            try
            {
                await process.StandardInput.WriteAsync(TextOrLastResult(context, Input) ?? "").ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The program closed its input early; what it produces still counts.
            }

            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                await FailAsync(context, "PROCESS_TIMEOUT", $"'{configured}' exceeded {options.TimeoutSeconds} seconds and was terminated.").ConfigureAwait(false);
                return;
            }

            var output = Cap(await stdout.ConfigureAwait(false), options.MaxOutputChars);
            var error = Cap(await stderr.ConfigureAwait(false), options.MaxOutputChars);
            context.Set(ExitCode, process.ExitCode);
            context.Set(StandardError, error);
            if (process.ExitCode != 0)
            {
                context.Set(Result, output);
                await FailAsync(context, "PROCESS_ERROR", $"'{configured}' exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}").ConfigureAwait(false);
                return;
            }

            await SucceedAsync(context, output).ConfigureAwait(false);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }

        private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;
    }
}
