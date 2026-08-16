namespace LogRadar.Infrastructure.Contracts;

public sealed record LogAggregationResultRow(
    DateTimeOffset Start,
    string? Group,
    long Count
);

public sealed record LogAggregationResult(
    IReadOnlyList<LogAggregationResultRow> Buckets
);
