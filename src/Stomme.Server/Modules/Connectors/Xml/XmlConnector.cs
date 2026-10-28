#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Xml
{
    /// <summary>
    /// The XML file-transform connector (BL-96 increment 2, lifted from the predecessor platform's `xml`): drives an external processor
    /// (<c>TelecomBridge.exe</c> by default) that reads vendor telecom XML exports from a source folder and writes CSV to a
    /// destination folder; the <c>transform</c> operation streams one outcome record per processed input file.
    /// </summary>
    /// <remarks>
    /// <b>HostOnly, by contract (D110):</b> this connector starts a process on the host and reads and moves files in host
    /// folders, so creating or editing a connection of its type requires the Host role - the Connectivity module refuses
    /// an administrator (BL-94's HostOnly gate). The processor is a host-provided Windows binary absent from this
    /// environment, so the live run is host-gated; the file matching and argument templating run headlessly through the
    /// <see cref="IXmlProcessRunner"/> seam, and a test honestly reports the processor or the source folder as absent.
    /// </remarks>
    public sealed class XmlConnector : IConnector, IConnectorExtractor
    {
        private const int OutputExcerptLimit = 4000;

        private readonly IXmlProcessRunner processRunner;

        public XmlConnector()
            : this(null)
        {
        }

        internal XmlConnector(IXmlProcessRunner? processRunner) => this.processRunner = processRunner ?? new SystemXmlProcessRunner();

        public string ConnectorType => "xml";

        public string DisplayName => "XML";

        public AuthenticationType AuthType => AuthenticationType.None;

        public bool HostOnly => true;

        /// <summary>This connector has no network endpoint: the settings carry the processor and the folders. The endpoint is a label (the source folder, say) validated as a credential-free string, never a URL.</summary>
        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("processor", "Processor executable", ConnectorConfigFieldType.Text, Description: "The external processor to run; resolved next to the host when not an absolute path.") { Group = "Processor", Default = "TelecomBridge.exe" },
            new ConnectorConfigField("arguments", "Command-line arguments", ConnectorConfigFieldType.Multiline, Required: true, Description: "Argument template; %InputFile% and %DestinationPath% are substituted per file.") { Group = "Processor", Default = "--input %InputFile% --output %DestinationPath% --provider ericsson" },
            new ConnectorConfigField("source_path", "Source folder", ConnectorConfigFieldType.Text, Required: true, Description: "Folder scanned for input files (supports the %temp% token).") { Group = "Files" },
            new ConnectorConfigField("file_name", "File name / pattern", ConnectorConfigFieldType.Text, Description: "A file name or wildcard (e.g. *.xml) selecting the input files.") { Group = "Files", Default = "*.xml" },
            new ConnectorConfigField("destination_path", "Destination folder", ConnectorConfigFieldType.Text, Required: true, Description: "Where the processor writes its output (supports the %temp% token).") { Group = "Files", Default = "%TEMP%" },
            new ConnectorConfigField("move_source", "Move source after success", ConnectorConfigFieldType.Boolean, Description: "Move each processed input file after a successful (exit 0) run.") { Group = "Files", Default = "false" },
            new ConnectorConfigField("move_to", "Move destination", ConnectorConfigFieldType.Text, Description: "Folder to move processed input files to.") { Group = "Files", Default = "./", VisibleWhen = new ConnectorConfigFieldVisibility("move_source", "true") },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("transform", "Transform files", "Runs the external processor over each matched source file and streams a per-file outcome."),
        };

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var processor = ResolveProcessorPath(Value(context.Settings, "processor") ?? "TelecomBridge.exe");
            var sourcePath = ExpandTokens(Value(context.Settings, "source_path") ?? string.Empty);

            var problems = new List<string>();
            if (!File.Exists(processor))
            {
                problems.Add($"the processor '{processor}' was not found (it is a host-provided executable)");
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            {
                problems.Add($"the source folder '{sourcePath}' does not exist");
            }

            return Task.FromResult(problems.Count == 0
                ? ConnectionTestResult.Ok("The processor and source folder are present.")
                : ConnectionTestResult.Fail($"XML processor unavailable: {string.Join("; ", problems)}."));
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "transform" : context.OperationName.ToLowerInvariant();
            if (operation != "transform")
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the XML connector.");
            }

            var processor = ResolveProcessorPath(Value(context.Settings, "processor") ?? "TelecomBridge.exe");
            if (!File.Exists(processor))
            {
                throw new InvalidOperationException($"The XML processor '{processor}' was not found - it is a host-provided executable that must be deployed alongside the host.");
            }

            var sourcePath = ExpandTokens(Value(context.Settings, "source_path") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException($"The XML source folder '{sourcePath}' does not exist.");
            }

            var destinationPath = ExpandTokens(Value(context.Settings, "destination_path") ?? string.Empty);
            var template = Value(context.Settings, "arguments") ?? string.Empty;
            var pattern = Value(context.Settings, "file_name") ?? "*.xml";
            var moveSource = string.Equals(Value(context.Settings, "move_source"), "true", StringComparison.OrdinalIgnoreCase);
            var moveTo = ExpandTokens(Value(context.Settings, "move_to") ?? string.Empty);
            var workingDirectory = Path.GetDirectoryName(processor) ?? AppContext.BaseDirectory;

            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MatchesPattern(Path.GetFileName(file), pattern))
                {
                    continue;
                }

                var arguments = BuildArguments(template, file, destinationPath);
                var result = await processRunner.RunAsync(new XmlProcessRequest(processor, arguments, workingDirectory), cancellationToken);

                var moved = false;
                if (moveSource && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(moveTo) && Directory.Exists(moveTo))
                {
                    File.Move(file, Path.Combine(moveTo, Path.GetFileName(file)), overwrite: true);
                    moved = true;
                }

                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["input_file"] = file,
                    ["arguments"] = arguments,
                    ["exit_code"] = (long)result.ExitCode,
                    ["succeeded"] = result.ExitCode == 0,
                    ["destination_path"] = destinationPath,
                    ["stdout"] = Excerpt(result.StandardOutput),
                    ["stderr"] = Excerpt(result.StandardError),
                    ["moved"] = moved,
                });
            }
        }

        /// <summary>Substitutes <c>%InputFile%</c> and <c>%DestinationPath%</c> (case-insensitive) into the template.</summary>
        internal static string BuildArguments(string template, string inputFile, string destinationPath) =>
            ReplaceInsensitive(ReplaceInsensitive(template, "%InputFile%", inputFile), "%DestinationPath%", destinationPath);

        /// <summary>Matches a file name against a name or <c>*</c>/<c>?</c> wildcard (exact, case-insensitive, when no wildcard).</summary>
        internal static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
            }

            return Regex.IsMatch(fileName, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        }

        /// <summary>Expands the <c>%temp%</c> token to the OS temp path (case-insensitive).</summary>
        internal static string ExpandTokens(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : ReplaceInsensitive(path, "%temp%", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        /// <summary>Resolves the processor path: absolute as-is, otherwise relative to the running host directory.</summary>
        internal static string ResolveProcessorPath(string processor) => Path.IsPathRooted(processor) ? processor : Path.Combine(AppContext.BaseDirectory, processor);

        private static string ReplaceInsensitive(string input, string token, string value)
        {
            var builder = new StringBuilder(input.Length);
            var start = 0;
            while (true)
            {
                var index = input.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    builder.Append(input, start, input.Length - start);
                    return builder.ToString();
                }

                builder.Append(input, start, index - start).Append(value);
                start = index + token.Length;
            }
        }

        private static string Excerpt(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= OutputExcerptLimit ? trimmed : trimmed[..OutputExcerptLimit];
        }

        private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}
