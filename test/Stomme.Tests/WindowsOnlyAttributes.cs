using System;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A fact that measures something only Windows has - its file locking, its MAUI workload - and so is SKIPPED elsewhere,
    /// with the reason, rather than reported green on a platform where it measured nothing.
    /// </summary>
    /// <remarks>
    /// Use it only where the instrument itself is Windows-only. A product behaviour that differs on Linux is a defect to
    /// fix, not a test to skip: the product is hosted on Linux.
    /// </remarks>
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(string reason)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows only: " + reason;
            }
        }
    }

    /// <summary>The theory form of <see cref="WindowsFactAttribute"/>.</summary>
    public sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute(string reason)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows only: " + reason;
            }
        }
    }
}
