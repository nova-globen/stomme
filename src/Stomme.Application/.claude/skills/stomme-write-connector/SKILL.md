---
name: stomme-write-connector
description: Use when adding a connector type of the app's own to Nova Globen Stomme's catalogue - an integration with an external API, service or database that administrators connect to on the Connections page and pipelines or code then use - or when asked to "write a connector", "add an integration for X", "support X in Connections", or to test one.
---

# Write a connector

You will add one connector type to the app's `Server` project: a class implementing `IConnector` and
`IConnectorExtractor` that reaches an HTTP API through the host's egress, an `IServerStartup` that registers it, and a
unit test project that proves it over a stub egress. Afterwards administrators create connections to it on the
Connections page, test them, and run its operations from a pipeline's Extract from connection step or from code. Read
[writing a connector](../../../docs/connectors/writing-a-connector.md) for the contract and
[connectors and connections](../../../docs/connectors/overview.md) for how connections, credentials and the egress
work.

Before you write one, check the catalogue: `GET api/Connections/connectors` or the picker on the Connections page. If
the system is there, or the generic REST connector (`rest`) with a bearer token and headers is enough, use that instead.

The example is "Acme Orders", a JSON API with an API key in a header. Replace the names, the key, the paths and the
operations with the real service's.

## Steps

1. **Choose the type key.** Lower case, prefixed with the app's name so a future Stomme connector cannot take it
   (here `myapp-acme-orders`). It is stored on every connection; never change it after the first connection exists.

