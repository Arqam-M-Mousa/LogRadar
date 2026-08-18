using LogRadar.Domain.Aggregation;

namespace LogRadar.Infrastructure.Aggregation;

public sealed class PassthroughAggregationCache : IAggregationCache
{
    public Task<LogAggregationResult> GetOrAddAsync(
        LogAggregationFilter filter,
        Func<LogAggregationFilter, CancellationToken, Task<LogAggregationResult>> factory,
        CancellationToken cancellationToken) => factory(filter, cancellationToken);
}
