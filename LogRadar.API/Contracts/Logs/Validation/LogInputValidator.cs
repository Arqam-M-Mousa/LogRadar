using FluentValidation;
using LogRadar.API.Contracts.Logs.LogIngest;
using System.Text.Json;

namespace LogRadar.API.Contracts.Logs.Validation;

public sealed class LogInputValidator : AbstractValidator<LogInput>
{
    private static readonly HashSet<string> AllowedLevels =
    [
        "debug",
        "info",
        "warn",
        "error"
    ];

    public LogInputValidator()
    {
        RuleFor(x => x.Timestamp)
            .NotEmpty()
            .WithMessage("timestamp is required")
            .Must(BeValidTimestamp)
            .When(x => !string.IsNullOrWhiteSpace(x.Timestamp))
            .WithMessage("timestamp must be a valid ISO 8601 timestamp")
            .Must(NotMoreThanFiveMinutesInFuture)
            .When(x => BeValidTimestamp(x.Timestamp))
            .WithMessage("timestamp must not be more than five minutes in the future");

        RuleFor(x => x.Level)
            .NotEmpty()
            .WithMessage("level is required")
            .Must(level => AllowedLevels.Contains(level!))
            .When(x => !string.IsNullOrWhiteSpace(x.Level))
            .WithMessage(x => $"invalid level: '{x.Level}'");

        RuleFor(x => x.Service)
            .NotEmpty()
            .WithMessage("service is required");

        RuleFor(x => x.Message)
            .NotEmpty()
            .WithMessage("message is required");

        RuleFor(x => x.Attributes)
            .Must(HaveOnlySupportedAttributeValues)
            .WithMessage("attribute values must be strings, numbers, or booleans")
            .When(x => x.Attributes is { Count: > 0 });
    }

    private static bool BeValidTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, out _);
    }

    private static bool NotMoreThanFiveMinutesInFuture(string? value)
    {
        if (!DateTimeOffset.TryParse(value, out var timestamp))
            return false;

        return timestamp <= DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static bool HaveOnlySupportedAttributeValues(
        Dictionary<string, JsonElement>? attributes)
    {
        if (attributes is null)
            return true;

        foreach (var kv in attributes)
        {
            var kind = kv.Value.ValueKind;

            if (kind != JsonValueKind.String &&
                kind != JsonValueKind.Number &&
                kind != JsonValueKind.True &&
                kind != JsonValueKind.False)
            {
                return false;
            }
        }

        return true;
    }
}