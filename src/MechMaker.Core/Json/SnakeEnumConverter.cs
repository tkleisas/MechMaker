using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechMaker.Core.Json;

/// <summary>
/// Reads/writes enums as snake_case strings, honoring EnumMember attribute values
/// (which JsonStringEnumConverter ignores when reading).
/// </summary>
public sealed class SnakeEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly Dictionary<string, T> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<T, string> _write = new();

    public SnakeEnumConverter()
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var name = field.GetCustomAttribute<EnumMemberAttribute>()?.Value
                       ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name);
            var value = (T)field.GetValue(null)!;
            _read[name] = value;
            _write[value] = name;
        }
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString()
                   ?? throw new JsonException($"Null is not a valid value for enum {typeof(T).Name}.");
        return _read.TryGetValue(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a valid {typeof(T).Name} (expected one of: {string.Join(", ", _read.Keys)}).");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(_write[value]);
}
