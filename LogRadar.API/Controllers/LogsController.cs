using FluentValidation;
using LogRadar.API.Contracts.Logs.LogAggregate;
using LogRadar.API.Contracts.Logs.LogIngest;
using LogRadar.API.Contracts.Logs.LogQuery;
using LogRadar.Application.Abstractions;
using LogRadar.Application.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogRadar.API.Controllers;

[ApiController]
[Route("logs")]
public class LogsController : ControllerBase
{
    private readonly IValidator<QueryLogsRequest> _queryValidator;
    private readonly IValidator<AggregateLogsRequest> _aggregateValidator;
    private readonly ILogBatchPublisher _publisher;
    private readonly ILogQueryService _queryService;

    public LogsController(IValidator<QueryLogsRequest> queryValidator, IValidator<AggregateLogsRequest> aggregateValidator, ILogBatchPublisher publisher, ILogQueryService queryService)
    {
        _queryValidator = queryValidator;
        _aggregateValidator = aggregateValidator;
        _publisher = publisher;
        _queryService = queryService;
    }

    [HttpPost]
    public async Task<IActionResult> Ingest(IngestLogsRequest request, CancellationToken cancellationToken)
    {
        if (request.Logs is null || request.Logs.Count == 0)
        {
            return BadRequest(new
            {
                error = "logs must contain at least one entry"
            });
        }

        var rejected = new List<RejectedLog>();
        var validLogs = new List<LogMessage>(request.Logs.Count);
        var maximumAllowedTimestamp = DateTimeOffset.UtcNow.AddMinutes(5);

        for (var index = 0; index < request.Logs.Count; index++)
        {
            var log = request.Logs[index];

            if (!log.TryToLogMessage(maximumAllowedTimestamp, out var logMessage, out var rejectionReason))
            {
                rejected.Add(new RejectedLog
                {
                    Index = index,
                    Reason = rejectionReason!
                });

                continue;
            }

            validLogs.Add(logMessage!);
        }

        if (validLogs.Count > 0)
        {
            var batch = new LogIngestedBatch(validLogs);

            await _publisher.PublishAsync(batch, cancellationToken);
        }

        var response = new IngestLogsResponse
        {
            Accepted = validLogs.Count,
            Rejected = rejected
        };

        return validLogs.Count > 0
            ? Ok(response)
            : BadRequest(response);
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] QueryLogsRequest request,
        CancellationToken cancellationToken)
    {
        var validationResult = await _queryValidator.ValidateAsync(request, cancellationToken);

        if (!validationResult.IsValid)
        {
            return BadRequest(new
            {
                error = validationResult.Errors.First().ErrorMessage
            });
        }

        var filter = request.ToLogQueryFilter(HttpContext.Request.Query);
        var result = await _queryService.QueryAsync(filter, cancellationToken);

        return Ok(result.ToQueryLogsResponse());
    }

    [HttpGet("aggregate")]
    public async Task<IActionResult> Aggregate(
        [FromQuery] AggregateLogsRequest request,
        CancellationToken cancellationToken)
    {
        var validationResult = await _aggregateValidator.ValidateAsync(request, cancellationToken);

        if (!validationResult.IsValid)
        {
            return BadRequest(new
            {
                error = validationResult.Errors.First().ErrorMessage
            });
        }

        var filter = request.ToLogAggregationFilter(HttpContext.Request.Query);
        var result = await _queryService.AggregateAsync(filter, cancellationToken);

        return Ok(result.ToAggregateLogsResponse());
    }

}
