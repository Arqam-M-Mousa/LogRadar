using LogRadar.Domain.Aggregation;

namespace LogRadar.Infrastructure.Caching;

public interface IAggregateCache
{
    Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken);
}
