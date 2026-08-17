// LogRadar.API/Contracts/Logs/LenientStringJsonConverter.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogRadar.API.Contracts.Logs;

/// <summary>
/// Reads a JSON value as a string only when the token really is a string.
/// Any other token (number, bool, object, array) becomes null instead of
/// throwing, so a wrong-typed field in one batch entry can't fail JSON
/// deserialization for the whole /logs request. Downstream per-entry
/// validation then reports it as a normal rejection for that entry.
/// </summary>
public sealed class LenientStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        if (reader.TokenType == JsonTokenType.Null)
            return null;

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}