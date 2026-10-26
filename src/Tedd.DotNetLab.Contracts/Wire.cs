using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Contracts;

public sealed class LabException(string code, string message, string? recovery = null) : Exception(message)
{
    public string Code { get; } = code;
    public string Recovery { get; } = recovery ?? RecoveryHints.ForCode(code);
}

public static class Wire
{
    public const string Version = "1.0";
    public static JsonSerializerOptions Json { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false));
        options.Converters.Add(new ExactInt64Converter());
        options.Converters.Add(new ExactUInt64Converter());
        return options;
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new LabException("invalid_request", "Null document.");
    public static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
    public static string Hash<T>(T value)
    {
        using var document = JsonDocument.Parse(Serialize(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Canonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Canonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}

public sealed class ExactInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.String)
        {
            string text=reader.GetString()!;
            if(long.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out long exact)&&
                exact.ToString(CultureInfo.InvariantCulture)==text) return exact;
            throw new JsonException("Expected a canonical decimal Int64 string.");
        }
        if(reader.TokenType==JsonTokenType.Number&&reader.TryGetInt64(out long value)&&value is >=-9007199254740991 and <=9007199254740991)
            return value;
        throw new JsonException("Int64 values outside the exact JSON number range require decimal strings.");
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class ExactUInt64Converter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.String)
        {
            string text=reader.GetString()!;
            if(ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out ulong exact)&&
                exact.ToString(CultureInfo.InvariantCulture)==text) return exact;
            throw new JsonException("Expected a canonical decimal UInt64 string.");
        }
        if(reader.TokenType==JsonTokenType.Number&&reader.TryGetUInt64(out ulong value)&&value<=9007199254740991) return value;
        throw new JsonException("UInt64 values outside the exact JSON number range require decimal strings.");
    }
    public override void Write(Utf8JsonWriter writer,ulong value,JsonSerializerOptions options)=>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
