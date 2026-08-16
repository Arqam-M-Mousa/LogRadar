using LogRadar.Application.Contracts;

namespace LogRadar.Application.Abstractions;

public interface ILogIngestionWriter
{
    ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken);
}
