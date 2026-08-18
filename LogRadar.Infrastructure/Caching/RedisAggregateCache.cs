using LogRadar.Domain.Aggregation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogRadar.Infrastructure.Caching;

public sealed class RedisAggregateCache : IAggregateCache, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnectionMultiplexer _redis;
    private readonly AggregateCacheOptions _options;
    private readonly ILogger<RedisAggregateCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Lazy<Task<LogAggregationResult>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, LocalEntry> _local = new();
    private const int LocalCapacity = 128;
    private static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(2);

    public RedisAggregateCache(
        IConnectionMultiplexer redis,
        IOptions<AggregateCacheOptions> options,
        ILogger<RedisAggregateCache> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
        _ttl = TimeSpan.FromSeconds(Math.Max(1, _options.TtlSeconds));
    }

    public async Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken)
    {
        var key = CreateKey(filter);
        if (_local.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Result;

        _local.TryRemove(key, out _);
        var pending = _inFlight.GetOrAdd(key, _ => new Lazy<Task<LogAggregationResult>>(
            () => ReadOrCreateAsync(key, filter, factory, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await pending.Value;
            AddLocal(key, result);
            return result;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<LogAggregationResult>>>(key, pending));
        }
    }

    public void Dispose() => _redis.Dispose();

    private async Task<LogAggregationResult> ReadOrCreateAsync(
        string key,
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken)
    {
        var redisKey = _options.RedisKeyPrefix + key;

        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync(redisKey);
            if (cached.HasValue)
            {
                var payload = JsonSerializer.Deserialize<CachedAggregation>((string)cached!, JsonOptions);
                if (payload?.Buckets is not null)
                {
                    return new LogAggregationResult(payload.Buckets
                        .Select(bucket => new LogAggregationBucket(bucket.Start, bucket.Group, bucket.Count))
                        .ToList());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis aggregation cache read failed; falling back to PostgreSQL");
        }

        var result = await factory(filter, cancellationToken);

        try
        {
            var payload = JsonSerializer.Serialize(new CachedAggregation
            {
                Buckets = result.Buckets
                    .Select(bucket => new CachedBucket(bucket.Start, bucket.Group, bucket.Count))
                    .ToList()
            }, JsonOptions);

            await _redis.GetDatabase().StringSetAsync(redisKey, payload, _ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis aggregation cache write failed");
        }

        return result;
    }

    private static string CreateKey(LogAggregationFilter filter)
    {
        var value = new StringBuilder(128)
            .Append(filter.Since.ToUnixTimeMilliseconds()).Append('|')
            .Append(filter.Until.ToUnixTimeMilliseconds()).Append('|')
            .Append(filter.Bucket).Append('|')
            .Append(filter.Service ?? "").Append('|')
            .Append(filter.Level ?? "").Append('|')
            .Append(filter.GroupBy ?? "").Append('|')
            .Append(filter.MessageContains ?? "").Append('|');

        foreach (var filterValue in filter.AttributeFilters.OrderBy(x => x.Key, StringComparer.Ordinal))
            value.Append(filterValue.Key).Append('=').Append(filterValue.Value).Append(';');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private void AddLocal(string key, LogAggregationResult result)
    {
        if (_local.Count >= LocalCapacity)
            _local.TryRemove(_local.Keys.FirstOrDefault() ?? key, out _);

        _local[key] = new LocalEntry(result, DateTimeOffset.UtcNow.Add(LocalTtl));
    }

    private sealed record LocalEntry(LogAggregationResult Result, DateTimeOffset ExpiresAt);

    private sealed class CachedAggregation
    {
        public List<CachedBucket> Buckets { get; init; } = [];
    }

    private sealed record CachedBucket(DateTimeOffset Start, string? Group, long Count);
}
