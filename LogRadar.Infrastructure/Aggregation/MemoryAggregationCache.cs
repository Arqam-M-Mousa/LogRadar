using LogRadar.Domain.Aggregation;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LogRadar.Infrastructure.Aggregation;

public sealed class MemoryAggregationCache : IAggregationCache
{
    private readonly ConcurrentDictionary<string, (LogAggregationResult Result, long ExpiryTicks)> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<LogAggregationResult>>> _inFlight = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public MemoryAggregationCache(TimeSpan ttl, int maxEntries = 500)
    {
        _ttl = ttl;
        _maxEntries = Math.Max(16, maxEntries);
    }

    public async Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken)
    {
        var key = ComputeKey(filter);
        var now = Environment.TickCount64;

        if (_cache.TryGetValue(key, out var entry) && now < entry.ExpiryTicks)
            return entry.Result;

        var pending = _inFlight.GetOrAdd(key, _ => new Lazy<Task<LogAggregationResult>>(
            async () =>
            {
                var result = await factory(filter, cancellationToken);
                var expiry = Environment.TickCount64 + (long)_ttl.TotalMilliseconds;

                if (_cache.Count >= _maxEntries)
                    EvictExpired();

                _cache[key] = (result, expiry);
                return result;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await pending.Value;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<LogAggregationResult>>>(key, pending));
        }
    }

    internal static string ComputeKey(LogAggregationFilter filter)
    {
        var sb = new StringBuilder(128);
        sb.Append(filter.Since.ToUnixTimeMilliseconds()).Append('|');
        sb.Append(filter.Until.ToUnixTimeMilliseconds()).Append('|');
        sb.Append(filter.Bucket).Append('|');
        sb.Append(filter.Service ?? "").Append('|');
        sb.Append(filter.Level ?? "").Append('|');
        sb.Append(filter.GroupBy ?? "").Append('|');
        sb.Append(filter.MessageContains ?? "").Append('|');

        if (filter.AttributeFilters.Count > 0)
        {
            foreach (var kv in filter.AttributeFilters.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private void EvictExpired()
    {
        var now = Environment.TickCount64;
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryGetValue(key, out var entry) && now >= entry.ExpiryTicks)
                _cache.TryRemove(key, out _);
        }
    }
}
