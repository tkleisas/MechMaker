using Evergine.Bindings.MuJoCo;

namespace MechMaker.Engine;

/// <summary>Structured summary of a compiled model — the seed of the M1+ validation report.</summary>
public sealed record SimulationReport
{
    public double Timestep { get; init; }
    public double TotalMassKg { get; init; }
    public int BodyCount { get; init; }
    public IReadOnlyList<JointInfo> Joints { get; init; } = [];
    public IReadOnlyList<string> Actuators { get; init; } = [];
    public List<string> Notes { get; } = [];

    public sealed record JointInfo(string Name, string Kind, double AxisX, double AxisY, double AxisZ)
    {
        public override string ToString() => $"{Kind} '{Name}' axis [{AxisX:0.##} {AxisY:0.##} {AxisZ:0.##}]";
    }

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Model: {BodyCount} bodies, total mass {TotalMassKg:0.###} kg, timestep {Timestep * 1000:0.#} ms",
            $"Joints: {string.Join(", ", Joints.Select(j => j.ToString()))}",
            $"Actuators: {string.Join(", ", Actuators)}"
        };
        lines.AddRange(Notes.Select(n => $"Note: {n}"));
        return string.Join(Environment.NewLine, lines);
    }
}
