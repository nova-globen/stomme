using System.Net.Sockets;
using Stomme.EventBus;

namespace Stomme.MessageBus.Live.Tests;

/// <summary>The integration event round-tripped over each durable transport.</summary>
public sealed record InvoiceIssued(Guid InvoiceId, decimal Amount) : IntegrationEvent;

/// <summary>Captures the delivery so the test can await it deterministically.</summary>
public sealed class DeliverySink
{
    private readonly TaskCompletionSource<InvoiceIssued> _received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<InvoiceIssued> Received => _received.Task;

    public void Record(InvoiceIssued e) => _received.TrySetResult(e);
}

/// <summary>The consuming module's handler — idempotent, as at-least-once delivery requires.</summary>
public sealed class InvoiceIssuedConsumer(DeliverySink sink) : IIntegrationEventHandler<InvoiceIssued>
{
    public Task HandleAsync(InvoiceIssued @event, CancellationToken ct = default)
    {
        sink.Record(@event);
        return Task.CompletedTask;
    }
}

/// <summary>Reachability probes so a missing broker/server skips rather than fails the suite.</summary>
internal static class Probe
{
    public static bool TcpOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static bool HttpOk(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return http.GetAsync(url).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
