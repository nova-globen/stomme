#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.Connectors.Xml
{
    /// <summary>The request to run the external XML processor once for a single input file.</summary>
    internal sealed record XmlProcessRequest(string ExecutablePath, string Arguments, string WorkingDirectory);

    /// <summary>The outcome of one external-processor run: its exit code and captured stdout/stderr.</summary>
    internal sealed record XmlProcessResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>The testability seam over the external processor: the real one shells out, a test substitutes a fake.</summary>
    internal interface IXmlProcessRunner
    {
        Task<XmlProcessResult> RunAsync(XmlProcessRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runs the external XML processor as a process on the HOST. This is the one place in the Connectors family that
    /// starts a process, and the connector that owns it is <c>HostOnly</c>: creating or editing a connection of its type
    /// requires the Host role, which is what lets this file be allowlisted in the family's egress source gate.
    /// </summary>
    internal sealed class SystemXmlProcessRunner : IXmlProcessRunner
    {
        public async Task<XmlProcessResult> RunAsync(XmlProcessRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = request.ExecutablePath,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new XmlProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}
