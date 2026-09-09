namespace MechMaker.Core.Model;

/// <summary>One placed part in a machine.</summary>
public sealed record PartInstance
{
    public required string Id { get; init; }
    public required string Part { get; init; }
    public Pose Pose { get; init; } = Pose.Identity;
}

/// <summary>A declared mating between two connectors of two placed parts.</summary>
public sealed record Connection
{
    public required string Id { get; init; }
    public required string PartA { get; init; }
    public required string ConnectorA { get; init; }
    public required string PartB { get; init; }
    public required string ConnectorB { get; init; }
}

/// <summary>A simulated control board ("skr-pico" etc.). Pin map is loose in M0.</summary>
public sealed record BoardDefinition
{
    public required string Id { get; init; }
    public string Type { get; init; } = "skr-pico";
    public Dictionary<string, string> Pins { get; init; } = [];
}

/// <summary>One electrical connection: component signal → board pin.</summary>
public sealed record Wire
{
    public required string Component { get; init; }
    public required string Signal { get; init; }
    public required string Board { get; init; }
    public required string Pin { get; init; }
}

public sealed record MachineDefinition
{
    public const string CurrentSchemaVersion = "0.1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Name { get; init; }
    public List<PartInstance> Parts { get; init; } = [];
    public List<Connection> Connections { get; init; } = [];
    public List<BoardDefinition> Boards { get; init; } = [];
    public List<Wire> Wiring { get; init; } = [];
}
