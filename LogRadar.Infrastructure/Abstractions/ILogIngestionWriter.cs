using LogRadar.Infrastructure.Contracts;

namespace LogRadar.Infrastructure.Abstractions;

public interface ILogIngestionWriter
{
    ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken);
}
