using LogRadar.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace LogRadar.API.Contracts.Logs;

public sealed class AggregateLogsRequest
{
    public string? Service { get; init; }
    public string? Level { get; init; }
    public string? Since { get; init; }
    public string? Until { get; init; }
    public string? Q { get; init; }
    public string? Bucket { get; init; }
    [FromQuery(Name = "group_by")]
    public string? GroupBy { get; init; }
}

public sealed class AggregateLogsResponse
{
    [JsonPropertyName("buckets")]
    public required List<AggregateLogBucket> Buckets { get; init; }
}

public sealed class AggregateLogBucket
{
    [JsonPropertyName("start")]
    public required DateTimeOffset Start { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("count")]
    public required long Count { get; init; }
}

public static class AggregateLogsMappings
{
    private const string AttributePrefix = "attr.";

    public static LogAggregationFilter ToLogAggregationFilter(this AggregateLogsRequest request, IQueryCollection query)
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

    public static AggregateLogsResponse ToAggregateLogsResponse(this LogAggregationResult result) => new()
    {
        Buckets = result.Buckets.Select(bucket => new AggregateLogBucket
        {
            Start = bucket.Start,
            Group = bucket.Group,
            Count = bucket.Count
        }).ToList()
    };
}
