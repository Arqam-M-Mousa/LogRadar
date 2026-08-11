using FluentValidation;
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
            .WithMessage("timestamp must be a valid ISO 8601 timestamp");

        RuleFor(x => x.Timestamp)
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

        RuleForEach(x => x.Attributes)
            .Must(IsSupportedAttribute)
            .WithMessage("attribute values must be strings, numbers, or booleans");
    }

    private static bool BeValidTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            out _);
    }

    private static bool NotMoreThanFiveMinutesInFuture(string? value)
    {
        if (!DateTimeOffset.TryParse(value, out var timestamp))
            return false;

        return timestamp <= DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static bool IsSupportedAttribute(
        KeyValuePair<string, JsonElement> attribute)
    {
        return attribute.Value.ValueKind switch
        {
            JsonValueKind.String => true,
            JsonValueKind.Number => true,
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            _ => false
        };
    }
}