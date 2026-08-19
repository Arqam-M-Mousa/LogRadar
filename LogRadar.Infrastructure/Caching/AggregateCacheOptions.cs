namespace LogRadar.Infrastructure.Caching;

public sealed class AggregateCacheOptions
{
    public const string SectionName = "AggregationCache";
    public int TtlSeconds { get; init; } = 30;
    public bool RedisEnabled { get; init; }
    public string? RedisConnection { get; init; }
    public string RedisKeyPrefix { get; init; } = "logradar:agg:";
    public int QueryTimeoutSeconds { get; init; } = 8;
}
