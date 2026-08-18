using LogRadar.Domain.Aggregation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace LogRadar.Infrastructure.Aggregation;

public sealed class RedisAggregationCache : IAggregationCache, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MemoryAggregationCache _memory;
    private readonly IConnectionMultiplexer _redis;
    private readonly AggregationCacheOptions _options;
    private readonly ILogger<RedisAggregationCache> _logger;
    private readonly TimeSpan _ttl;

    public RedisAggregationCache(
        MemoryAggregationCache memory,
        IConnectionMultiplexer redis,
        IOptions<AggregationCacheOptions> options,
        ILogger<RedisAggregationCache> logger)
    {
        _memory = memory;
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
        return await _memory.GetOrAddAsync(filter, async (innerFilter, token) =>
        {
            var key = _options.RedisKeyPrefix + MemoryAggregationCache.ComputeKey(innerFilter);

            try
            {
                var db = _redis.GetDatabase();
                var cached = await db.StringGetAsync(key);
                if (cached.HasValue)
                {
                    var payload = JsonSerializer.Deserialize<CachedAggregation>((string)cached!, JsonOptions);
                    if (payload?.Buckets is not null)
                    {
                        return new LogAggregationResult(payload.Buckets
                            .Select(b => new LogAggregationBucket(b.Start, b.Group, b.Count))
                            .ToList());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis aggregation cache read failed; falling back to PostgreSQL");
            }

            var result = await factory(innerFilter, token);

            try
            {
                var db = _redis.GetDatabase();
                var payload = JsonSerializer.Serialize(new CachedAggregation
                {
                    Buckets = result.Buckets
                        .Select(b => new CachedBucket(b.Start, b.Group, b.Count))
                        .ToList()
                }, JsonOptions);

                await db.StringSetAsync(key, payload, _ttl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis aggregation cache write failed");
            }

            return result;
        }, cancellationToken);
    }

    public void Dispose() => _redis.Dispose();

    private sealed class CachedAggregation
    {
        public List<CachedBucket> Buckets { get; init; } = [];
    }

    private sealed record CachedBucket(DateTimeOffset Start, string? Group, long Count);
}
