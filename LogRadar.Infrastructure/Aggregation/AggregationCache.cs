using LogRadar.Domain.Aggregation;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LogRadar.Infrastructure.Aggregation;
  

public sealed class AggregationCache
{
    private readonly ConcurrentDictionary<string, (LogAggregationResult Result, long ExpiryTicks)> _cache = new();
    private readonly TimeSpan _ttl;
    private const int MaxEntries = 500;

    public AggregationCache(TimeSpan ttl)
    {
        _ttl = ttl;
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

        var result = await factory(filter, cancellationToken);
        var expiry = now + _ttl.TotalMilliseconds;

        if (_cache.Count >= MaxEntries)
            EvictExpired();

        _cache[key] = (result, (long)expiry);
        return result;
    }

    private static string ComputeKey(LogAggregationFilter filter)
    {
        var sb = new StringBuilder();
        sb.Append(filter.Since.ToUnixTimeMilliseconds()).Append('|');
        sb.Append(filter.Until.ToUnixTimeMilliseconds()).Append('|');
        sb.Append(filter.Bucket).Append('|');
        sb.Append(filter.Service ?? "").Append('|');
        sb.Append(filter.Level ?? "").Append('|');
        sb.Append(filter.GroupBy ?? "").Append('|');
        sb.Append(filter.MessageContains ?? "").Append('|');

        if (filter.AttributeFilters.Count > 0)
        {
            foreach (var kv in filter.AttributeFilters.OrderBy(x => x.Key))
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
