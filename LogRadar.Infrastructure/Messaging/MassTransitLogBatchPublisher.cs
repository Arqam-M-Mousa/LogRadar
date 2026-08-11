using LogRadar.Application.Abstractions;
using LogRadar.Application.Contracts;
using MassTransit;

namespace LogRadar.Infrastructure.Messaging;

public sealed class MassTransitLogBatchPublisher : ILogBatchPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitLogBatchPublisher(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task PublishAsync(
        LogIngestedBatch batch,
        CancellationToken cancellationToken)
    {
        return _publishEndpoint.Publish(batch, cancellationToken);
    }
}
