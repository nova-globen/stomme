#nullable enable
using System.Collections.Generic;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The assembly marker the engine's <c>AddStepsFrom</c> scans: every public, concrete, non-generic step in this
    /// assembly is registered by it (BL-95 increment (d)).
    /// </summary>
    public sealed class PipelineStepsMarker
    {
        private PipelineStepsMarker()
        {
        }
    }

    /// <summary>
    /// The settings the built-in steps read, bound from the <c>Pipelines</c> section of THIS host's configuration
    /// root (the one that ends with the override file - BL-162) at composition time. Every switch fails closed: an
    /// absent section refuses private-network HTTP egress, confines file steps to the host's data directory and
    /// bounds a delay.
    /// </summary>
    public sealed class PipelineStepOptions
    {
        public const string Section = "Pipelines";

        public HttpStepOptions Http { get; set; } = new();

        public FileStepOptions Files { get; set; } = new();

        public ScriptStepOptions Scripts { get; set; } = new();

        public ProcessStepOptions Processes { get; set; } = new();

        /// <summary>The longest pause a <see cref="Delay"/> step may take; a longer one faults the run.</summary>
        public int MaxDelaySeconds { get; set; } = 300;
    }

    /// <summary>
    /// Settings for the JavaScript sandbox behind the script step and the map step's <c>js:</c> computed fields
    /// (<c>Pipelines:Scripts</c>, the predecessor platform's <c>Actions:Scripts</c>). OFF by default: a script is a code-execution
    /// surface, even one with no host access; when on, it runs in-process in Jint with no CLR access and is bounded by
    /// statements, wall-clock and memory. Native scripts (PowerShell, Bash, Python) are not a step here: the predecessor platform refused
    /// them without OS isolation, and the process step is the bounded way to run a program.
    /// </summary>
    public sealed class ScriptStepOptions
    {
        public bool Enabled { get; set; }

        public int StatementLimit { get; set; } = 100_000;

        public int TimeoutSeconds { get; set; } = 5;

        public long MemoryLimitBytes { get; set; } = 64L * 1024 * 1024;
    }

    /// <summary>
    /// Settings for the process step (<c>Pipelines:Processes</c>, the predecessor platform's <c>Actions:Processes</c>). OFF by default,
    /// and even when on nothing may launch until a deployment lists the directories its approved programs live in.
    /// </summary>
    public sealed class ProcessStepOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Directories an executable may launch from; one outside every root is refused.</summary>
        public List<string> AllowedRoots { get; set; } = new();

        /// <summary>The wall-clock bound of one launch; the process tree is killed on overrun.</summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>The most characters of output kept.</summary>
        public int MaxOutputChars { get; set; } = 1_000_000;
    }

    /// <summary>Settings for the HTTP request step (<c>Pipelines:Http</c>).</summary>
    public sealed class HttpStepOptions
    {
        /// <summary>
        /// Host names a request may reach even when they resolve to a denied address (loopback, the LAN, link-local,
        /// the cloud metadata endpoint) - the framework's ONE outbound guard's allowlist (<c>OutboundConnectGuard</c>,
        /// BL-132), the same option the webhook channel, the MCP client and upload-by-URL carry. Empty by default: a
        /// pipeline authored by a site administrator must not be able to probe the host's own network (the SSRF
        /// class), and the decision is taken at CONNECT time on the resolved address, so a redirect or a DNS answer
        /// that changes between check and connect cannot slip past it.
        /// </summary>
        public List<string> AllowedHosts { get; set; } = new();

        /// <summary>The ceiling on a step's own timeout input.</summary>
        public int MaxTimeoutSeconds { get; set; } = 120;

        /// <summary>The most response bytes a step captures; a longer body is cut and the step records that it was.</summary>
        public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>How many redirects a request follows (each hop goes through the egress check again).</summary>
        public int MaxRedirects { get; set; } = 3;

        /// <summary>BL-229: the most pages one paginated request reads, whatever its own 'Most pages' says.</summary>
        public int MaxPages { get; set; } = 100;
    }

    /// <summary>Settings for the file steps (<c>Pipelines:Files</c>).</summary>
    public sealed class FileStepOptions
    {
        /// <summary>
        /// The directory every file step is confined to; each engine tenant (a Stomme site) gets a folder of its own
        /// under it. Defaults to <c>Pipelines/Files</c> under the host's data directory. A path that resolves outside
        /// the site's folder - traversal, a rooted path, a UNC path - is refused.
        /// </summary>
        public string? Root { get; set; }

        /// <summary>The most bytes one file step reads or writes.</summary>
        public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
    }
}
