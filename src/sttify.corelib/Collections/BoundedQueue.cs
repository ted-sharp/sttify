using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Collections;

/// <summary>
/// A thread-safe bounded queue that drops oldest items when capacity is exceeded.
/// Optimized for high-throughput audio processing scenarios.
/// </summary>
/// <typeparam name="T">The type of items in the queue</typeparam>
public class BoundedQueue<T> : IDisposable
{
    private readonly object _lockObject = new();
    private readonly ConcurrentQueue<T> _queue = new();
    private volatile int _count;

    public BoundedQueue(int maxCapacity)
    {
        if (maxCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be positive");

        MaxCapacity = maxCapacity;
    }

    public int Count => _count;
    public int MaxCapacity { get; }

    public bool IsFull => _count >= MaxCapacity;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Clear();
        }
    }

    /// <summary>
    /// Attempts to enqueue an item. If the queue is full, drops the oldest item first.
    /// </summary>
    /// <param name="item">The item to enqueue</param>
    /// <returns>True if the item was added, false if the queue is at capacity and oldest item was dropped</returns>
    public bool TryEnqueue(T item)
    {
        lock (_lockObject)
        {
            var wasAtCapacity = _count >= MaxCapacity;

            // Drop oldest items if at capacity
            while (_count >= MaxCapacity && _queue.TryDequeue(out _))
            {
                _count--;
            }

            _queue.Enqueue(item);
            _count++;

            return !wasAtCapacity;
        }
    }

    /// <summary>
    /// Attempts to dequeue an item from the front of the queue.
    /// </summary>
    /// <param name="item">The dequeued item, or default(T) if the queue is empty</param>
    /// <returns>True if an item was dequeued, false if the queue is empty</returns>
    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        lock (_lockObject)
        {
            if (_queue.TryDequeue(out item))
            {
                _count--;
                return true;
            }

            item = default;
            return false;
        }
    }

    /// <summary>
    /// Attempts to peek at the front item without removing it.
    /// </summary>
    /// <param name="item">The front item, or default(T) if the queue is empty</param>
    /// <returns>True if an item was peeked, false if the queue is empty</returns>
    public bool TryPeek([MaybeNullWhen(false)] out T item)
    {
        return _queue.TryPeek(out item);
    }

    /// <summary>
    /// Clears all items from the queue.
    /// </summary>
    public void Clear()
    {
        lock (_lockObject)
        {
            while (_queue.TryDequeue(out _))
            {
                _count--;
            }
            _count = 0;
        }
    }

    /// <summary>
    /// Gets all items currently in the queue without removing them.
    /// This is a snapshot and may not reflect the exact state by the time it's processed.
    /// </summary>
    /// <returns>An array containing all current items</returns>
    public T[] ToArray()
    {
        lock (_lockObject)
        {
            return _queue.ToArray();
        }
    }

    /// <summary>
    /// Drains up to the specified number of items from the queue.
    /// </summary>
    /// <param name="maxItems">Maximum number of items to drain</param>
    /// <returns>A list of drained items</returns>
    public List<T> Drain(int maxItems = int.MaxValue)
    {
        var result = new List<T>();
        var drained = 0;

        lock (_lockObject)
        {
            while (drained < maxItems && _queue.TryDequeue(out var item))
            {
                result.Add(item);
                _count--;
                drained++;
            }
        }

        return result;
    }
}
