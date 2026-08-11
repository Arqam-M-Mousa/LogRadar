using LogRadar.Application.Contracts;

namespace LogRadar.API.Contracts.Logs;

public static class LogInputMapper
{
    public static LogMessage ToLogMessage(this LogInput input)
    {
        Dictionary<string, object>? attributes = null;

        if (input.Attributes is { Count: > 0 })
        {
            attributes = new Dictionary<string, object>(input.Attributes.Count);
            foreach (var kv in input.Attributes)
            {
                attributes[kv.Key] = kv.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => kv.Value.GetString()!,
                    System.Text.Json.JsonValueKind.Number => kv.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    _ => throw new InvalidOperationException("unsupported attribute value")
                };
            }
        }

        return new LogMessage(
            DateTimeOffset.Parse(input.Timestamp!),
            input.Level!,
            input.Service!,
            input.Message!,
            attributes);
    }
}
