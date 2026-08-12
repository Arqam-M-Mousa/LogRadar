namespace LogRadar.Application.Contracts;

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