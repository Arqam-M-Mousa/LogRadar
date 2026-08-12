using LogRadar.Application.Contracts;

namespace LogRadar.API.Contracts.Logs.LogQuery;

public static class QueryLogsMappings
{
    private const string AttributePrefix = "attr.";

    public static LogQueryFilter ToLogQueryFilter(this QueryLogsRequest request, IQueryCollection query)
    {
        LogCursor.TryDecode(request.Cursor, out var cursor);

        var attributeFilters = query
            .Where(parameter => parameter.Key.StartsWith(AttributePrefix, StringComparison.Ordinal))
            .ToDictionary(
                parameter => parameter.Key[AttributePrefix.Length..],
                parameter => parameter.Value.ToString(),
                StringComparer.Ordinal);

        return new LogQueryFilter(
            request.Service,
            request.Level,
            ParseTimestamp(request.Since),
            ParseTimestamp(request.Until),
            request.Q,
            attributeFilters,
            string.IsNullOrWhiteSpace(request.Limit) ? 100 : int.Parse(request.Limit),
            cursor?.Timestamp,
            cursor?.Id);
    }

    public static QueryLogsResponse ToQueryLogsResponse(this LogQueryResult result)
    {
        var logs = result.Logs.Select(log => new QueryLogResultItem
        {
            Id = log.Id.ToString(),
            Timestamp = log.Timestamp,
            Level = log.Level,
            Service = log.Service,
            Message = log.Message,
            Attributes = log.Attributes?.ToDictionary(attribute => attribute.Key, attribute => attribute.Value)
        }).ToList();

        var nextCursor = result.HasMore && result.Logs.Count > 0
            ? new LogCursor(result.Logs[^1].Timestamp, result.Logs[^1].Id).Encode()
            : null;

        return new QueryLogsResponse { Logs = logs, NextCursor = nextCursor };
    }

    private static DateTimeOffset? ParseTimestamp(string? timestamp) =>
        string.IsNullOrWhiteSpace(timestamp) ? null : DateTimeOffset.Parse(timestamp);
}
