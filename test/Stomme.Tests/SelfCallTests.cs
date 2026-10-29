#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The host's calls to its own API go to its own listener: the first plain-HTTP address it listens on, a wildcard
    /// one as the IPv4 loopback, none for TLS alone or a socket; <c>Installation:InternalUrl</c> naming another, or <c>public</c>
    /// keeping the public address; asked again until the server has its addresses. A call is sent there with the address it
    /// was made to kept as its host and scheme and the caller's address forwarded; a call already local, or with nowhere local
    /// to go, is sent as it is.
    /// </summary>
    public sealed class SelfCallTests
    {
        private sealed class Server : IServer
        {
            public Server(params string[] addresses)
            {
                var feature = new ServerAddressesFeature();
                foreach (var address in addresses)
                {
                    feature.Addresses.Add(address);
                }

                Addresses = feature.Addresses;
                Features.Set<IServerAddressesFeature>(feature);
            }

            public ICollection<string> Addresses { get; }

            public IFeatureCollection Features { get; } = new FeatureCollection();

            public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }

        private sealed class Capture : HttpMessageHandler
        {
            public HttpRequestMessage? Sent { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Sent = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private static SelfCallAddress Address(Server server, string? internalUrl = null) =>
            new(server, new ConfigurationBuilder().AddInMemoryCollection(internalUrl is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Installation:InternalUrl"] = internalUrl }).Build(), NullLogger<SelfCallAddress>.Instance);

        [Fact]
        public void The_hosts_own_listener_is_its_first_plain_http_address_a_wildcard_one_as_the_loopback()
        {
            Uri? Of(params string[] addresses) => SelfCallAddress.Listener(addresses);
            Assert.Equal("http://localhost:5194/", Of("https://localhost:44357", "http://localhost:5194")?.ToString()); // TLS passed over
            Assert.Equal("http://127.0.0.1:8080/", Of("http://[::]:8080")?.ToString());
            Assert.Equal("http://127.0.0.1:8080/", Of("http://+:8080")?.ToString());
            Assert.Equal("http://127.0.0.1/", Of("http://*:80")?.ToString()); // port 80, the default
            Assert.Equal("http://127.0.0.1:5000/", Of("http://0.0.0.0:5000")?.ToString());
            Assert.Equal("http://10.0.0.5:8080/", Of("http://10.0.0.5:8080")?.ToString());
            Assert.Null(Of("https://+:443")); // TLS alone: the public address is kept
            Assert.Null(Of("http://unix:/tmp/kestrel.sock"));
            Assert.Null(Of("not an address"));
            Assert.Null(Of());
        }

        [Fact]
        public void The_setting_names_another_listener_or_keeps_the_public_address_and_the_addresses_are_asked_for_until_there()
        {
            Assert.Equal("http://127.0.0.1:8080/", Address(new Server("http://+:8080")).Resolve()?.ToString());
            Assert.Equal("http://127.0.0.1:8080/", Address(new Server("http://+:8080"), "  ").Resolve()?.ToString());
            Assert.Equal("http://app.internal:9000/", Address(new Server("http://+:8080"), "http://app.internal:9000/kassa?x=1").Resolve()?.ToString()); // its authority alone
            Assert.Equal("https://app.internal/", Address(new Server("http://+:8080"), "https://app.internal").Resolve()?.ToString());
            Assert.Null(Address(new Server("http://+:8080"), "PUBLIC").Resolve());
            Assert.Equal("http://127.0.0.1:8080/", Address(new Server("http://+:8080"), "ftp://app.internal").Resolve()?.ToString()); // not http: the listener, said in the log
            Assert.Equal("http://127.0.0.1:8080/", Address(new Server("http://+:8080"), "http://user:secret@app.internal").Resolve()?.ToString());

            var starting = new Server();
            var address = Address(starting);
            Assert.Null(address.Resolve()); // not started: nothing yet, asked again
            starting.Addresses.Add("http://127.0.0.1:61234");
            Assert.Equal("http://127.0.0.1:61234/", address.Resolve()?.ToString());
            starting.Addresses.Clear();
            Assert.Equal("http://127.0.0.1:61234/", address.Resolve()?.ToString()); // known once found
        }

        [Fact]
        public async Task A_call_to_the_sites_public_address_goes_to_the_listener_with_its_host_scheme_and_caller_kept()
        {
            async Task<HttpRequestMessage> Send(SelfCallAddress address, string url, string? caller = "203.0.113.7", Action<HttpRequestMessage>? before = null)
            {
                var context = new DefaultHttpContext();
                if (caller is not null)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(caller);
                }

                var capture = new Capture();
                using var client = new HttpClient(new SelfCallHandler(address, new HttpContextAccessor { HttpContext = caller is null ? null : context }) { InnerHandler = capture });
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                before?.Invoke(request);
                await client.SendAsync(request);
                return capture.Sent!;
            }

            var listener = Address(new Server("http://[::]:8080"));
            var sent = await Send(listener, "https://play.example.com/kassa/api/Database?name=SQLite&x=%C3%A5");
            Assert.Equal("http://127.0.0.1:8080/kassa/api/Database?name=SQLite&x=%C3%A5", sent.RequestUri!.AbsoluteUri);
            Assert.Equal("play.example.com", sent.Headers.Host);
            Assert.Equal(new[] { "https" }, sent.Headers.GetValues("X-Forwarded-Proto"));
            Assert.Equal(new[] { "203.0.113.7" }, sent.Headers.GetValues("X-Forwarded-For"));

            var plain = await Send(listener, "http://kafe.example:8081/api/Page", caller: null); // in a circuit: no request, no caller
            Assert.Equal(("http://127.0.0.1:8080/api/Page", "kafe.example:8081", "http"), (plain.RequestUri!.AbsoluteUri, plain.Headers.Host, plain.Headers.GetValues("X-Forwarded-Proto").Single()));
            Assert.False(plain.Headers.Contains("X-Forwarded-For"));

            var forwarded = await Send(listener, "https://play.example.com/api/Page", before: request => { request.Headers.Add("X-Forwarded-For", "198.51.100.2"); request.Headers.Add("X-Forwarded-Proto", "http"); });
            Assert.Equal(new[] { "198.51.100.2" }, forwarded.Headers.GetValues("X-Forwarded-For")); // a caller already said is kept
            Assert.Equal(new[] { "https" }, forwarded.Headers.GetValues("X-Forwarded-Proto")); // the scheme is the call's

            foreach (var local in new[] { "http://localhost:44357/api/Page", "https://127.0.0.1:44357/api/Page", "http://127.0.0.1:8080/api/Page" })
            {
                var untouched = await Send(listener, local);
                Assert.Equal((local, null), (untouched.RequestUri!.AbsoluteUri, untouched.Headers.Host)); // already local: as it is
            }

            var publicOnly = await Send(Address(new Server("http://+:8080"), "public"), "https://play.example.com/api/Page");
            Assert.Equal(("https://play.example.com/api/Page", null), (publicOnly.RequestUri!.AbsoluteUri, publicOnly.Headers.Host));
            var inMemory = await Send(Address(new Server()), "https://play.example.com/api/Page"); // no listener: as before
            Assert.Equal("https://play.example.com/api/Page", inMemory.RequestUri!.AbsoluteUri);
        }
    }
}
