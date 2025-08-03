using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Caching;

/// <summary>
/// Thread-safe LRU cache for cloud API responses to reduce latency and costs.
/// Uses content-based hashing for cache keys to handle similar audio segments.
/// </summary>
public class ResponseCache<TResponse> : IDisposable where TResponse : class
{
    private readonly ConcurrentDictionary<string, CacheEntry<TResponse>> _cache = new();
    private readonly ConcurrentQueue<string> _accessOrder = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly Timer _cleanupTimer;
    private readonly object _lockObject = new();
    private volatile bool _disposed;

    public int Count => _cache.Count;
    public int MaxEntries => _maxEntries;

    public ResponseCache(int maxEntries = 1000, TimeSpan? ttl = null)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be positive");

        _maxEntries = maxEntries;
        _ttl = ttl ?? TimeSpan.FromMinutes(30); // Default 30 minute TTL

        // Cleanup expired entries every 5 minutes
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Generates a cache key from audio data using SHA256 hash.
    /// </summary>
    /// <param name="audioData">The audio data to hash</param>
    /// <param name="prefix">Optional prefix for the key</param>
    /// <returns>A cache key string</returns>
    public static string GenerateKey(ReadOnlySpan<byte> audioData, string? prefix = null)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(audioData.ToArray());
        var hashString = Convert.ToHexString(hash);
        
        return string.IsNullOrEmpty(prefix) ? hashString : $"{prefix}:{hashString}";
    }

    /// <summary>
    /// Generates a cache key from text content using SHA256 hash.
    /// </summary>
    /// <param name="content">The text content to hash</param>
    /// <param name="prefix">Optional prefix for the key</param>
    /// <returns>A cache key string</returns>
    public static string GenerateKey(string content, string? prefix = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return GenerateKey(bytes, prefix);
    }

    /// <summary>
    /// Attempts to get a cached response.
    /// </summary>
    /// <param name="key">The cache key</param>
    /// <param name="response">The cached response if found</param>
    /// <returns>True if the response was found and not expired</returns>
    public bool TryGet(string key, [MaybeNullWhen(false)] out TResponse response)
    {
        if (_disposed)
        {
            response = null;
            return false;
        }

        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.CreatedAt <= _ttl)
            {
                // Update access order for LRU
                _accessOrder.Enqueue(key);
                entry.LastAccessed = DateTime.UtcNow;
                
                response = entry.Response;
                
                Telemetry.LogEvent("CacheHit", new { Key = key[..Math.Min(8, key.Length)], Type = typeof(TResponse).Name });
                return true;
            }
            else
            {
                // Entry expired, remove it
                _cache.TryRemove(key, out _);
                Telemetry.LogEvent("CacheExpired", new { Key = key[..Math.Min(8, key.Length)], Type = typeof(TResponse).Name });
            }
        }

        response = null;
        Telemetry.LogEvent("CacheMiss", new { Key = key[..Math.Min(8, key.Length)], Type = typeof(TResponse).Name });
        return false;
    }

    /// <summary>
    /// Stores a response in the cache.
    /// </summary>
    /// <param name="key">The cache key</param>
    /// <param name="response">The response to cache</param>
    public void Set(string key, TResponse response)
    {
        if (_disposed || response == null)
            return;

        var entry = new CacheEntry<TResponse>
        {
            Response = response,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        };

        _cache.AddOrUpdate(key, entry, (_, _) => entry);
        _accessOrder.Enqueue(key);

        // Enforce size limit
        if (_cache.Count > _maxEntries)
        {
            Task.Run(EnforceSizeLimit);
        }

        Telemetry.LogEvent("CacheSet", new { 
            Key = key[..Math.Min(8, key.Length)], 
            Type = typeof(TResponse).Name,
            CacheSize = _cache.Count 
        });
    }

    /// <summary>
    /// Removes an entry from the cache.
    /// </summary>
    /// <param name="key">The cache key to remove</param>
    /// <returns>True if the entry was removed</returns>
    public bool Remove(string key)
    {
        var removed = _cache.TryRemove(key, out _);
        if (removed)
        {
            Telemetry.LogEvent("CacheRemove", new { Key = key[..Math.Min(8, key.Length)], Type = typeof(TResponse).Name });
        }
        return removed;
    }

    /// <summary>
    /// Clears all cached entries.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        
        // Clear access order queue
        while (_accessOrder.TryDequeue(out _)) { }
        
        Telemetry.LogEvent("CacheClear", new { Type = typeof(TResponse).Name, ClearedCount = count });
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>Cache statistics</returns>
    public CacheStatistics GetStatistics()
    {
        var now = DateTime.UtcNow;
        var entries = _cache.Values.ToArray();
        
        return new CacheStatistics
        {
            TotalEntries = entries.Length,
            MaxEntries = _maxEntries,
            ExpiredEntries = entries.Count(e => now - e.CreatedAt > _ttl),
            AverageAge = entries.Length > 0 ? 
                TimeSpan.FromTicks((long)entries.Average(e => (now - e.CreatedAt).Ticks)) : 
                TimeSpan.Zero,
            OldestEntry = entries.Length > 0 ? 
                now - entries.Min(e => e.CreatedAt) : 
                TimeSpan.Zero
        };
    }

    private void EnforceSizeLimit()
    {
        if (_disposed)
            return;

        lock (_lockObject)
        {
            var currentCount = _cache.Count;
            var targetCount = (int)(_maxEntries * 0.8); // Remove 20% when over limit
            var toRemove = currentCount - targetCount;

            if (toRemove <= 0)
                return;

            // Build LRU list from access order
            var keysByAccess = new Dictionary<string, DateTime>();
            
            // Process recent access order
            var recentAccesses = new List<string>();
            while (_accessOrder.TryDequeue(out var key) && recentAccesses.Count < _maxEntries)
            {
                recentAccesses.Add(key);
                if (_cache.TryGetValue(key, out var entry))
                {
                    keysByAccess[key] = entry.LastAccessed;
                }
            }

            // Add any missing keys with their last accessed time
            foreach (var kvp in _cache)
            {
                if (!keysByAccess.ContainsKey(kvp.Key))
                {
                    keysByAccess[kvp.Key] = kvp.Value.LastAccessed;
                }
            }

            // Remove least recently used entries
            var keysToRemove = keysByAccess
                .OrderBy(kvp => kvp.Value)
                .Take(toRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }

            Telemetry.LogEvent("CacheSizeLimitEnforced", new { 
                Type = typeof(TResponse).Name,
                RemovedCount = keysToRemove.Count,
                NewSize = _cache.Count 
            });
        }
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        var expiredKeys = new List<string>();

        foreach (var kvp in _cache)
        {
            if (now - kvp.Value.CreatedAt > _ttl)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            Telemetry.LogEvent("CacheExpiredCleanup", new { 
                Type = typeof(TResponse).Name,
                ExpiredCount = expiredKeys.Count,
                RemainingCount = _cache.Count 
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer.Dispose();
        Clear();
    }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class CacheEntry<T> where T : class
{
    public T Response { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessed { get; set; }
}

[ExcludeFromCodeCoverage] // Simple data container class
public class CacheStatistics
{
    public int TotalEntries { get; set; }
    public int MaxEntries { get; set; }
    public int ExpiredEntries { get; set; }
    public TimeSpan AverageAge { get; set; }
    public TimeSpan OldestEntry { get; set; }
    public double HitRatio { get; set; }
}