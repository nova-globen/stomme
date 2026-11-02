#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Services;
using Stomme.Services;
using FluentValidation;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// A connection form left incomplete on an Interactive Server site - the pipeline editor's "New connection" or Admin,
    /// Connections - said FluentValidation's own text, "Validation failed: -- Endpoint: 'Endpoint' must not be empty. Severity:
    /// Error" (BL-240, driven). The server IService now refuses with the validator's message, the words the browser's HTTP
    /// service already showed from the 400.
    /// </summary>
    public sealed class ServerConnectionsRefusalTests
    {
        [Theory]
        [InlineData("Local API", "rest", "", 30, "Give the address the connection reaches - its URL or connection string.")]
        [InlineData("", "rest", "https://api.example.test", 30, "Give the connection a name.")]
        [InlineData("Local API", "", "https://api.example.test", 30, "Choose a connector.")]
        [InlineData("Local API", "rest", "https://api.example.test", 0, "The timeout is 1 to 300 seconds.")]
        public async Task An_incomplete_connection_is_refused_in_the_validators_words(string name, string connector, string endpoint, int timeout, string said)
        {
            var service = new ServerConnectionsService(new ValidatingMediator());
            var request = new ConnectionRequest { Name = name, ConnectorType = connector, Endpoint = endpoint, TimeoutSeconds = timeout };

            var created = await Assert.ThrowsAsync<ServiceRefusalException>(() => service.CreateAsync(request));
            Assert.Equal((said, HttpStatusCode.BadRequest), (created.Message, created.StatusCode));

            if (connector.Length > 0) // an edit names no connector
            {
                var updated = await Assert.ThrowsAsync<ServiceRefusalException>(() => service.UpdateAsync(Guid.NewGuid(), request));
                Assert.Equal(said, updated.Message);
            }
        }

        // Runs each command's own validator, as the mediator's validation behaviour does, and stops there.
        private sealed class ValidatingMediator : IMediator
        {
            public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            {
                switch (request)
                {
                    case CreateConnectionCommand create:
                        new CreateConnectionCommandValidator().ValidateAndThrow(create);
                        break;
                    case UpdateConnectionCommand update:
                        new UpdateConnectionCommandValidator().ValidateAndThrow(update);
                        break;
                }

                throw new InvalidOperationException("Only an invalid command is sent in this test.");
            }

            public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;

            public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
}
