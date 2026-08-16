using LogRadar.Infrastructure.Contracts;

namespace LogRadar.Infrastructure.Abstractions;

public interface ILogQueryService
{
    Task<LogQueryResult> QueryAsync(
        LogQueryFilter filter,
        CancellationToken cancellationToken);

    Task<LogAggregationResult> AggregateAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken);
}
