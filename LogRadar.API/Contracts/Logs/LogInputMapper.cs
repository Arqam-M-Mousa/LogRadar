using LogRadar.Application.Contracts;

namespace LogRadar.API.Contracts.Logs;

public static class LogInputMapper
{
    public static LogMessage ToLogMessage(this LogInput input)
    {
        var attributes = input.Attributes?.ToDictionary(
            kv => kv.Key,
            kv => (object)(kv.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => kv.Value.GetString()!,
                System.Text.Json.JsonValueKind.Number => kv.Value.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => throw new InvalidOperationException("unsupported attribute value")
            }));

        return new LogMessage(
            DateTimeOffset.Parse(input.Timestamp!),
            input.Level!,
            input.Service!,
            input.Message!,
            attributes);
    }
}