2. **Write the connector.** Create `Server/Connectors/AcmeOrders/AcmeOrdersConnector.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Globalization;
   using System.Linq;
   using System.Net.Http;
   using System.Runtime.CompilerServices;
   using System.Text;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Connectivity;
   using Stomme.Modules.Connectors.Shared;

   namespace Stomme.Application.Server.Connectors.AcmeOrders
   {
       /// <summary>Acme's order API: a key in a header, a list of orders, and an order created.</summary>
       public sealed class AcmeOrdersConnector : IConnector, IConnectorExtractor
       {
           public const string Type = "myapp-acme-orders";

           private const int DefaultMax = 100;

           private const string DefaultKeyHeader = "X-Api-Key";

           public string ConnectorType => Type;

           public string DisplayName => "Acme Orders";

           public AuthenticationType AuthType => AuthenticationType.ApiKey;

           public IReadOnlyDictionary<string, string>? CredentialLabels { get; } =
               new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (Acme - Settings - API)" };

           public string? EndpointExample => "https://api.acme.example";

           public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

           public Uri? WebsiteUrl { get; } = new("https://acme.example/");

           public Uri? ApiDocumentationUrl { get; } = new("https://acme.example/docs/api");

           public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
           {
               new ConnectorConfigField("api_key_header", "API key header", ConnectorConfigFieldType.Text, Required: false, Description: "The header Acme reads the key from.")
               {
                   Default = DefaultKeyHeader,
               },
           });

           public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
           {
               new ConnectorOperation("orders", "Get orders", "Acme's orders, newest first.")
               {
                   Parameters = new ConnectorConfigSchema(new[]
                   {
                       new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Select, Required: false, Description: "Only orders in this status.")
                       {
                           Options = new[] { new ConnectorConfigFieldOption("open", "Open"), new ConnectorConfigFieldOption("shipped", "Shipped") },
                       },
                       new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many orders, at most.")
                       {
                           Default = "100",
                       },
                   }),
               },
               new ConnectorOperation("order_create", "Create an order", "Creates one order and answers its id.")
               {
                   Parameters = new ConnectorConfigSchema(new[]
                   {
                       new ConnectorConfigField("body", "Order", ConnectorConfigFieldType.Multiline, Required: true, Description: "The order as Acme's JSON - {\"customer\": \"C-1\", \"lines\": []}."),
                   }),
               },
           };

           public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
           {
               ArgumentNullException.ThrowIfNull(context);
               try
               {
                   var baseUrl = Values.BaseUrl(context.Endpoint, string.Empty);
                   using var client = CreateClient(context.Egress, context.Timeout, context.Settings, context.Credentials);
                   using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/me"), cancellationToken);
                   if (!response.IsSuccessStatusCode)
                   {
                       return ConnectionTestResult.Fail($"Acme answered HTTP {(int)response.StatusCode} - check the address and the API key.");
                   }

                   return await JsonAnswers.NotJsonAsync(response, "Acme", cancellationToken) is { } notJson
                       ? ConnectionTestResult.Fail(notJson)
                       : ConnectionTestResult.Ok("Acme answers and accepts the key.");
               }
               catch (InvalidOperationException ex) when (!context.Egress.IsDenial(ex))
               {
                   return ConnectionTestResult.Fail(ex.Message);
               }
               catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
               {
                   return ConnectionTestResult.Fail(ex.Message); // the remote's failure; the host's refusal propagates
               }
           }

           public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
           {
               ArgumentNullException.ThrowIfNull(context);
               var operation = (context.OperationName ?? string.Empty).Trim().ToLowerInvariant();
               var baseUrl = Values.BaseUrl(context.Endpoint, string.Empty);
               using var client = CreateClient(context.Egress, context.Timeout, context.Settings, context.Credentials);
               switch (operation)
               {
                   case "orders":
                       {
                           var max = ReadMax(context.Parameters);
                           var url = baseUrl + "/v1/orders";
                           if (Values.Get(context.Parameters, "status") is { } status)
                           {
                               url += "?status=" + Uri.EscapeDataString(status);
                           }

                           using var response = await TimeoutAsTimeoutException(RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken), cancellationToken);
                           var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                           if (!response.IsSuccessStatusCode)
                           {
                               throw new InvalidOperationException($"Acme answered HTTP {(int)response.StatusCode} to the order list.");
                           }

                           foreach (var record in JsonRecords.Parse(payload, context.Parameters).Take(max))
                           {
                               yield return record;
                           }

                           yield break;
                       }

                   case "order_create":
                       {
                           var body = Values.Get(context.Parameters, "body") ?? throw new InvalidOperationException("Give the order as JSON (body).");

                           // Sent once, not through the retry policy: repeating a create after a 503 could create it twice.
                           using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                           using var response = await TimeoutAsTimeoutException(client.SendAsync(request, cancellationToken), cancellationToken);
                           var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                           if (!response.IsSuccessStatusCode)
                           {
                               throw new InvalidOperationException($"Acme refused the order with HTTP {(int)response.StatusCode}.");
                           }

                           yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                           {
                               ["status"] = (int)response.StatusCode,
                               ["body"] = payload,
                           });
                           yield break;
                       }

                   default:
                       throw new InvalidOperationException($"Acme Orders has no operation '{context.OperationName}'.");
               }
           }

           // The client's timeout surfaces as a TaskCanceledException. Since Stomme 10.73.0 the host's extraction port
           // reports that as TimedOut itself; turning it into a TimeoutException is harmless and makes the connector say
           // so on older hosts too. A cancellation the caller asked for stays one.
           private static async Task<HttpResponseMessage> TimeoutAsTimeoutException(Task<HttpResponseMessage> sending, CancellationToken cancellationToken)
           {
               try
               {
                   return await sending;
               }
               catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
               {
                   throw new TimeoutException("Acme did not answer within the connection's timeout.", ex);
               }
           }

           private static HttpClient CreateClient(IConnectorEgress egress, TimeSpan timeout, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials)
           {
               var key = credentials.TryGetValue("api-key", out var value) && !string.IsNullOrEmpty(value)
                   ? value
                   : throw new InvalidOperationException("The connection has no API key - enter it on the connection.");
               var header = Values.GetOrDefault(settings, "api_key_header", DefaultKeyHeader);
               var client = egress.CreateHttpClient(timeout);
               client.DefaultRequestHeaders.TryAddWithoutValidation(header, key);
               client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
               return client;
           }

           private static int ReadMax(IReadOnlyDictionary<string, string> parameters) =>
               Values.Get(parameters, "max") is { } text
                   ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
                       ? max
                       : throw new InvalidOperationException($"'{text}' is not a number above 0 (max).")
                   : DefaultMax;
       }
   }
   ```

   What the code holds to, and why:
   - The only network is `context.Egress.CreateHttpClient(context.Timeout)`.
   - Every `catch` lets the host's denial through (`when (!context.Egress.IsDenial(ex))`).
   - The key comes from `Credentials["api-key"]`, the `ApiKey` scheme's field. It never comes from a setting or the
     endpoint.
   - Settings and parameters are read with fallbacks. The host applies no defaults and does not validate parameters.
   - No `yield return` sits inside a `try` with a `catch`: C# forbids it. Failures inside the loop throw, and the host
     categorises them.
   - Every request inside an operation goes through `TimeoutAsTimeoutException`. Since Stomme 10.73.0 the host's
     extraction port reports a connector's own HTTP timeout as `TimedOut` without it, so the wrapper is harmless but no
     longer needed; before 10.73.0 the port threw the `TaskCanceledException` at its caller instead. A test never
     needed it: the host maps a timed-out test to `TimedOut` itself.

   For a database or other driver instead of HTTP, set `EndpointKind => ConnectorEndpointKind.ConnectionString`, call
   `context.Egress.ResolveAndPinAsync(host, port, cancellationToken)`, throw `ConnectorEgressDeniedException` when it
   answers `null`, and make the driver dial the returned address. See the egress rules in
   [writing a connector](../../../docs/connectors/writing-a-connector.md#the-egress-rules).

3. **Register it.** Create `Server/Connectors/ConnectorsStartup.cs`:

   ```csharp
   using Stomme.Application.Server.Connectors.AcmeOrders;
   using Stomme.Connectivity;
   using Stomme.Infrastructure;
   using Microsoft.AspNetCore.Builder;
   using Microsoft.AspNetCore.Hosting;
   using Microsoft.Extensions.DependencyInjection;

   namespace Stomme.Application.Server.Connectors
   {
       /// <summary>Registers the app's connectors in the host's catalogue.</summary>
       public class ConnectorsStartup : IServerStartup
       {
           public void ConfigureServices(IServiceCollection services)
           {
               services.AddSingleton<IConnector, AcmeOrdersConnector>();
           }

           public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
           {
           }

           public void ConfigureMvc(IMvcBuilder mvcBuilder)
           {
           }
       }
   }
   ```

   The host discovers this class because the `Server` assembly's name contains `Stomme` (the template gives every
   project's assembly name a `.Stomme` suffix for this); do not rename the assembly.

4. **Add a test project.** Create `Server.Tests/Stomme.Application.Server.Tests.csproj`:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">

     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <Nullable>enable</Nullable>
       <IsPackable>false</IsPackable>
       <IsTestProject>true</IsTestProject>
     </PropertyGroup>

     <ItemGroup>
       <PackageReference Include="Microsoft.NET.Test.Sdk" />
       <PackageReference Include="xunit" />
       <PackageReference Include="xunit.runner.visualstudio">
         <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
         <PrivateAssets>all</PrivateAssets>
       </PackageReference>
     </ItemGroup>

     <ItemGroup>
       <ProjectReference Include="..\Server\Stomme.Application.Server.csproj" />
     </ItemGroup>

   </Project>
   ```

   The app manages package versions centrally. Add these lines to the `<ItemGroup>` of `Directory.Packages.props` (the
   versions Stomme's own tests use at the version this template was made from; newer ones are fine):

   ```xml
   <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
   <PackageVersion Include="xunit" Version="2.9.2" />
   <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
   ```

   Add the project to `Stomme.Application.slnx`:

   ```xml
   <Project Path="Server.Tests\Stomme.Application.Server.Tests.csproj" />
   ```

5. **Write the tests.** Create `Server.Tests/AcmeOrdersConnectorTests.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using System.Net;
   using System.Net.Http;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Application.Server.Connectors.AcmeOrders;
   using Stomme.Connectivity;
   using Xunit;

   namespace Stomme.Application.Server.Tests
   {
       public sealed class AcmeOrdersConnectorTests
       {
           [Fact]
           public async Task A_test_sends_the_key_in_the_configured_header()
           {
               var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"me\"}");
               var context = new ConnectorTestContext("https://api.acme.example/", new Dictionary<string, string> { ["api_key_header"] = "X-Acme-Key" }, TimeSpan.FromSeconds(5), new StubEgress(handler))
               {
                   Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
               };

               var result = await new AcmeOrdersConnector().TestConnectionAsync(context);

               Assert.True(result.Success, result.Message);
               Assert.Equal("https://api.acme.example/v1/me", handler.Requests.Single().RequestUri?.ToString());
               Assert.Equal("k-1", handler.Requests.Single().Headers.GetValues("X-Acme-Key").Single());
           }

           [Fact]
           public async Task A_sign_in_page_a_bad_status_and_a_missing_key_are_failures()
           {
               Assert.False((await new AcmeOrdersConnector().TestConnectionAsync(TestContext(new StubHandler(HttpStatusCode.OK, "<html>Sign in</html>")))).Success);
               Assert.False((await new AcmeOrdersConnector().TestConnectionAsync(TestContext(new StubHandler(HttpStatusCode.Unauthorized, "{}")))).Success);

               var noKey = new ConnectorTestContext("https://api.acme.example", new Dictionary<string, string>(), TimeSpan.FromSeconds(5), new StubEgress(new StubHandler(HttpStatusCode.OK, "{}")));
               Assert.False((await new AcmeOrdersConnector().TestConnectionAsync(noKey)).Success);
           }

           [Fact]
           public async Task A_denial_by_the_egress_propagates()
           {
               var egress = new StubEgress(new ThrowingHandler()) { DeniesEverything = true };
               var context = new ConnectorTestContext("https://10.0.0.5", new Dictionary<string, string>(), TimeSpan.FromSeconds(5), egress)
               {
                   Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
               };

               await Assert.ThrowsAsync<HttpRequestException>(() => new AcmeOrdersConnector().TestConnectionAsync(context));
           }

           [Fact]
           public async Task Orders_reads_records_up_to_max_and_filters_by_status()
           {
               var handler = new StubHandler(HttpStatusCode.OK, "[{\"id\":1,\"total\":9.5},{\"id\":2,\"total\":3},{\"id\":3,\"total\":1}]");
               var context = new ConnectorExtractContext("https://api.acme.example", new Dictionary<string, string>(), TimeSpan.FromSeconds(5), new StubEgress(handler), "ORDERS",
                   new Dictionary<string, string> { ["status"] = "open", ["max"] = "2" })
               {
                   Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
               };

               var records = new List<ConnectorRecord>();
               await foreach (var record in new AcmeOrdersConnector().ExtractAsync(context))
               {
                   records.Add(record);
               }

               Assert.Equal(2, records.Count);
               Assert.Equal(1L, records[0].Fields["id"]);
               Assert.Equal("https://api.acme.example/v1/orders?status=open", handler.Requests.Single().RequestUri?.ToString());
           }

           [Fact]
           public async Task A_timeout_during_an_operation_is_a_TimeoutException()
           {
               var context = new ConnectorExtractContext("https://api.acme.example", new Dictionary<string, string>(), TimeSpan.FromMilliseconds(200), new StubEgress(new SlowHandler()), "orders",
                   new Dictionary<string, string>())
               {
                   Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
               };

               await Assert.ThrowsAsync<TimeoutException>(async () =>
               {
                   await foreach (var record in new AcmeOrdersConnector().ExtractAsync(context))
                   {
                   }
               });
           }

           private static ConnectorTestContext TestContext(HttpMessageHandler handler) =>
               new("https://api.acme.example", new Dictionary<string, string>(), TimeSpan.FromSeconds(5), new StubEgress(handler))
               {
                   Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
               };

           private sealed class StubEgress(HttpMessageHandler handler) : IConnectorEgress
           {
               public bool DeniesEverything { get; init; }

               public HttpClient CreateHttpClient(TimeSpan timeout) => new(handler, disposeHandler: false) { Timeout = timeout };

               public Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default) =>
                   Task.FromResult<IPAddress?>(DeniesEverything ? null : IPAddress.Loopback);

               public bool IsDenial(Exception exception) => DeniesEverything;
           }

           private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
           {
               public List<HttpRequestMessage> Requests { get; } = new();

               protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
               {
                   Requests.Add(request);
                   return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
               }
           }

           private sealed class SlowHandler : HttpMessageHandler
           {
               protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
               {
                   await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                   return new HttpResponseMessage(HttpStatusCode.OK);
               }
           }

           private sealed class ThrowingHandler : HttpMessageHandler
           {
               protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                   throw new HttpRequestException("refused");
           }
       }
   }
   ```

## Verify

1. `dotnet build` and `dotnet test`: the build is clean and the five tests pass. Check the test count rose by the
   tests you added.
2. Run the app (`cd Server`, `dotnet run`), sign in as the Host, and open `/admin/connections`.
   - The connector list under **New connection** shows **Acme Orders** in the Commerce group. Choosing it shows the
     endpoint placeholder `https://api.acme.example`, the credential field labelled "API key (Acme - Settings - API)"
     and the setting "API key header" filled with `X-Api-Key`.
   - `GET api/Connections/connectors` (signed in) lists it with `authFields` `api-key`, both operations and their
     parameters, and a monogram `icon`.
