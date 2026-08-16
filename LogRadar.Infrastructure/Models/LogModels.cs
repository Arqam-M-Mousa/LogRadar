namespace LogRadar.Infrastructure.Models;

public sealed record LogMessage(
    DateTimeOffset Timestamp,
    string Level,
    string Service,
    string Message,
    IReadOnlyDictionary<string, object>? Attributes
);

public sealed record LogQueryFilter(
    string? Service,
    string? Level,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? MessageContains,
    IReadOnlyDictionary<string, string> AttributeFilters,
    int Limit,
    DateTimeOffset? CursorTimestamp,
    long? CursorId
);

public sealed record LogQueryResultRow(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Service,
    string Message,
    IReadOnlyDictionary<string, object>? Attributes
);

public sealed record LogQueryResult(
    IReadOnlyList<LogQueryResultRow> Logs,
    bool HasMore
);

public sealed record LogAggregationFilter(
    string? Service,
    string? Level,
    DateTimeOffset Since,
    DateTimeOffset Until,
    string? MessageContains,
    IReadOnlyDictionary<string, string> AttributeFilters,
    string Bucket,
    string? GroupBy
);

public sealed record LogAggregationResultRow(
    DateTimeOffset Start,
    string? Group,
    long Count
);

public sealed record LogAggregationResult(
    IReadOnlyList<LogAggregationResultRow> Buckets
);
