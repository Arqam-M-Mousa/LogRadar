using LogRadar.Domain.Aggregation;

namespace LogRadar.API.Contracts.Aggregation;
  

public static class AggregateLogsMappings
{
    private const string AttributePrefix = "attr.";

    public static LogAggregationFilter ToFilter(this AggregateLogsRequest request, IQueryCollection query)
    {
        var attributeFilters = query
            .Where(parameter => parameter.Key.StartsWith(AttributePrefix, StringComparison.Ordinal))
            .ToDictionary(
                parameter => parameter.Key[AttributePrefix.Length..],
                parameter => parameter.Value.ToString(),
                StringComparer.Ordinal);

        return new LogAggregationFilter(
            request.Service,
            request.Level,
            DateTimeOffset.Parse(request.Since!),
            DateTimeOffset.Parse(request.Until!),
            request.Q,
            attributeFilters,
            request.Bucket!,
            request.GroupBy);
    }

    public static AggregateLogsResponse ToResponse(this LogAggregationResult result) => new()
    {
        Buckets = result.Buckets.Select(bucket => new AggregateLogBucket
        {
            Start = bucket.Start,
            Group = bucket.Group,
            Count = bucket.Count
        }).ToList()
    };
}
