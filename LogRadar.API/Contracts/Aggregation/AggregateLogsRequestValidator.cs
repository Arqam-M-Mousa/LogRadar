using FluentValidation;

namespace LogRadar.API.Contracts.Aggregation;

public sealed class AggregateLogsRequestValidator : AbstractValidator<AggregateLogsRequest>
{
    private static readonly HashSet<string> AllowedLevels = ["debug", "info", "warn", "error"];
    private static readonly HashSet<string> AllowedBuckets = ["1m", "5m", "1h", "1d"];
    private static readonly HashSet<string> AllowedGroupBy = ["service", "level"];

    public AggregateLogsRequestValidator()
    {
        RuleFor(x => x.Level)
            .Must(level => AllowedLevels.Contains(level!))
            .When(x => !string.IsNullOrWhiteSpace(x.Level))
            .WithMessage(x => $"invalid level: '{x.Level}'");

        RuleFor(x => x.Since)
            .Must(BeAValidTimestamp)
            .WithMessage(x => string.IsNullOrWhiteSpace(x.Since) ? "since is required" : $"invalid since: '{x.Since}'");

        RuleFor(x => x.Until)
            .Must(BeAValidTimestamp)
            .WithMessage(x => string.IsNullOrWhiteSpace(x.Until) ? "until is required" : $"invalid until: '{x.Until}'");

        RuleFor(x => x)
            .Must(HaveUntilNotEarlierThanSince)
            .When(x => BeAValidTimestamp(x.Since) && BeAValidTimestamp(x.Until))
            .WithMessage("'until' must not be earlier than 'since'");

        RuleFor(x => x.Bucket)
            .Must(bucket => AllowedBuckets.Contains(bucket!))
            .WithMessage(x => string.IsNullOrWhiteSpace(x.Bucket) ? "bucket is required" : $"invalid bucket: '{x.Bucket}'");

        RuleFor(x => x.GroupBy)
            .Must(groupBy => AllowedGroupBy.Contains(groupBy!))
            .When(x => !string.IsNullOrWhiteSpace(x.GroupBy))
            .WithMessage(x => $"invalid group_by: '{x.GroupBy}'");
    }

    private static bool BeAValidTimestamp(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains('T', StringComparison.Ordinal)
        && DateTimeOffset.TryParse(value, out _);

    private static bool HaveUntilNotEarlierThanSince(AggregateLogsRequest request) =>
        DateTimeOffset.Parse(request.Until!) >= DateTimeOffset.Parse(request.Since!);
}
