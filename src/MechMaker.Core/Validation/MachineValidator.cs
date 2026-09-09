using MechMaker.Core.Model;

namespace MechMaker.Core.Validation;

/// <summary>Static checks on a machine definition against a catalog. Produces machine-readable diagnostics.</summary>
public sealed class MachineValidator(PartCatalog catalog)
{
    public ValidationReport Validate(MachineDefinition machine)
    {
        var report = new ValidationReport();

        if (!string.Equals(machine.SchemaVersion, MachineDefinition.CurrentSchemaVersion, StringComparison.Ordinal))
            report.AddWarning("mm022",
                $"Schema version '{machine.SchemaVersion}' differs from supported '{MachineDefinition.CurrentSchemaVersion}'.");

        CheckParts(machine, report);
        CheckConnections(machine, report);
        CheckWiring(machine, report);
        CheckConnectivity(machine, report);
        CheckBelts(machine, report);

        return report;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeId =
        new("^[A-Za-z_][A-Za-z0-9_.-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void CheckParts(MachineDefinition machine, ValidationReport report)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in machine.Parts)
        {
            if (!seenIds.Add(instance.Id))
                report.AddError("mm001", $"Duplicate part instance id '{instance.Id}'.", instance.Id);

            if (!SafeId.IsMatch(instance.Id))
                report.AddError("mm024",
                    $"Part instance id '{instance.Id}' may only contain letters, digits, '_' and '-'.", instance.Id);

            if (!catalog.TryGet(instance.Part, out var definition) || definition is null)
            {
                report.AddError("mm002", $"Part instance '{instance.Id}' references unknown catalog part '{instance.Part}'.", instance.Id);
                continue;
            }

            if (definition.Bodies.Count == 0)
                report.AddError("mm002", $"Catalog part '{definition.Id}' has no bodies.", instance.Id);
        }
    }

    private void CheckConnections(MachineDefinition machine, ValidationReport report)
    {
        var seenConnectionIds = new HashSet<string>(StringComparer.Ordinal);
        var usedConnectors = new Dictionary<(string Part, string Connector), string>();
        var partsById = machine.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var connection in machine.Connections)
        {
            if (!seenConnectionIds.Add(connection.Id))
                report.AddError("mm006", $"Duplicate connection id '{connection.Id}'.", connection.Id);

            foreach (var (partId, connectorName) in new[] { (connection.PartA, connection.ConnectorA), (connection.PartB, connection.ConnectorB) })
            {
                if (!partsById.TryGetValue(partId, out var instance))
                {
                    report.AddError("mm002", $"Connection '{connection.Id}' references unknown part instance '{partId}'.", connection.Id);
                    continue;
                }

                if (!catalog.TryGet(instance.Part, out var definition) || definition is null)
                    continue;

                var connector = definition.Connectors.FirstOrDefault(c =>
                    string.Equals(c.Name, connectorName, StringComparison.Ordinal));
                if (connector is null)
                {
                    report.AddError("mm003",
                        $"Part '{partId}' (catalog '{instance.Part}') has no connector '{connectorName}'.", connection.Id);
                    continue;
                }

                var key = (partId, connectorName);
                if (usedConnectors.TryGetValue(key, out var otherConnection))
                    report.AddError("mm005",
                        $"Connector '{connectorName}' of '{partId}' is used by both '{otherConnection}' and '{connection.Id}'.",
                        connection.Id);
                else
                    usedConnectors[key] = connection.Id;
            }
        }

        foreach (var connection in machine.Connections)
        {
            var connectorA = FindConnector(machine, connection.PartA, connection.ConnectorA);
            var connectorB = FindConnector(machine, connection.PartB, connection.ConnectorB);
            if (connectorA is null || connectorB is null)
                continue;

            if (!ConnectorRules.AreCompatible(connectorA.Type, connectorB.Type))
                report.AddError("mm004",
                    $"Connection '{connection.Id}': {connectorA.Type} cannot mate with {connectorB.Type}.", connection.Id);
        }
    }

