using LogRadar.Application.Contracts;

namespace LogRadar.API.Contracts.Logs.LogIngest;

public static class LogInputMapper
{
    private static readonly HashSet<string> AllowedLevels =
    ["debug", "info", "warn", "error"];

    public static bool TryToLogMessage(
        this LogInput? input,
        DateTimeOffset maximumAllowedTimestamp,
        out LogMessage? logMessage,
        out string? rejectionReason)
    {
        logMessage = null;
        rejectionReason = null;

        if (input is null)
        {
            rejectionReason = "log entry is required";
            return false;
        }

        if (!TryParseTimestamp(input.Timestamp, out var timestamp))
        {
            rejectionReason = string.IsNullOrWhiteSpace(input.Timestamp)
                ? "timestamp is required"
                : "timestamp must be a valid ISO 8601 timestamp";
            return false;
        }

        if (timestamp > maximumAllowedTimestamp)
        {
            rejectionReason = "timestamp must not be more than five minutes in the future";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.Level))
        {
            rejectionReason = "level is required";
            return false;
        }

        if (!AllowedLevels.Contains(input.Level))
        {
            rejectionReason = $"invalid level: '{input.Level}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.Service))
        {
            rejectionReason = "service is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.Message))
        {
            rejectionReason = "message is required";
            return false;
        }

        Dictionary<string, object>? attributes = null;

        if (input.Attributes is { } attributesElement)
        {
            if (attributesElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                rejectionReason = "attributes must be a flat object with string, number, or boolean values";
                return false;
            }

            attributes = new Dictionary<string, object>();

            foreach (var property in attributesElement.EnumerateObject())
            {
                if (!TryGetAttributeValue(property.Value, out var value))
                {
                    rejectionReason = "attributes must be a flat object with string, number, or boolean values";
                    return false;
                }

                attributes[property.Name] = value;
            }
        }

        logMessage = new LogMessage(
            timestamp,
            input.Level!,
            input.Service!,
            input.Message!,
            attributes);

        return true;
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('T', StringComparison.Ordinal))
        {
            timestamp = default;
            return false;
        }

        if (!DateTimeOffset.TryParse(value, out timestamp))
            return false;

        timestamp = timestamp.ToUniversalTime();
        return true;
    }

    private static bool TryGetAttributeValue(System.Text.Json.JsonElement element, out object value)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                value = element.GetString()!;
                return true;
            case System.Text.Json.JsonValueKind.Number:
                value = GetNumber(element);
                return true;
            case System.Text.Json.JsonValueKind.True:
                value = true;
                return true;
            case System.Text.Json.JsonValueKind.False:
                value = false;
                return true;
            default:
                value = null!;
                return false;
        }
    }

    private static object GetNumber(System.Text.Json.JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
            return integer;

        if (value.TryGetDecimal(out var decimalValue))
            return decimalValue;

        return value.GetDouble();
    }
}
