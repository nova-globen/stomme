using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Repository;
using Stomme.Transport.Realtime;
using Microsoft.Extensions.Logging;

namespace Stomme.Tests.Notifications
{
    /// <summary>An in-memory <see cref="INotificationRepository"/> that records the rows a channel writes.</summary>
    internal sealed class RecordingNotificationRepository : INotificationRepository
    {
        private int _nextId = 1;

        public List<Notification> Rows { get; } = [];

        public Exception ThrowOnAdd { get; set; }

        public Task<Notification> AddNotificationAsync(Notification notification)
        {
            if (ThrowOnAdd is not null)
            {
                throw ThrowOnAdd;
            }

            notification.NotificationId = _nextId++;
            Rows.Add(notification);
            return Task.FromResult(notification);
        }

        public Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId) => throw new NotSupportedException();

        public Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId, int count, bool isRead) => throw new NotSupportedException();

        public Task<int> GetNotificationCountAsync(int siteId, int fromUserId, int toUserId, bool isRead) => throw new NotSupportedException();

        public Task<Notification> UpdateNotificationAsync(Notification notification) => throw new NotSupportedException();

        public Task<Notification> GetNotificationAsync(int notificationId) => throw new NotSupportedException();

        public Task<Notification> GetNotificationAsync(int notificationId, bool tracking) => throw new NotSupportedException();

        public Task DeleteNotificationAsync(int notificationId) => throw new NotSupportedException();

        public Task<int> DeleteNotificationsAsync(int siteId, int age) => throw new NotSupportedException();
    }

    /// <summary>A dictionary-backed <see cref="ISiteSecretStore"/>; a key mapped to null reads as Unreadable.</summary>
    internal sealed class FakeSiteSecretStore : ISiteSecretStore
    {
        public Dictionary<(int SiteId, string Key), string> Secrets { get; } = [];

        public HashSet<(int SiteId, string Key)> Unreadable { get; } = [];

        public Task SetAsync(int siteId, string referenceKey, string secret, CancellationToken ct = default)
        {
            Secrets[(siteId, referenceKey)] = secret;
            return Task.CompletedTask;
        }

        public Task<SiteSecretResult> GetAsync(int siteId, string referenceKey, CancellationToken ct = default)
        {
            if (Unreadable.Contains((siteId, referenceKey)))
            {
                return Task.FromResult(SiteSecretResult.Unreadable);
            }

            return Task.FromResult(Secrets.TryGetValue((siteId, referenceKey), out var secret)
                ? SiteSecretResult.Found(secret)
                : SiteSecretResult.NotFound);
        }

        public Task DeleteAsync(int siteId, string referenceKey, CancellationToken ct = default)
        {
            Secrets.Remove((siteId, referenceKey));
            return Task.CompletedTask;
        }
    }

    /// <summary>A fixed per-site host allowlist.</summary>
    internal sealed class FixedWebhookHostAllowlist : IWebhookHostAllowlist
    {
        private readonly IReadOnlyCollection<string> _hosts;

        public FixedWebhookHostAllowlist(params string[] hosts) => _hosts = hosts;

        public Task<IReadOnlyCollection<string>> GetAllowedHostsAsync(int siteId, CancellationToken ct = default) => Task.FromResult(_hosts);
    }

    /// <summary>Captures the in-app messages pushed to the realtime hub.</summary>
    internal sealed class CapturingRealtimePush : INotificationRealtimePush
    {
        public List<NotificationRealtimeMessage> Messages { get; } = [];

        public Task PushAsync(NotificationRealtimeMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>A minimal <see cref="IServiceProvider"/> over a fixed map.</summary>
    internal sealed class MapServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public MapServiceProvider With<T>(T service)
        {
            _services[typeof(T)] = service!;
            return this;
        }

        public object GetService(Type serviceType) => _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    /// <summary>A transport double for a kind and provider key: records sends and returns a fixed result.</summary>
    internal sealed class FakeTransportChannel : INotificationChannel
    {
        public FakeTransportChannel(ChannelKind kind, string providerKey, ChannelDeliveryResult result = null)
        {
            Kind = kind;
            ProviderKey = providerKey;
            Result = result ?? ChannelDeliveryResult.Ok();
        }

        public ChannelKind Kind { get; }

        public string ProviderKey { get; }

        public ChannelDeliveryResult Result { get; set; }

        public List<ChannelDeliveryRequest> Sent { get; } = [];

        public Task<IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult(Result);
        }
    }

    /// <summary>An <see cref="ILogger{T}"/> that keeps every formatted message.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    /// <summary>
    /// An in-test HTTP/1.1 receiver on a raw loopback <see cref="TcpListener"/> (no HttpListener URL ACL, no Kestrel):
    /// records every request it receives and answers with <see cref="Status"/> (plus a Location header when set).
    /// </summary>
    internal sealed class LoopbackWebhookReceiver : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackWebhookReceiver(IPAddress address = null)
        {
            _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Url(string path = "/hook") => $"http://127.0.0.1:{Port}{path}";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Location { get; set; }

        public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

        public sealed record ReceivedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int headerEnd;
                    while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
                    {
                        var read = await stream.ReadAsync(chunk, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        buffer.Write(chunk, 0, read);
                    }

                    var all = buffer.ToArray();
                    var head = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
                    var requestLine = head[0].Split(' ');
                    var headers = head.Skip(1).Where(line => line.Contains(':'))
                        .GroupBy(line => line[..line.IndexOf(':')].Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First()[(group.First().IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                    var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
                    var body = new MemoryStream();
                    body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
                    while (body.Length < length)
                    {
                        var read = await stream.ReadAsync(chunk, _stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        body.Write(chunk, 0, read);
                    }

                    Requests.Enqueue(new ReceivedRequest(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body.ToArray())));

                    var location = Location is null ? string.Empty : $"Location: {Location}\r\n";
                    var response = $"HTTP/1.1 {(int)Status} Test\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n{location}\r\n{{}}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                }
                catch (Exception)
                {
                    // a broken test connection is reported by the asserting test, not here
                }
            }
        }

        private static int IndexOfHeaderEnd(MemoryStream buffer)
        {
            var bytes = buffer.GetBuffer();
            for (var i = 0; i + 3 < buffer.Length; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // stopping
            }

            _stop.Dispose();
        }
    }
}
