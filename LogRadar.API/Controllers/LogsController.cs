using FluentValidation;
using LogRadar.API.Contracts.Logs;
using Microsoft.AspNetCore.Mvc;

namespace LogRadar.API.Controllers;

[ApiController]
[Route("logs")]
public class LogsController : ControllerBase
{
    private readonly IValidator<LogInput> _validator;

    public LogsController(IValidator<LogInput> validator)
    {
        _validator = validator;
    }

    [HttpPost]
    public IActionResult Ingest(IngestLogsRequest request)
    {
        if (request.Logs is null || request.Logs.Count == 0)
        {
            return BadRequest(new
            {
                error = "logs must contain at least one entry"
            });
        }

        var rejected = new List<RejectedLog>();
        var accepted = 0;

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

            accepted++;
        }

        var response = new IngestLogsResponse
        {
            Accepted = accepted,
            Rejected = rejected
        };

        return accepted > 0
            ? Ok(response)
            : BadRequest(response);
    }

}
