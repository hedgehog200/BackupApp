using System.Text.Json;
using System.Text.Json.Serialization;
using BackupApp.Models;

namespace BackupApp.Utils;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ScheduleTypeJsonConverter() }
    };
}

public class ScheduleTypeJsonConverter : JsonConverter<ScheduleType>
{
    public override ScheduleType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return Enum.Parse<ScheduleType>(reader.GetString()!, ignoreCase: true);
        return (ScheduleType)reader.GetInt32();
    }

    public override void Write(Utf8JsonWriter writer, ScheduleType value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue((int)value);
    }
}
