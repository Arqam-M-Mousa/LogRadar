using System.Text.Json;

namespace LogRadar.API.Contracts.Logs.LogIngest;

public sealed class IngestLogsRequest
{
    public List<LogInput>? Logs { get; init; } = [];
}

public sealed class LogInput
{
    public string? Timestamp { get; init; }
    public string? Level { get; init; }
    public string? Service { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

public sealed class IngestLogsResponse
{
    public int Accepted { get; init; }
    public List<RejectedLog> Rejected { get; init; } = [];
}

public sealed class RejectedLog
{
    public int Index { get; init; }
    public string Reason { get; init; } = string.Empty;
}
