using ColdChain.Core.Domain;
using ColdChain.Core.Storage;

namespace ColdChain.Core.Engine;

/// <summary>
/// Fleet-level entry point. Routes each reading to its truck's monitor; trucks never share state,
/// so a fault on one truck can never suppress or trigger an alert on another.
/// </summary>
public sealed class AlertEngine
{
    private readonly Dictionary<string, TruckMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _truckProfiles;
    private readonly EngineConfig _config;
    private readonly IIncidentStore _store;

    public AlertEngine(EngineConfig config, IReadOnlyDictionary<string, string> truckProfiles, IIncidentStore store)
    {
        _config = config;
        _truckProfiles = new Dictionary<string, string>(truckProfiles, StringComparer.OrdinalIgnoreCase);
        _store = store;
    }

    public event Action<AlertEvent>? EventRaised;
    public event Action<Reading>? ReadingProcessed;
    public event Action<Reading>? ReadingFaulted;

    public IReadOnlyCollection<TruckMonitor> Monitors => _monitors.Values;
    public IIncidentStore Store => _store;
    public EngineOptions Options => _config.Engine;

    public void Ingest(Reading reading, DateTimeOffset arrival) => MonitorFor(reading.TruckId, arrival).Ingest(reading, arrival);

    public void Tick(DateTimeOffset now)
    {
        foreach (var m in _monitors.Values) m.Tick(now);
    }

    public void Flush(DateTimeOffset now)
    {
        foreach (var m in _monitors.Values) m.Flush(now);
    }

    private TruckMonitor MonitorFor(string truckId, DateTimeOffset arrival)
    {
        if (_monitors.TryGetValue(truckId, out var existing)) return existing;

        var profiles = _config.ProfilesByName;
        CargoProfile profile;
        if (_truckProfiles.TryGetValue(truckId, out var name) && profiles.TryGetValue(name, out var configured))
        {
            profile = configured;
        }
        else
        {
            // Safer to over-alert on an unknown truck than to miss a pharma load.
            profile = profiles[_config.DefaultProfile];
            Raise(new AlertEvent(truckId, EventType.ProfileMissing, Severity.Warning, arrival, arrival,
                $"No cargo profile configured for {truckId}; using the strictest default ({profile.Name}). Assign one."));
        }

        var monitor = new TruckMonitor(truckId, profile, _config.Engine, _store, Raise,
            r => ReadingProcessed?.Invoke(r), r => ReadingFaulted?.Invoke(r));
        _monitors[truckId] = monitor;
        return monitor;
    }

    private void Raise(AlertEvent e)
    {
        _store.Append(e);
        EventRaised?.Invoke(e);
    }
}
