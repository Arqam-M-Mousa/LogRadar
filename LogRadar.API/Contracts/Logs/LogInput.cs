using System.Text.Json;
namespace LogRadar.API.Contracts.Logs;

public sealed class LogInput
{
    public string? Timestamp { get; init; }
    public string? Level { get; init; }
    public string? Service { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, JsonElement>? Attributes { get; init; }

}
