using ColdChain.Core.Domain;
using ColdChain.Core.Engine;
using ColdChain.Core.Simulation;
using ColdChain.Core.Storage;

namespace ColdChain.Dashboard;

// ------------------------------------------------------------------------------------------- API shapes
// The UI depends only on these records, not on engine internals, so the engine can change underneath.

public enum TruckStatus { Alert, Recovering, Resolved, Ok }

public sealed record ProfileInfo(string Name, string Description, double MinC, double MaxC, int HighToleranceMinutes, int LowToleranceMinutes);

public sealed record TruckSummary(
    string TruckId,
    ProfileInfo Profile,
    TruckStatus Status,
    string Headline,
    double? LatestC,
    DateTimeOffset? LatestAt,
    int Readings,
    int Alerts,
    int NaiveBreaches);

public sealed record AlertRow(
    string IncidentId,
    string TruckId,
    string Problem,
    IncidentStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset AlertedAt,
    DateTimeOffset? RecoveredAt,
    DateTimeOffset? ResolvedAt,
    int? OutOfRangeMinutes,
    double PeakC,
    string Why,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DateTimeOffset> MissedReadings);

public sealed record PointDto(DateTimeOffset At, double? TemperatureC, double? HumidityPct);

public sealed record FileRowDto(int Row, DateTimeOffset At, double? TemperatureC, double? HumidityPct, bool OutOfOrder, bool Duplicate, bool Fault);

public sealed record EventDto(DateTimeOffset At, EventType Type, string Label, Severity Severity, string Message, string? IncidentId, double? TemperatureC);

public sealed record TruckDetail(
    TruckSummary Summary,
    IReadOnlyList<PointDto> Readings,
    IReadOnlyList<PointDto> Faults,
    IReadOnlyList<FileRowDto> FileRows,
    IReadOnlyList<DateTimeOffset> MissingSlots,
    IReadOnlyList<AlertRow> Incidents,
    IReadOnlyList<EventDto> Events,
    int SilenceThresholdMinutes);

public sealed record FleetSnapshot(
    string Source,
    DateTimeOffset DataStart,
    DateTimeOffset DataEnd,
    IReadOnlyDictionary<string, TruckDetail> Trucks);

// ------------------------------------------------------------------------------------------- data source

/// <summary>
/// Where the dashboard gets its picture of the fleet. The POC replays the seed file through the engine
/// once at startup; production implements this over the live incident store and telemetry database.
/// </summary>
public interface IFleetDataSource
{
    FleetSnapshot Current { get; }
}

public sealed class SeedReplaySource : IFleetDataSource
{
    public SeedReplaySource(string repoRoot) => Current = Build(repoRoot);

    public FleetSnapshot Current { get; }

    private static FleetSnapshot Build(string root)
    {
        var config = EngineConfig.Load(Path.Combine(root, "config", "cargo_profiles.json"));
        var readings = SeedLoader.LoadReadings(Path.Combine(root, "data", "sensor_readings.json"));
        var truckProfiles = SeedLoader.LoadTruckProfiles(Path.Combine(root, "data", "trucks.csv"));
        var quality = DataQuality.Inspect(readings, config.Engine.ExpectedInterval);

        var store = new InMemoryIncidentStore();
        var engine = new AlertEngine(config, truckProfiles, store);
        var capture = new RunCapture(engine);
        DeviceSimulator.Run(engine, SeedLoader.ToDeliveries(readings));

        var trucks = new Dictionary<string, TruckDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in engine.Monitors.OrderBy(m => m.TruckId, StringComparer.Ordinal))
        {
            var p = m.Profile;
            var events = store.Events.Where(e => e.TruckId == m.TruckId).ToList();
            var processed = capture.Processed.GetValueOrDefault(m.TruckId) ?? [];
            var faults = capture.Faults.GetValueOrDefault(m.TruckId) ?? [];
            var incidents = store.Incidents.Where(i => i.TruckId == m.TruckId).Select(i => ToAlertRow(i, events)).ToList();
            var latest = processed.MaxBy(r => r.Timestamp);

            var status = incidents.Any(i => i.Status == IncidentStatus.Open) ? TruckStatus.Alert
                : incidents.Any(i => i.Status == IncidentStatus.Recovering) ? TruckStatus.Recovering
                : incidents.Count > 0 ? TruckStatus.Resolved
                : TruckStatus.Ok;

            var summary = new TruckSummary(m.TruckId,
                new ProfileInfo(p.Name, p.Description, p.MinC, p.MaxC, p.HighToleranceMinutes, p.LowToleranceMinutes),
                status, Headline(p, incidents, events, processed), latest?.TemperatureC, latest?.Timestamp,
                processed.Count, incidents.Count, m.NaiveThresholdBreaches);

            var rows = readings.Where(r => string.Equals(r.TruckId, m.TruckId, StringComparison.OrdinalIgnoreCase)).ToList();
            trucks[m.TruckId] = new TruckDetail(summary,
                processed.OrderBy(r => r.Timestamp).Select(ToPoint).ToList(),
                faults.Select(ToPoint).ToList(),
                ToFileRows(rows, faults),
                quality.FirstOrDefault(q => string.Equals(q.TruckId, m.TruckId, StringComparison.OrdinalIgnoreCase))?.MissingSlots ?? [],
                incidents,
                events.Select(e => new EventDto(e.At, e.Type, EventName(e.Type), e.Severity, e.Message, e.IncidentId, Finite(e.TemperatureC))).ToList(),
                config.Engine.SilenceThresholdMinutes);
        }

