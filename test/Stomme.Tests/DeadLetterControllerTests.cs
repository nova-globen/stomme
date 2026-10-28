using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Enums;
using Stomme.EventBus;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-36(b) — pins the DLQ admin controller's mapping onto the <see cref="IDeadLetterQueue"/> port:
    /// the three status shapes (messaging off / Null port / real queue), the list projection, and the
    /// found/not-found answers for retry and discard. The PORT itself is driven live against real RavenDB
    /// by <c>RavenDbDeadLetterAdminLiveTests</c> (poison → dead-letter → retry → redelivery → discard);
    /// this suite is the controller seam over a scripted port.
    /// </summary>
    public class DeadLetterControllerTests
    {
        [Fact]
        public async Task Status_reports_disabled_when_messaging_is_off()
        {
            var controller = NewController(queues: Array.Empty<IDeadLetterQueue>(), messagingEnabled: false);

            var status = await controller.GetStatus();

            Assert.False(status.MessagingEnabled);
            Assert.False(status.HasDeadLetterQueue);
            Assert.Equal(0, status.Count);
        }

        [Fact]
        public async Task Status_reports_no_real_queue_on_the_null_port()
        {
            var controller = NewController(queues: new IDeadLetterQueue[] { new NullDeadLetterQueue() }, messagingEnabled: true);

            var status = await controller.GetStatus();

            Assert.True(status.MessagingEnabled);
            Assert.False(status.HasDeadLetterQueue);
        }

        [Fact]
        public async Task Status_and_list_project_the_real_queue()
        {
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage(
                "RavenTransportMessages/1", "Some.Namespace.InvoiceIssued, Some.Assembly", "boom",
                new DateTimeOffset(2026, 8, 3, 1, 2, 3, TimeSpan.Zero),
                new Dictionary<string, string> { ["rbs2-msg-id"] = "abc" }));

            var controller = NewController(new IDeadLetterQueue[] { queue }, messagingEnabled: true);

            var status = await controller.GetStatus();
            Assert.True(status.HasDeadLetterQueue);
            Assert.Equal(1, status.Count);

            var items = (await controller.Get()).ToList();
            var item = Assert.Single(items);
            Assert.Equal("RavenTransportMessages/1", item.Id);
            Assert.Equal("Some.Namespace.InvoiceIssued, Some.Assembly", item.MessageType);
            Assert.Equal("boom", item.ErrorDetails);
            Assert.Equal("abc", item.Headers["rbs2-msg-id"]);
        }

        [Fact]
        public async Task Retry_and_discard_answer_found_and_not_found()
        {
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("id-1", null, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

            var controller = NewController(new IDeadLetterQueue[] { queue }, messagingEnabled: true);

            Assert.IsType<OkResult>(await controller.Retry(new DeadLetterActionRequest { Id = "id-1" }));
            Assert.Equal("id-1", queue.Retried.Single());

            Assert.IsType<NotFoundResult>(await controller.Retry(new DeadLetterActionRequest { Id = "missing" }));
            Assert.IsType<NotFoundResult>(await controller.Retry(new DeadLetterActionRequest { Id = " " }));

            queue.Messages.Add(new DeadLetterMessage("id-2", null, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));
            Assert.IsType<OkResult>(await controller.Discard(new DeadLetterActionRequest { Id = "id-2" }));
            Assert.Equal("id-2", queue.Discarded.Single());
            Assert.IsType<NotFoundResult>(await controller.Discard(new DeadLetterActionRequest { Id = "missing" }));
        }

        [Fact]
        public async Task Actions_on_the_null_port_answer_not_found_rather_than_touching_it()
        {
            var nullPort = new NullDeadLetterQueue();
            var controller = NewController(new IDeadLetterQueue[] { nullPort }, messagingEnabled: true);

            Assert.IsType<NotFoundResult>(await controller.Retry(new DeadLetterActionRequest { Id = "any" }));
            Assert.IsType<NotFoundResult>(await controller.Discard(new DeadLetterActionRequest { Id = "any" }));
            Assert.False(nullPort.Touched, "the null port must never be exercised by admin actions");
        }

        private static DeadLetterController NewController(IEnumerable<IDeadLetterQueue> queues, bool messagingEnabled)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Messaging:Enabled"] = messagingEnabled ? "true" : "false"
                })
                .Build();
            return new DeadLetterController(queues, configuration, new RecordingLogManager());
        }

        /// <summary>Carries the REAL fallback's exact type name — the library's NullDeadLetterQueue is internal, and the controller discriminates by name; this double documents that coupling.</summary>
        private sealed class NullDeadLetterQueue : IDeadLetterQueue
        {
            public bool Touched { get; private set; }

            public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default)
            {
                Touched = true;
                return Task.FromResult<IReadOnlyList<DeadLetterMessage>>(new List<DeadLetterMessage>());
            }

            public Task<int> CountAsync(CancellationToken cancellationToken = default)
            {
                Touched = true;
                return Task.FromResult(0);
            }

            public Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default)
            {
                Touched = true;
                return Task.FromResult(false);
            }

            public Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default)
            {
                Touched = true;
                return Task.FromResult(false);
            }
        }

        private sealed class ScriptedQueue : IDeadLetterQueue
        {
            public List<DeadLetterMessage> Messages { get; } = new();
            public List<string> Retried { get; } = new();
            public List<string> Discarded { get; } = new();

            public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Messages.Take(max).ToList());

            public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(Messages.Count);

            public Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default)
            {
                var found = Messages.RemoveAll(m => m.Id == id) > 0;
                if (found)
                {
                    Retried.Add(id);
                }
                return Task.FromResult(found);
            }

            public Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default)
            {
                var found = Messages.RemoveAll(m => m.Id == id) > 0;
                if (found)
                {
                    Discarded.Add(id);
                }
                return Task.FromResult(found);
            }
        }

        private sealed class RecordingLogManager : ILogManager
        {
            public void Log(LogLevel level, object @class, LogFunction function, string message, params object[] args) { }
            public void Log(LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) { }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, string message, params object[] args) { }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) { }
            public void Log(Models.Log log) { }
        }
    }
}
