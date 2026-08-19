using FluentValidation;
using LogRadar.API.Contracts.Aggregation;
using LogRadar.API.Contracts.Ingestion;
using LogRadar.API.Contracts.Query;
using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;
using LogRadar.Domain.Query;
using Microsoft.AspNetCore.Mvc;

namespace LogRadar.API.Controllers;

[ApiController]
[Route("logs")]
public class LogsController : ControllerBase
{
    private readonly IValidator<QueryLogsRequest> _queryValidator;
    private readonly IValidator<AggregateLogsRequest> _aggregateValidator;
    private readonly ILogIngestionService _ingestionService;
    private readonly ILogQueryService _queryService;
    private readonly ILogAggregationService _aggregationService;

    public LogsController(
        IValidator<QueryLogsRequest> queryValidator,
        IValidator<AggregateLogsRequest> aggregateValidator,
        ILogIngestionService ingestionService,
        ILogQueryService queryService,
        ILogAggregationService aggregationService)
    {
        _queryValidator = queryValidator;
        _aggregateValidator = aggregateValidator;
        _ingestionService = ingestionService;
        _queryService = queryService;
        _aggregationService = aggregationService;
    }

    [HttpPost]
    [RequestSizeLimit(4 * 1024 * 1024)]
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
        var validLogs = new List<LogEntry>(request.Logs.Count);
        var maximumAllowedTimestamp = DateTimeOffset.UtcNow.AddMinutes(5);

        for (var index = 0; index < request.Logs.Count; index++)
        {
            var log = request.Logs[index];

            if (!log.TryMap(maximumAllowedTimestamp, out var logEntry, out var rejectionReason))
            {
                rejected.Add(new RejectedLog
                {
                    Index = index,
                    Reason = rejectionReason!
                });

                continue;
            }

            validLogs.Add(logEntry!);
        }

        if (validLogs.Count > 0)
        {
            try
            {
                await _ingestionService.PublishAsync(validLogs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "ingestion temporarily unavailable"
                });
            }
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

        var filter = request.ToFilter(HttpContext.Request.Query);
        var result = await _queryService.QueryAsync(filter, cancellationToken);

        return Ok(result.ToResponse());
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

        var filter = request.ToFilter(HttpContext.Request.Query);
        LogAggregationResult result;
        try
        {
            result = await _aggregationService.AggregateAsync(filter, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "aggregation temporarily unavailable"
            });
        }

        return Ok(result.ToResponse());
    }
}
