#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Lifted from the predecessor platform (BL-97 increment 3): the payload mapping is unchanged; only the namespaces moved.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// Raised when a provider's native billing payload is structurally invalid — not JSON at all, or JSON
    /// that does not carry the provider's documented response shape. Individual rows lacking usable data
    /// are skipped rather than raising this exception.
    /// </summary>
    public sealed class CloudCostPayloadFormatException : FormatException
    {
        public CloudCostPayloadFormatException(string message)
            : base(message)
        {
        }

        public CloudCostPayloadFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
