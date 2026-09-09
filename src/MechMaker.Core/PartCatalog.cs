using System.Text.Json;
using MechMaker.Core.Model;

namespace MechMaker.Core;

/// <summary>A set of part definitions, loadable from a directory of JSON files.</summary>
public sealed class PartCatalog
{
    private readonly Dictionary<string, PartDefinition> _parts;

    public PartCatalog(IEnumerable<PartDefinition> parts)
    {
        _parts = parts.ToDictionary(p => p.Id);
    }

    public IReadOnlyCollection<PartDefinition> All => _parts.Values;

    public PartDefinition? Find(string id) => _parts.GetValueOrDefault(id);

    public PartDefinition Get(string id) =>
        _parts.TryGetValue(id, out var part)
            ? part
            : throw new KeyNotFoundException($"Unknown part '{id}' in catalog.");

    public bool TryGet(string id, out PartDefinition? definition)
    {
        definition = _parts.GetValueOrDefault(id);
        return definition is not null;
    }

    public static PartCatalog LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Catalog directory not found: {directory}");

        var parts = new List<PartDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var json = File.ReadAllText(file);
            var part = JsonSerializer.Deserialize<PartDefinition>(json, CoreJson.Options)
                       ?? throw new InvalidDataException($"Failed to parse catalog part: {file}");
            parts.Add(part);
        }

        return new PartCatalog(parts);
    }
}
