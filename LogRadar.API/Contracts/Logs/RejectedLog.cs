namespace LogRadar.API.Contracts.Logs;

public sealed class RejectedLog
{
    public int Index { get; init; }

    public string Reason { get; init; } = string.Empty;
}