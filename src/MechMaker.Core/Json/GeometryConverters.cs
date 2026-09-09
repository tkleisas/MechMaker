using System.Text.Json;
using System.Text.Json.Serialization;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;

namespace MechMaker.Core.Json;

/// <summary>Vec3 as a 3-number JSON array: [x, y, z].</summary>
public sealed class Vec3Converter : JsonConverter<Vec3>
{
    public override Vec3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a [x, y, z] array for a vector.");
        var v = new double[3];
        for (var i = 0; i < 3; i++)
        {
            reader.Read();
            v[i] = reader.GetDouble();
        }
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Expected exactly 3 numbers in a vector array.");
        return new Vec3(v[0], v[1], v[2]);
    }

    public override void Write(Utf8JsonWriter writer, Vec3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

/// <summary>Pose as { "pos": [x,y,z], "rot": [rx,ry,rz] } with both members optional (degrees).</summary>
public sealed class PoseConverter : JsonConverter<Pose>
{
    public override Pose Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for a pose.");
        var position = Vec3.Zero;
        var rotation = Vec3.Zero;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new Pose { Position = position, RotationEulerDeg = rotation };
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "pos":
                    position = new Vec3Converter().Read(ref reader, typeof(Vec3), options);
                    break;
                case "rot":
                    rotation = new Vec3Converter().Read(ref reader, typeof(Vec3), options);
                    break;
                default:
                    throw new JsonException($"Unknown pose member '{property}' (expected 'pos' / 'rot').");
            }
        }
        throw new JsonException("Unterminated pose object.");
    }

    public override void Write(Utf8JsonWriter writer, Pose value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Position != Vec3.Zero)
        {
            writer.WritePropertyName("pos");
            new Vec3Converter().Write(writer, value.Position, options);
        }
        if (value.RotationEulerDeg != Vec3.Zero)
        {
            writer.WritePropertyName("rot");
            new Vec3Converter().Write(writer, value.RotationEulerDeg, options);
        }
        writer.WriteEndObject();
    }
}

public static class JsonConverterExtensions
{
    public static Vec3 ReadVec3(this ref Utf8JsonReader reader, JsonSerializerOptions options)
        => new Vec3Converter().Read(ref reader, typeof(Vec3), options);
}
