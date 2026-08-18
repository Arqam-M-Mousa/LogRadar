using LogRadar.Domain.Aggregation;

namespace LogRadar.Infrastructure.Caching;

public sealed class NoopAggregateCache : IAggregateCache
{
    public Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken) => factory(filter, cancellationToken);
}
