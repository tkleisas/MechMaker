using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;

namespace MechMaker.Core.Parametric;

/// <summary>
/// Resolves one instance's effective part definition: catalog defaults + derived
/// param expressions + per-instance overrides, evaluated into plain numbers.
/// Shapes, connector poses and params may carry expressions ("length_m/2");
/// everything a consumer (compiler, validator, editor) sees afterwards is
/// numeric — the same code paths as before, just per instance.
/// </summary>
public static class ResolvedParts
{
    public static PartDefinition Materialize(
        PartDefinition definition,
        IReadOnlyDictionary<string, double> instanceParams,
        string instanceId)
    {
        // Params first, in declaration order: catalog numeric defaults, then
        // catalog expressions (each may reference earlier params), then instance
        // overrides (plain numbers) win.
        // Resolution order: catalog numeric defaults, then instance overrides
        // (they win), then derived params recompute from the merged set —
        // pitch_radius follows the instance's teeth/module, not the catalog's.
        var resolved = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in definition.Params)
            resolved[key] = value;
        foreach (var (key, value) in instanceParams)
            resolved[key] = value;
        if (definition.ParamsExpr is { } exprs)
            foreach (var (key, expression) in exprs)
                resolved[key] = Evaluate(expression, resolved, definition.Id, instanceId, $"param '{key}'");

        return new PartDefinition
        {
            Id = definition.Id,
            Name = definition.Name,
            Category = definition.Category,
            Description = definition.Description,
            Bodies = definition.Bodies.Select(b => new PartBody
            {
                Name = b.Name,
                MassKg = b.MassKg,
                ParentBody = b.ParentBody,
                RelativePose = b.RelativePose,
                Actuated = b.Actuated,
                Shapes = b.Shapes.Select(s => new Shape
                {
                    Kind = s.Kind,
                    Extents = s.ExtentsExpr is { } e ? EvalVec(e, resolved, definition.Id, instanceId, $"shape extents") : s.Extents,
                    ExtentsExpr = s.ExtentsExpr,
                    PosExpr = s.PosExpr,
                    RelativePose = s.PosExpr is { } p
                        ? new Pose { Position = EvalVec(p, resolved, definition.Id, instanceId, "shape position"), RotationEulerDeg = s.RelativePose.RotationEulerDeg }
                        : s.RelativePose,
                    Rgba = s.Rgba
                }).ToList()
            }).ToList(),
            Connectors = definition.Connectors.Select(c => new ConnectorDefinition
            {
                Name = c.Name,
                Type = c.Type,
                Body = c.Body,
                Axis = c.Axis,
                Pose = c.PosExpr is { } p
                    ? new Pose { Position = EvalVec(p, resolved, definition.Id, instanceId, $"connector '{c.Name}' position"), RotationEulerDeg = c.Pose.RotationEulerDeg }
                    : c.Pose,
                PosExpr = c.PosExpr
            }).ToList(),
            Motor = definition.Motor,
            Params = resolved,
            IsTransmissionElement = definition.IsTransmissionElement
        };
    }

    private static Vec3 EvalVec(IReadOnlyList<string> expressions, IReadOnlyDictionary<string, double> resolved,
        string catalogId, string instanceId, string what)
    {
        if (expressions.Count != 3)
            throw new ParamEvalException(
                $"Instance '{instanceId}' (catalog '{catalogId}'): position/extents expressions need exactly 3 components, got {expressions.Count}.");
        return new Vec3(
            Evaluate(expressions[0], resolved, catalogId, instanceId, what + " x"),
            Evaluate(expressions[1], resolved, catalogId, instanceId, what + " y"),
            Evaluate(expressions[2], resolved, catalogId, instanceId, what + " z"));
    }

    private static double Evaluate(string expression, IReadOnlyDictionary<string, double> resolved,
        string catalogId, string instanceId, string what)
    {
        try
        {
            return ParamEval.Evaluate(expression, resolved);
        }
        catch (ParamEvalException e)
        {
            throw new ParamEvalException(
                $"Part '{instanceId}' (catalog '{catalogId}') {what}: {e.Message}");
        }
    }
}