namespace LogRadar.Application.Contracts;

public sealed record LogIngestedBatch(
    IReadOnlyList<LogMessage> Logs
);