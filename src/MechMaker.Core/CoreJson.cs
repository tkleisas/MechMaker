using System.Text.Json;
using System.Text.Json.Serialization;
using MechMaker.Core.Json;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;

namespace MechMaker.Core;

/// <summary>Shared JSON settings: snake_case property names, tolerant of comments and trailing commas.</summary>
public static class CoreJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new SnakeEnumConverter<ConnectorType>(),
            new SnakeEnumConverter<ShapeKind>(),
            new SnakeEnumConverter<PartCategory>(),
            new SnakeEnumConverter<MotorKind>(),
            new SnakeEnumConverter<JointActuation>(),
            new Vec3Converter(),
            new PoseConverter()
        }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("Deserialization produced null.");
}
