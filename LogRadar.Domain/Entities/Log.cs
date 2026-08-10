using LogRadar.Domain.Enums;

namespace LogRadar.Domain.Entities;

public class Log
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Service { get; set; } = null!;
    public string Message { get; set; } = null!;
    public Dictionary<string, object>? Attributes { get; set; }
}
