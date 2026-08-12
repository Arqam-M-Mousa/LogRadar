using FluentValidation;
using LogRadar.API.Contracts.Logs.LogQuery;

namespace LogRadar.API.Contracts.Logs.Validation;

public sealed class QueryLogsRequestValidator : AbstractValidator<QueryLogsRequest>
{
    private static readonly HashSet<string> AllowedLevels =
    ["debug", "info", "warn", "error"];

    public QueryLogsRequestValidator()
    {
        RuleFor(x => x.Level)
            .Must(level => AllowedLevels.Contains(level!))
            .When(x => !string.IsNullOrWhiteSpace(x.Level))
            .WithMessage(x => $"invalid level: '{x.Level}'");

        RuleFor(x => x.Since)
            .Must(BeAValidTimestamp)
            .When(x => !string.IsNullOrWhiteSpace(x.Since))
            .WithMessage(x => $"invalid since: '{x.Since}'");

        RuleFor(x => x.Until)
            .Must(BeAValidTimestamp)
            .When(x => !string.IsNullOrWhiteSpace(x.Until))
            .WithMessage(x => $"invalid until: '{x.Until}'");

        RuleFor(x => x)
            .Must(HaveUntilNotEarlierThanSince)
            .When(x => BeAValidTimestamp(x.Since) && BeAValidTimestamp(x.Until))
            .WithMessage("'until' must not be earlier than 'since'");

        RuleFor(x => x.Limit)
            .Must(BeAValidLimit)
            .When(x => !string.IsNullOrWhiteSpace(x.Limit))
            .WithMessage("limit must be a number between 1 and 1000");


        RuleFor(x => x.Cursor)
            .Must(cursor => LogCursor.TryDecode(cursor, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.Cursor))
            .WithMessage("invalid cursor");
    }

    private static bool BeAValidTimestamp(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains('T', StringComparison.Ordinal)
        && DateTimeOffset.TryParse(value, out _);


    private static bool HaveUntilNotEarlierThanSince(QueryLogsRequest request)
    {
        var since = DateTimeOffset.Parse(request.Since!);
        var until = DateTimeOffset.Parse(request.Until!);
        return until >= since;
    }

    private static bool BeAValidLimit(string? value)
    {
        if (!int.TryParse(value, out var limit))
            return false;

        return limit is > 0 and <= 1000;
    }




}
