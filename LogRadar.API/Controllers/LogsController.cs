using FluentValidation;
using LogRadar.API.Contracts.Logs;
using LogRadar.Application.Abstractions;
using LogRadar.Application.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogRadar.API.Controllers;

[ApiController]
[Route("logs")]
public class LogsController : ControllerBase
{
    private readonly IValidator<LogInput> _validator;
    private readonly ILogBatchPublisher _publisher;

    public LogsController(IValidator<LogInput> validator, ILogBatchPublisher publisher)
    {
        _validator = validator;
        _publisher = publisher;
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
        var validLogs = new List<LogMessage>();

        for (var index = 0; index < request.Logs.Count; index++)
        {
            var log = request.Logs[index];

            var validationResult = _validator.Validate(log);

            if (!validationResult.IsValid)
            {
                rejected.Add(new RejectedLog
                {
                    Index = index,
                    Reason = string.Join(
                        "; ",
                        validationResult.Errors.Select(x => x.ErrorMessage))
                });

                continue;
            }

            validLogs.Add(log.ToLogMessage());
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

}
