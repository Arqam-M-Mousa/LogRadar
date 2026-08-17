namespace LogRadar.Domain.Aggregation;

public interface ILogAggregationService
{
    Task<LogAggregationResult> AggregateAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken);
}
