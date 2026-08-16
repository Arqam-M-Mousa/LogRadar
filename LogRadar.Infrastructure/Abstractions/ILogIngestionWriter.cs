using LogRadar.Infrastructure.Models;

namespace LogRadar.Infrastructure.Abstractions;

public interface ILogIngestionWriter
{
    ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken);
}
