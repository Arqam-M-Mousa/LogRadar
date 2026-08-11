namespace LogRadar.API.Contracts.Logs;

public sealed class IngestLogsResponse
{
    public int Accepted { get; init; }

    public List<RejectedLog> Rejected { get; init; } = [];
}