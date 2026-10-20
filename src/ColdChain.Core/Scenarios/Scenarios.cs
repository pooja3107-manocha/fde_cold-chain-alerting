using System.Text.Json;
using ColdChain.Core.Domain;
using ColdChain.Core.Engine;
using ColdChain.Core.Simulation;
using ColdChain.Core.Storage;

namespace ColdChain.Core.Scenarios;

/// <summary>
/// A small, named story about one truck. Readings are [minuteOffset, tempC] or
/// [minuteOffset, tempC, arrivalDelayMinutes]; a missing minute is a gap in the data.
/// </summary>
public sealed class ScenarioDefinition
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string Proves { get; init; } = "";
    public required string Profile { get; init; }
    public string TruckId { get; init; } = "TRK-SIM";
    public DateTimeOffset Start { get; init; } = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    public required double[][] Readings { get; init; }
    public required ScenarioExpectation Expect { get; init; }

    public List<Delivery> ToDeliveries() => Readings.Select(row =>
    {
        if (row.Length < 2) throw new InvalidDataException($"Scenario {Name}: each reading needs [minute, temp]");
        var ts = Start + TimeSpan.FromMinutes(row[0]);
        var delay = row.Length > 2 ? TimeSpan.FromMinutes(row[2]) : TimeSpan.Zero;
        return new Delivery(new Reading(TruckId, ts, row[1]), ts + delay);
    }).ToList();
}

public sealed class ScenarioExpectation
{
    /// <summary>The exact ordered list of event types the engine must raise.</summary>
    public required List<EventType> Sequence { get; init; }
    public int? Incidents { get; init; }
}

public sealed record ScenarioResult(
    ScenarioDefinition Definition,
    IReadOnlyList<AlertEvent> Events,
    IReadOnlyList<Incident> Incidents,
    RunCapture Capture,
    CargoProfile Profile,
    int NaiveBreaches,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

public static class ScenarioRunner
{
    public static List<ScenarioDefinition> LoadAll(string directory) =>
        Directory.GetFiles(directory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(Load)
            .ToList();

    public static ScenarioDefinition Load(string path) =>
        JsonSerializer.Deserialize<ScenarioDefinition>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InvalidDataException($"Could not read scenario {path}");

    public static ScenarioResult Run(ScenarioDefinition def, EngineConfig config)
    {
        if (!config.ProfilesByName.TryGetValue(def.Profile, out var profile))
            throw new InvalidDataException($"Scenario {def.Name}: unknown profile '{def.Profile}'");

        var store = new InMemoryIncidentStore();
        var engine = new AlertEngine(config, new Dictionary<string, string> { [def.TruckId] = def.Profile }, store);
        var capture = new RunCapture(engine);
        DeviceSimulator.Run(engine, def.ToDeliveries());

        var failures = new List<string>();
        var actual = store.Events.Select(e => e.Type).ToList();
        if (!actual.SequenceEqual(def.Expect.Sequence))
            failures.Add($"expected events [{string.Join(", ", def.Expect.Sequence)}] but got [{string.Join(", ", actual)}]");
        if (def.Expect.Incidents is { } n && store.Incidents.Count != n)
            failures.Add($"expected {n} incident(s) but got {store.Incidents.Count}");

        var naive = engine.Monitors.Sum(m => m.NaiveThresholdBreaches);
        return new ScenarioResult(def, store.Events.ToList(), store.Incidents, capture, profile, naive, failures);
    }
}