        var end = capture.Processed.Values.SelectMany(v => v).Select(r => r.Timestamp).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        return new FleetSnapshot("Seed replay: data/sensor_readings.json", readings.Min(r => r.Timestamp), end, trucks);
    }

    private static AlertRow ToAlertRow(Incident i, List<AlertEvent> events)
    {
        var why = events.FirstOrDefault(e => e.IncidentId == i.Id && e.Type is EventType.ExcursionAlert or EventType.ExcursionReopened)?.Message ?? "";
        var notes = new List<string>();
        if (i.IncludesDataGap) notes.Add("includes a data gap");
        if (i.ReopenCount > 0) notes.Add($"reopened {i.ReopenCount}x");
        if (i.RemindersSent > 0) notes.Add($"{i.RemindersSent} reminder{(i.RemindersSent == 1 ? "" : "s")}");
        if (i.MissedReadings.Count > 0) notes.Add($"{i.MissedReadings.Count} reading{(i.MissedReadings.Count == 1 ? "" : "s")} missing");
        if (i.PriorExcursions.Count > 0) notes.Add($"repeat of an excursion at {string.Join(", ", i.PriorExcursions.Select(p => Fmt.Time(p.Start)))}");
        return new AlertRow(i.Id, i.TruckId, i.Direction == ExcursionDirection.High ? "Too warm" : "Too cold", i.Status,
            i.StartedAt, i.AlertedAt, i.RecoveredAt, i.ResolvedAt,
            i.OutOfRangeDuration is { } d ? (int)Math.Round(d.TotalMinutes) : null,
            i.PeakC, why, notes, i.MissedReadings);
    }

    private static string Headline(CargoProfile p, List<AlertRow> incidents, List<AlertEvent> events, List<Reading> processed)
    {
        var latestIncident = incidents.MaxBy(i => i.AlertedAt);
        if (latestIncident is not null) return latestIncident.Why;
        var blips = events.Where(e => e.Type == EventType.BlipIgnored).ToList();
        if (blips.Count > 0) return $"No alert. {blips.Count} brief excursion{(blips.Count == 1 ? "" : "s")} recovered within tolerance.";
        if (processed.Count == 0) return "No valid readings.";
        var warmest = processed.Max(r => r.TemperatureC);
        return $"No alert. Stayed within {Fmt.Range(p)}; warmest {Fmt.Temp(warmest)}.";
    }

    /// <summary>Same out-of-order rule as DataQuality: earlier than a row listed before it in the file.</summary>
    private static List<FileRowDto> ToFileRows(List<Reading> rows, List<Reading> faults)
    {
        var result = new List<FileRowDto>(rows.Count);
        var latest = DateTimeOffset.MinValue;
        var seen = new HashSet<DateTimeOffset>();
        for (var k = 0; k < rows.Count; k++)
        {
            var r = rows[k];
            var duplicate = !seen.Add(r.Timestamp);
            var outOfOrder = r.Timestamp < latest;
            if (!outOfOrder) latest = r.Timestamp;
            var fault = double.IsNaN(r.TemperatureC) || faults.Any(f => f.Timestamp == r.Timestamp && f.TemperatureC.Equals(r.TemperatureC));
            result.Add(new FileRowDto(k + 1, r.Timestamp, Finite(r.TemperatureC), r.HumidityPct, outOfOrder, duplicate, fault));
        }
        return result;
    }

    private static PointDto ToPoint(Reading r) => new(r.Timestamp, Finite(r.TemperatureC), r.HumidityPct);

    // JSON has no NaN; an unreadable value is sent as null.
    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    private static string EventName(EventType type) => type switch
    {
        EventType.ExcursionAlert => "Alert",
        EventType.ExcursionReopened => "Reopened",
        EventType.ExcursionResumed => "Resumed",
        EventType.BlipIgnored => "Blip ignored",
        EventType.SensorSilentDuringExcursion => "Silent during excursion",
        EventType.DeviceOffline => "Device offline",
        EventType.DeviceBackOnline => "Back online",
        EventType.SensorFault => "Sensor fault",
        EventType.LateReading => "Late reading",
        EventType.DuplicateDropped => "Duplicate dropped",
        EventType.ReadingRejected => "Reading rejected",
        EventType.ProfileMissing => "No cargo profile",
        _ => type.ToString()
    };
}
