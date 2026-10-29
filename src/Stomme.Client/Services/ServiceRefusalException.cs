using System;
using System.Net;

namespace Stomme.Services
{
    /// <summary>
    /// The server refused a request made by a client service that opted in to <c>ThrowOnRefusal</c>; the message is the
    /// server's own reason, so a page shows the same words on a WebAssembly site as on a Server site.
    /// </summary>
    public sealed class ServiceRefusalException : Exception
    {
        public ServiceRefusalException(string message, HttpStatusCode statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}
