namespace LogRadar.Infrastructure.Aggregation;

public sealed class AggregationCacheOptions
{
    public const string SectionName = "AggregationCache";
    public int TtlSeconds { get; init; } = 5;
    public int MemoryMaxEntries { get; init; } = 500;
    public bool RedisEnabled { get; init; }
    public string? RedisConnection { get; init; }
    public string RedisKeyPrefix { get; init; } = "logradar:agg:";
    public int QueryTimeoutSeconds { get; init; } = 8;
}
