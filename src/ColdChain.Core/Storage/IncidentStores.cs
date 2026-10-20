using System.Text.Json;
using ColdChain.Core.Domain;

namespace ColdChain.Core.Storage;

/// <summary>
/// Where incidents and the event audit trail are kept. The POC uses in-memory and JSON files so the
/// reviewer needs nothing installed; production swaps in Azure SQL (ledger tables) or PostgreSQL
/// behind this same interface.
/// </summary>
public interface IIncidentStore
{
    void Upsert(Incident incident);
    void Append(AlertEvent alertEvent);
    IReadOnlyList<Incident> Incidents { get; }
    IReadOnlyList<AlertEvent> Events { get; }
}

public class InMemoryIncidentStore : IIncidentStore
{
    private readonly Dictionary<string, Incident> _incidents = new();
    private readonly List<string> _order = new();
    private readonly List<AlertEvent> _events = new();

    public void Upsert(Incident incident)
    {
        if (!_incidents.ContainsKey(incident.Id)) _order.Add(incident.Id);
        _incidents[incident.Id] = incident;
    }

    public void Append(AlertEvent alertEvent) => _events.Add(alertEvent);

    public IReadOnlyList<Incident> Incidents => _order.Select(id => _incidents[id]).ToList();
    public IReadOnlyList<AlertEvent> Events => _events;
}

/// <summary>Writes incidents.json and an append-only events.jsonl audit log.</summary>
public sealed class JsonFileIncidentStore : InMemoryIncidentStore
{
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "incidents.json"),
            JsonSerializer.Serialize(Incidents, JsonDefaults.Options));

        var compact = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
        File.WriteAllLines(Path.Combine(directory, "events.jsonl"),
            Events.Select(e => JsonSerializer.Serialize(e, compact)));
    }
}
