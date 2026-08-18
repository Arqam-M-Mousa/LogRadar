using LogRadar.Domain.Aggregation;

namespace LogRadar.Infrastructure.Aggregation;

public interface IAggregationCache
{
    Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken);
}