3. Create a connection to a real or local test server, enter the key, and press **Test**.
   - Against a reachable server with a good key the page says "Connection succeeded." and the row shows `Healthy`.
   - With a wrong key it says "The connector reported a failure." and the row shows `Failing`. The key appears nowhere
     on the page.
   - A test server on `localhost`, `127.0.0.1` or a LAN address is refused ("The destination is not permitted by the
     outbound policy.") until the Host adds its exact host to **Private hosts** on the same page and saves. An entry
     matches only the host as written: `127.0.0.1` does not open `localhost`. That proves the connector uses the egress.
4. Run an operation from a pipeline. Pipelines are off by default: set `Pipelines:Enabled` to `true` (see
   [configuration](../../../docs/architecture/configuration.md)) and restart. Create a pipeline with an **Extract from
   connection** step: pick the connection, operation `orders`, the parameter lines `max = 2` and `status = open`. Publish
   and run it. The step completes, its `Result` output is a JSON array of two orders and `RecordCount` is 2, and
   neither the step data nor the run log carries the key.
5. Prove the timeout: point a connection with a short timeout (2 seconds) at a server that answers slower, and run an
   operation. The answer is `TimedOut`, not an exception.
6. Check the browser console is clean and the server log has no exception for the connection beyond the ones you
   provoked. A connector failure is logged as a warning naming the connection id, the type key and the exception type.

## Common mistakes

- **Every test answers `DeniedByPolicy` against your local server.** The cause: loopback and private addresses are
  refused by design. The fix: the Host adds the exact host the endpoint names (`127.0.0.1`, `api.local`) under Private
  hosts. Do not bypass the egress with your own `HttpClient`.
- **The Connections page and every `api/Connections` read answer 500 after you added a connector.** The cause: your
  type key equals another connector's (compared case-insensitively). The host still starts; the catalogue is built on
  first use and throws there. Admin > Event Log shows "Connector type key '...' is published twice: by ... and by ...".
  The fix: choose a prefixed key.
- **The connector is missing from the picker.** The cause: the startup class lives in a project whose assembly name does
  not contain `Stomme` (the template's `.Stomme` suffix was removed or the class went into a new project), or it is
  abstract or generic, so the scan skips it. The fix: put it in `Server/` as a plain class. (A startup class without a
  parameterless constructor is not skipped: the host fails to start with a `MissingMethodException`.)
- **A failure shows as "the connector faulted" with no detail.** The cause: by design, the host discards connector
  messages. The fix: read the server log for the exception type and the connection id, and reproduce it in a unit
  test, where the message is visible.
- **A policy refusal shows as "the connector reported a failure".** The cause: a `catch` without
  `when (!context.Egress.IsDenial(ex))` turned the host's refusal into `Fail`. The fix: add the filter.
- **`CS1626: Cannot yield a value in the body of a try block with a catch clause`.** The cause: `yield return` inside
  a `try`/`catch`. The fix: validate before yielding, and let exceptions inside the loop propagate.
- **Every connection to it is refused with "A URL endpoint must be an absolute http or https URL".** The cause: the
  service's address is not `http(s)` (for example `ldap://`). The fix: `EndpointKind => ConnectorEndpointKind.ConnectionString`.
- **An HTTP timeout during an operation surfaces as a `TaskCanceledException`** at the caller (a 500 from an endpoint, a
  pipeline step faulted with "The request was canceled due to the configured HttpClient.Timeout ...") rather than
  `TimedOut`. The cause: a Stomme older than 10.73.0, whose extraction port let `OperationCanceledException` through;
  from 10.73.0 the port reports it as `TimedOut`. The fix: upgrade Stomme, or send through
  `TimeoutAsTimeoutException` as the connector above does; a `TimeoutException` is reported as `TimedOut`.