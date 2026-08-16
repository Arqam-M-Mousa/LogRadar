using LogRadar.Application.Contracts;

namespace LogRadar.Application.Abstractions;

/// <summary>
/// Accepts validated logs for asynchronous persistence.
/// </summary>
public interface ILogIngestionWriter
{
    ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken);
}
