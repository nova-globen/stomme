#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Xml;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>The XML connector cases (11, plus 6 pattern rows), carried over by name over the port, plus the port's rule: the connector is HostOnly, since it starts a process on the host.</summary>
    public sealed class XmlConnectorTests
    {
        private sealed class FakeRunner : IXmlProcessRunner
        {
            public List<XmlProcessRequest> Requests { get; } = new();

            public int ExitCode { get; set; }

            public Task<XmlProcessResult> RunAsync(XmlProcessRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new XmlProcessResult(ExitCode, "done", string.Empty));
            }
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-xml-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Path);

            public string Write(string name, string content = "x")
            {
                var path = System.IO.Path.Combine(Path, name);
                File.WriteAllText(path, content);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static Dictionary<string, string> Settings(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        private static ConnectorExtractContext ExtractContext(Dictionary<string, string> settings, string operation = "transform") => Contexts.Extract(new StubEgress(), string.Empty, operation, settings: settings, timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesThePort()
        {
            IConnector connector = new XmlConnector();
            Assert.Equal(("xml", "XML", AuthenticationType.None, true), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.HostOnly));
            Assert.Contains(ConnectorCategory.DataAndFiles, connector.Categories);
            Assert.Null(connector.WebsiteUrl);
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "transform" }, ((IConnectorExtractor)connector).Operations.Select(o => o.Name));
        }

        [Fact]
        public void BuildArguments_SubstitutesBothTokensCaseInsensitively() =>
            Assert.Equal(@"--input C:\in\site.xml --output C:\out --provider ericsson", XmlConnector.BuildArguments("--input %InputFile% --output %destinationpath% --provider ericsson", @"C:\in\site.xml", @"C:\out"));

        [Theory]
        [InlineData("a.xml", "*.xml", true)]
        [InlineData("a.txt", "*.xml", false)]
        [InlineData("report.xml", "report.xml", true)]
        [InlineData("Report.XML", "report.xml", true)]
        [InlineData("a1.xml", "a?.xml", true)]
        [InlineData("a12.xml", "a?.xml", false)]
        public void MatchesPattern_HonoursWildcardsAndExactNames(string fileName, string pattern, bool expected) => Assert.Equal(expected, XmlConnector.MatchesPattern(fileName, pattern));

        [Fact]
        public void ExpandTokens_ReplacesTempToken()
        {
            var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.Equal(Path.Combine(tempRoot, "sub"), XmlConnector.ExpandTokens("%temp%" + Path.DirectorySeparatorChar + "sub"));
        }

        [Fact]
        public void ResolveProcessorPath_RelativeIsAnchoredToTheHostDirectory()
        {
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "TelecomBridge.exe"), XmlConnector.ResolveProcessorPath("TelecomBridge.exe"));
            var absolute = Path.Combine(Path.GetTempPath(), "proc.exe");
            Assert.Equal(absolute, XmlConnector.ResolveProcessorPath(absolute));
        }

        [Fact]
        public async Task Extract_RunsProcessorPerMatchedFileAndStreamsOutcome()
        {
            using var temp = new TempDir();
            var processor = temp.Write("proc.exe");
            temp.Write("a.xml");
            temp.Write("b.xml");
            temp.Write("c.txt");
            var runner = new FakeRunner { ExitCode = 0 };
            var settings = Settings(("processor", processor), ("source_path", temp.Path), ("destination_path", temp.Path), ("file_name", "*.xml"), ("arguments", "--input %InputFile% --output %DestinationPath%"));

            var records = await Contexts.DrainAsync(new XmlConnector(runner), ExtractContext(settings));

            Assert.Equal((2, 2), (records.Count, runner.Requests.Count));
            Assert.All(records, record => Assert.Equal((true, 0L), (record.Fields["succeeded"], record.Fields["exit_code"])));
            Assert.All(runner.Requests, request => Assert.Equal(processor, request.ExecutablePath));
            Assert.Contains(runner.Requests, request => request.Arguments.Contains("a.xml", StringComparison.Ordinal) && request.Arguments.Contains(temp.Path, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Extract_MoveSource_MovesProcessedFileAfterSuccess()
        {
            using var source = new TempDir();
            using var archive = new TempDir();
            var processor = source.Write("proc.exe");
            source.Write("a.xml");
            var settings = Settings(("processor", processor), ("source_path", source.Path), ("destination_path", source.Path), ("file_name", "*.xml"), ("arguments", "run"), ("move_source", "true"), ("move_to", archive.Path));

            var record = Assert.Single(await Contexts.DrainAsync(new XmlConnector(new FakeRunner { ExitCode = 0 }), ExtractContext(settings)));

            Assert.Equal(true, record.Fields["moved"]);
            Assert.False(File.Exists(Path.Combine(source.Path, "a.xml")));
            Assert.True(File.Exists(Path.Combine(archive.Path, "a.xml")));
        }

        [Fact]
        public async Task Extract_ProcessorMissing_Throws()
        {
            using var temp = new TempDir();
            temp.Write("a.xml");
            var settings = Settings(("processor", Path.Combine(temp.Path, "does-not-exist.exe")), ("source_path", temp.Path), ("destination_path", temp.Path), ("arguments", "run"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new XmlConnector(new FakeRunner()), ExtractContext(settings)));
            Assert.Contains("processor", ex.Message);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws() =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new XmlConnector(new FakeRunner()), ExtractContext(Settings(), operation: "bogus")));

        [Fact]
        public async Task TestConnection_Fail_WhenProcessorMissing()
        {
            using var temp = new TempDir();
            var result = await new XmlConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), string.Empty, Settings(("source_path", temp.Path)), timeoutSeconds: 30));
            Assert.False(result.Success);
            Assert.Contains("processor", result.Message);
        }

        [Fact]
        public async Task TestConnection_Ok_WhenProcessorAndSourcePresent()
        {
            using var temp = new TempDir();
            var processor = temp.Write("proc.exe");
            var result = await new XmlConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), string.Empty, Settings(("processor", processor), ("source_path", temp.Path)), timeoutSeconds: 30));
            Assert.True(result.Success, result.Message);
        }
    }
}
