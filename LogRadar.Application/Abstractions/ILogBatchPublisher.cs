using LogRadar.Application.Contracts;

namespace LogRadar.Application.Abstractions;

public interface ILogBatchPublisher
{
    Task PublishAsync(
        LogIngestedBatch batch,
        CancellationToken cancellationToken);
}