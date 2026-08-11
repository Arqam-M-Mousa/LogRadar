namespace LogRadar.Application.Contracts;

public sealed record LogMessage(
    DateTimeOffset Timestamp,
    string Level,
    string Service,
    string Message,
    IReadOnlyDictionary<string, object>? Attributes
);