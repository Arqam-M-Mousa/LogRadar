namespace LogRadar.Infrastructure.Contracts;

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
