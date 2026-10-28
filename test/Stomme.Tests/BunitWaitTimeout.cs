using System;
using System.Runtime.CompilerServices;
using Bunit;

namespace Stomme.Tests
{
    /// <summary>
    /// bUnit's waits - <c>WaitForAssertion</c>, <c>WaitForElement</c> - give up after <see cref="BunitContext.DefaultWaitTimeout"/>,
    /// one second unless set, and the setting is process-wide.
    /// </summary>
    /// <remarks>
    /// One second is too short for the first render of a large component in a cold test host: measured 2026-10-07, the pipeline
    /// editor's <c>Javascript_is_offered_where_scripts_run_and_previewed_by_the_server(scripts: False)</c> failed 4 seconds into a
    /// filtered run of 502 tests, while every class was compiling its first renders at once, then passed on the re-run and 12
    /// times out of 12 alone. A longer wait costs a passing test nothing - a wait ends as soon as its assertion holds - and makes
    /// a failing one slower to report.
    /// </remarks>
    internal static class BunitWaitTimeout
    {
        internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        [ModuleInitializer]
        internal static void Raise() => BunitContext.DefaultWaitTimeout = Wait;
    }
}
