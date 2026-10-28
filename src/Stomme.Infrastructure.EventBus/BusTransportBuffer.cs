using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Thread-safe bounded queue of <see cref="BusBufferedMessage"/> items captured while the bus
/// transport is unreachable. <see cref="TryEnqueue"/> returns <see langword="false"/> (caller
/// should surface an error) once <see cref="Capacity"/> is reached; messages are drained
/// FIFO by <see cref="BusTransportFlusher"/> when transport connectivity resumes.
/// </summary>
internal sealed class BusTransportBuffer
{
    private readonly ConcurrentQueue<BusBufferedMessage> _queue = new();
    private int _count;

    public BusTransportBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => Volatile.Read(ref _count);

    public bool IsFull => Volatile.Read(ref _count) >= Capacity;

    public bool TryEnqueue(BusBufferedMessage message)
    {
        if (Volatile.Read(ref _count) >= Capacity)
        {
            return false;
        }
        _queue.Enqueue(message);
        Interlocked.Increment(ref _count);
        return true;
    }

    public bool TryPeek([NotNullWhen(true)] out BusBufferedMessage? message) =>
        _queue.TryPeek(out message);

    public bool TryDequeue([NotNullWhen(true)] out BusBufferedMessage? message)
    {
        if (_queue.TryDequeue(out message))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }
}
