#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The predecessor platform's RetryAfterHandlerTests (6) by name over the port: the policy sends AROUND the egress's
    /// client rather than sitting inside it as a handler, rebuilding the request per attempt; the timing and the give-up
    /// rule are the predecessor platform's.
    /// </summary>
    public sealed class RetryAfterPolicyTests
    {
        // Factories, not instances: a throttled response is disposed before the retry, so each attempt must receive a fresh message.
        private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
        {
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = responses[Math.Min(Calls, responses.Length - 1)]();
                Calls++;
                return Task.FromResult(response);
            }
        }

        private static Func<HttpResponseMessage> Throttled(int? retryAfterSeconds = null) => () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.Add("Retry-After", seconds.ToString());
            }

            return response;
        };

        private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);

        private static Func<HttpRequestMessage> Get() => () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/resource");

        [Fact]
        public async Task Send_HonoursAzuresRetryAfterMillisecondsWhereNoRetryAfterIsGiven()
        {
            var delays = new List<TimeSpan>();
            var both = Throttled(3);
            Func<HttpResponseMessage> azure = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.Add("x-ms-retry-after-ms", "15.5"); // Azure Cosmos DB's throttling
                return response;
            };
            Func<HttpResponseMessage> azureAndStandard = () =>
            {
                var response = both();
                response.Headers.Add("x-ms-retry-after-ms", "15");
                return response;
            };
            var inner = new SequenceHandler(azure, azureAndStandard, Status(HttpStatusCode.OK));
            var policy = new RetryAfterPolicy { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal((HttpStatusCode.OK, 3), (response.StatusCode, inner.Calls));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(15.5), TimeSpan.FromSeconds(3) }, delays); // the standard header first where both are given
        }

        [Fact]
        public async Task Send_HonoursRetryAfterSecondsAndSucceeds()
        {
            var delays = new List<TimeSpan>();
            var inner = new SequenceHandler(Throttled(2), Status(HttpStatusCode.OK));
            var policy = new RetryAfterPolicy { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal((HttpStatusCode.OK, 2), (response.StatusCode, inner.Calls));
            Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task Send_FallsBackToExponentialBackoffWithoutHeader()
        {
            var delays = new List<TimeSpan>();
            var inner = new SequenceHandler(Throttled(), Throttled(), Status(HttpStatusCode.OK));
            var policy = new RetryAfterPolicy { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task Send_GivesUpAfterMaxRetries()
        {
            var inner = new SequenceHandler(Throttled(1));
            var policy = new RetryAfterPolicy(maxRetries: 2) { Delay = (_, _) => Task.CompletedTask };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal((HttpStatusCode.TooManyRequests, 3), (response.StatusCode, inner.Calls));
        }

        [Fact]
        public async Task Send_RetriesServiceUnavailable()
        {
            var inner = new SequenceHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));
            var policy = new RetryAfterPolicy { Delay = (_, _) => Task.CompletedTask };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal((HttpStatusCode.OK, 2), (response.StatusCode, inner.Calls));
        }

        [Fact]
        public async Task Send_CapsDelayAtMaxDelay()
        {
            var delays = new List<TimeSpan>();
            var inner = new SequenceHandler(Throttled(3600), Status(HttpStatusCode.OK));
            var policy = new RetryAfterPolicy(maxDelay: TimeSpan.FromSeconds(5)) { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, delays);
        }

        [Fact]
        public async Task Send_PassesOtherStatusesThrough()
        {
            var inner = new SequenceHandler(Status(HttpStatusCode.NotFound));
            var policy = new RetryAfterPolicy { Delay = (_, _) => Task.CompletedTask };

            using var client = new StubEgress(inner).CreateHttpClient(TimeSpan.FromSeconds(5));
            using var response = await policy.SendAsync(client, Get());

            Assert.Equal((HttpStatusCode.NotFound, 1), (response.StatusCode, inner.Calls));
        }
    }
}
