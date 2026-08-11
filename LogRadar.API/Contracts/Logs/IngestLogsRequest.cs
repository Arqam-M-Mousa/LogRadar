namespace LogRadar.API.Contracts.Logs;

public sealed class IngestLogsRequest
{
    public List<LogInput>? Logs { get; init; } = [];
}
