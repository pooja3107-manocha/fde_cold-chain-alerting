namespace ColdChain.Core.Domain;

/// <summary>One sensor reading as reported by a truck (event time = Timestamp). Humidity is context only.</summary>
public sealed record Reading(string TruckId, DateTimeOffset Timestamp, double TemperatureC, double? HumidityPct = null);

/// <summary>An excursion that ended before its tolerance ran out. Kept briefly so a later alert can mention it.</summary>
public sealed record PriorExcursion(DateTimeOffset Start, DateTimeOffset End, double PeakC);

public enum ExcursionDirection { High, Low }

public enum Severity
{
    /// <summary>Recorded for the audit trail and timeline; nobody is notified.</summary>
    Info,
    /// <summary>Worth a look: device problems, reminders, late data showing a problem.</summary>
    Warning,
    /// <summary>Pages ops: a confirmed excursion, or losing data while cargo is out of range.</summary>
    Critical
}

public enum EventType
{
    ExcursionAlert,
    ExcursionReopened,
    Reminder,
    ExcursionResumed,
    Resolved,
    BlipIgnored,
    SensorSilentDuringExcursion,
    DeviceOffline,
    DeviceBackOnline,
    SensorFault,
    LateReading,
    DuplicateDropped,
    ReadingRejected,
    ProfileMissing
}

/// <summary>
/// Everything the engine decides is written as an event. At = when it happened in the truck's
/// timeline (event time); DetectedAt = when the engine knew (processing time).
/// </summary>
public sealed record AlertEvent(
    string TruckId,
    EventType Type,
    Severity Severity,
    DateTimeOffset At,
    DateTimeOffset DetectedAt,
    string Message,
    string? IncidentId = null,
    double? TemperatureC = null);

public enum IncidentStatus { Open, Recovering, Resolved }

/// <summary>One excursion for one truck, from first out-of-range reading to confirmed recovery.</summary>
public sealed class Incident
{
    public required string Id { get; init; }
    public required string TruckId { get; init; }
    public required string CargoProfile { get; init; }
    public required ExcursionDirection Direction { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset AlertedAt { get; init; }

    /// <summary>First back-in-range reading of the latest recovery (cleared if the excursion resumes).</summary>
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public double PeakC { get; set; }
    public bool IncludesDataGap { get; set; }
    public int ReopenCount { get; set; }
    public int RemindersSent { get; set; }

    /// <summary>Expected readings that never arrived while the cargo was out of range.</summary>
    public List<DateTimeOffset> MissedReadings { get; init; } = new();

    /// <summary>Shorter excursions on the same truck shortly before this one: a sign of a recurring fault.</summary>
    public List<PriorExcursion> PriorExcursions { get; init; } = new();

    public double? HumidityAtStart { get; set; }
    public double? HumidityAtPeak { get; set; }
    public IncidentStatus Status { get; set; }

    public TimeSpan? OutOfRangeDuration => RecoveredAt is { } r ? r - StartedAt : null;
}