    private ConnectorDefinition? FindConnector(MachineDefinition machine, string partId, string connectorName)
    {
        var instance = machine.Parts.FirstOrDefault(p => p.Id == partId);
        if (instance is null || !catalog.TryGet(instance.Part, out var definition) || definition is null)
            return null;
        return definition.Connectors.FirstOrDefault(c => c.Name == connectorName);
    }

    private void CheckWiring(MachineDefinition machine, ValidationReport report)
    {
        var partsById = machine.Parts.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var boardsById = machine.Boards.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var wiredComponents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wire in machine.Wiring)
        {
            if (!partsById.Contains(wire.Component))
                report.AddError("mm014", $"Wire targets unknown component '{wire.Component}'.", wire.Component);
            if (boardsById.Count > 0 && !boardsById.Contains(wire.Board))
                report.AddError("mm014", $"Wire references unknown board '{wire.Board}'.", wire.Board);
            wiredComponents.Add(wire.Component);
        }

        foreach (var instance in machine.Parts)
        {
            if (!catalog.TryGet(instance.Part, out var definition) || definition is null)
                continue;

            if (definition.Motor is { Kind: MotorKind.Stepper } && !wiredComponents.Contains(instance.Id))
                report.AddWarning("mm012", $"Stepper motor '{instance.Id}' has no wiring.", instance.Id);
        }
    }

    private void CheckConnectivity(MachineDefinition machine, ValidationReport report)
    {
        if (machine.Parts.Count == 0)
            return;

        var adjacency = machine.Parts.ToDictionary(p => p.Id, _ => new List<string>());
        foreach (var connection in machine.Connections)
        {
            if (adjacency.ContainsKey(connection.PartA) && adjacency.ContainsKey(connection.PartB))
            {
                adjacency[connection.PartA].Add(connection.PartB);
                adjacency[connection.PartB].Add(connection.PartA);
            }
        }

        var visited = new HashSet<string>();
        var islands = new List<List<string>>();
        foreach (var start in machine.Parts.Select(p => p.Id))
        {
            if (visited.Contains(start))
                continue;
            var island = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(start);
            visited.Add(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                island.Add(current);
                foreach (var next in adjacency[current].Where(next => !visited.Contains(next)))
                {
                    visited.Add(next);
                    queue.Enqueue(next);
                }
            }
            islands.Add(island);
        }

        if (islands.Count > 1)
            report.AddWarning("mm020",
                $"Assembly has {islands.Count} disconnected islands: " +
                string.Join(", ", islands.Select(i => "{" + string.Join(", ", i) + "}")));
    }

    private void CheckBelts(MachineDefinition machine, ValidationReport report)
    {
        foreach (var instance in machine.Parts)
        {
            if (!catalog.TryGet(instance.Part, out var definition) || definition is null || !definition.IsTransmissionElement)
                continue;

            var connections = machine.Connections
                .Where(c => c.PartA == instance.Id || c.PartB == instance.Id)
                .ToList();

            var pulleyCount = 0;
            var clampCount = 0;
            foreach (var connection in connections)
            {
                switch (ConnectionKind(machine, connection))
                {
                    case JointKind.Belt: pulleyCount++; break;
                    case JointKind.Clamp: clampCount++; break;
                }
            }

            if (pulleyCount != 2)
                report.AddWarning("mm021",
                    $"Belt '{instance.Id}' connects {pulleyCount} pulleys (expected 2).", instance.Id);
            if (clampCount > 1)
                report.AddError("mm032",
                    $"Belt '{instance.Id}' has {clampCount} clamp connections (at most 1).", instance.Id);
        }
    }

    private JointKind ConnectionKind(MachineDefinition machine, Connection connection)
    {
        var connectorA = FindConnector(machine, connection.PartA, connection.ConnectorA);
        var connectorB = FindConnector(machine, connection.PartB, connection.ConnectorB);
        return connectorA is null || connectorB is null ? JointKind.Weld : ConnectorRules.InferJoint(connectorA.Type, connectorB.Type);
    }
}
