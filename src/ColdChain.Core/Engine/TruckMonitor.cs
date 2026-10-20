using ColdChain.Core.Domain;
using ColdChain.Core.Storage;

namespace ColdChain.Core.Engine;

/// <summary>
/// Watches one truck. Pure decision logic: readings in, events out, no I/O.
///
/// Lifecycle:  Normal -> Pending -> Alerting -> Recovering -> (resolved) Normal
///   Pending     out of range, but not yet for longer than the cargo's tolerance
///   Alerting    an incident is open; ops have been alerted once
///   Recovering  back in range, waiting to confirm it stays there
///
/// Ingest() takes readings as they arrive (any order). They wait in a small buffer for
/// AllowedLateness so late arrivals can be put back in timestamp order; Tick() releases
/// them in order and checks whether the device has gone silent.
/// </summary>
public sealed class TruckMonitor
{
    private enum Phase { Normal, Pending, Alerting, Recovering }

    private readonly CargoProfile _p;
    private readonly EngineOptions _o;
    private readonly IIncidentStore _store;
    private readonly Action<AlertEvent> _emit;
    private readonly Action<Reading>? _onProcessed;
    private readonly Action<Reading>? _onFault;

    // Arrival side
    private readonly SortedDictionary<DateTimeOffset, Reading> _buffer = new();
    private DateTimeOffset? _lastValidArrival;
    private double? _lastValidTemp;
    private bool _silent;
    private bool _inFaultStreak;

    // Event-time side
    private DateTimeOffset? _lastProcessedTs;
    private double? _lastProcessedTemp;
    private Phase _phase = Phase.Normal;
    private ExcursionDirection _dir;
    private DateTimeOffset _excursionStart;
    private double _pendingPeak;
    private DateTimeOffset _pendingPeakAt;
    private double? _pendingHumidityStart;
    private double? _pendingHumidityAtPeak;
    private bool _pendingSawGap;
    private readonly List<DateTimeOffset> _pendingMissed = new();
    private readonly List<PriorExcursion> _recentBlips = new();
    private DateTimeOffset _recoveringSince;
    private DateTimeOffset _lastNotifiedAt;
    private Incident? _incident;
    private Incident? _lastResolved;
    private int _incidentSeq;

    public TruckMonitor(string truckId, CargoProfile profile, EngineOptions options, IIncidentStore store,
        Action<AlertEvent> emit, Action<Reading>? onProcessed = null, Action<Reading>? onFault = null)
    {
        TruckId = truckId;
        _p = profile;
        _o = options;
        _store = store;
        _emit = emit;
        _onProcessed = onProcessed;
        _onFault = onFault;
    }

    public string TruckId { get; }
    public CargoProfile Profile => _p;

    /// <summary>How many alerts a naive "page on every out-of-range reading" rule would have sent.</summary>
    public int NaiveThresholdBreaches { get; private set; }

    // ---------------------------------------------------------------- arrival

    public void Ingest(Reading r, DateTimeOffset arrival)
    {
        if (r.Timestamp > arrival + _o.MaxFutureSkew)
        {
            Emit(EventType.ReadingRejected, Severity.Warning, arrival, arrival,
                $"Rejected a reading stamped {Fmt.Time(r.Timestamp)}, {Fmt.Span(r.Timestamp - arrival)} in the future. " +
                "The device clock is likely wrong; it needs a time sync.", temp: r.TemperatureC);
            return;
        }

        if (IsFault(r.TemperatureC, out var reason))
        {
            _onFault?.Invoke(r);
            if (!_inFaultStreak)
                Emit(EventType.SensorFault, Severity.Warning, r.Timestamp, arrival,
                    $"Sensor reported {Fmt.Temp(r.TemperatureC)} ({reason}). Ignored for alerting; the probe needs checking.",
                    temp: r.TemperatureC);
            _inFaultStreak = true;
            return;
        }
        _inFaultStreak = false;

        if (_silent && _lastValidArrival is { } lastArrival)
        {
            Emit(EventType.DeviceBackOnline, Severity.Info, arrival, arrival,
                $"Valid readings resumed after {Fmt.Span(arrival - lastArrival)} without data.", temp: r.TemperatureC);
            _silent = false;
        }
        _lastValidArrival = arrival;
        _lastValidTemp = r.TemperatureC;

        if (_lastProcessedTs is { } processedUpTo && r.Timestamp <= processedUpTo)
        {
            if (r.Timestamp == processedUpTo && r.TemperatureC.Equals(_lastProcessedTemp))
            {
                Emit(EventType.DuplicateDropped, Severity.Info, r.Timestamp, arrival,
                    $"Duplicate of the {Fmt.Time(r.Timestamp)} reading dropped.", temp: r.TemperatureC);
                return;
            }

            var outOfRange = IsOutOfRange(r.TemperatureC);
            Emit(EventType.LateReading, outOfRange ? Severity.Warning : Severity.Info, r.Timestamp, arrival,
                $"Reading for {Fmt.Time(r.Timestamp)} arrived {Fmt.Span(arrival - r.Timestamp)} late, after that period was " +
                "already evaluated. Logged for the audit trail, not applied." +
                (outOfRange ? $" It shows {Fmt.Temp(r.TemperatureC)}, outside {Fmt.Range(_p)}: review this period." : ""),
                temp: r.TemperatureC);
            return;
        }

        if (_buffer.TryGetValue(r.Timestamp, out var existing))
        {
            var conflict = !existing.TemperatureC.Equals(r.TemperatureC);
            Emit(EventType.DuplicateDropped, Severity.Info, r.Timestamp, arrival,
                conflict
                    ? $"Second reading for {Fmt.Time(r.Timestamp)} with a different value ({Fmt.Temp(r.TemperatureC)} vs {Fmt.Temp(existing.TemperatureC)}); kept the first."
                    : $"Duplicate of the {Fmt.Time(r.Timestamp)} reading dropped.",
                temp: r.TemperatureC);
            return;
        }

        _buffer.Add(r.Timestamp, r);
    }

    /// <summary>Advance the processing clock: release buffered readings in order, then check for silence.</summary>
    public void Tick(DateTimeOffset now)
    {
        var releaseUpTo = now - _o.AllowedLateness;
        while (_buffer.Count > 0)
        {
            var first = _buffer.First();
            if (first.Key > releaseUpTo) break;
            _buffer.Remove(first.Key);
            Process(first.Value, now);
        }
        CheckSilence(now);
    }

    /// <summary>End of stream: process whatever is still waiting in the buffer.</summary>
    public void Flush(DateTimeOffset now)
    {
        foreach (var r in _buffer.Values.ToList()) Process(r, now);
        _buffer.Clear();
    }

    private void CheckSilence(DateTimeOffset now)
    {
        if (_silent || _lastValidArrival is not { } last || now - last <= _o.SilenceThreshold) return;

        _silent = true;
        var at = last + _o.SilenceThreshold;
        var faultNote = _inFaultStreak ? " The probe is sending fault values instead of temperatures." : "";
        var inExcursion = _phase is Phase.Pending or Phase.Alerting or Phase.Recovering
                          || _buffer.Values.Any(b => IsOutOfRange(b.TemperatureC));

        if (inExcursion)
        {
            _pendingSawGap = true;
            if (_incident is not null)
            {
                _incident.IncludesDataGap = true;
                _store.Upsert(_incident);
            }
            Emit(EventType.SensorSilentDuringExcursion, Severity.Critical, at, now,
                $"No valid readings for {Fmt.Span(_o.SilenceThreshold)} while cargo was at risk " +
                $"(last {Fmt.Temp(_lastValidTemp)}). Treat it as unsafe until data resumes; contact the driver.{faultNote}",
                _incident?.Id, _lastValidTemp);
        }
        else
        {
            Emit(EventType.DeviceOffline, Severity.Warning, at, now,
                $"No valid readings for {Fmt.Span(_o.SilenceThreshold)}. Cargo was in range at the last report " +
                $"({Fmt.Temp(_lastValidTemp)}).{faultNote}", temp: _lastValidTemp);
        }
    }

    // ---------------------------------------------------------------- event-time state machine

    private void Process(Reading r, DateTimeOffset now)
    {
        var t = r.TemperatureC;

        // A gap in the data while out of range: the unobserved time counts against the cargo.
        if (_lastProcessedTs is { } prev && _phase is Phase.Pending or Phase.Alerting or Phase.Recovering)
        {
            var step = r.Timestamp - prev;
            if (step > _o.ExpectedInterval * 1.5)
            {
                var missing = (int)Math.Round(step / _o.ExpectedInterval) - 1;
                var times = Enumerable.Range(1, Math.Max(missing, 1)).Select(k => prev + _o.ExpectedInterval * k).ToList();
                if (_incident is not null && _phase is Phase.Alerting or Phase.Recovering) _incident.MissedReadings.AddRange(times);
                else _pendingMissed.AddRange(times);
            }
            if (step > _o.SilenceThreshold)
            {
                _pendingSawGap = true;
                if (_incident is not null) _incident.IncludesDataGap = true;
                // Recovery must be observed: unseen minutes never count towards the 15 stable minutes.
                if (_phase == Phase.Recovering) _recoveringSince = r.Timestamp;
            }
        }
        _lastProcessedTs = r.Timestamp;
        _lastProcessedTemp = t;
        _onProcessed?.Invoke(r);

        var high = t > _p.MaxC;
        var low = t < _p.MinC;
        if (high || low) NaiveThresholdBreaches++;

        switch (_phase)
        {
            case Phase.Normal:
                if (high || low) StartPending(r, high ? ExcursionDirection.High : ExcursionDirection.Low, now);
                break;

            case Phase.Pending:
                if (IsBackInRange(t, _dir))
                {
                    Emit(EventType.BlipIgnored, Severity.Info, r.Timestamp, now,
                        $"{Describe(_dir)} for {Fmt.Span(r.Timestamp - _excursionStart)} (peak {Fmt.Temp(_pendingPeak)}), " +
                        $"under the {Fmt.Span(Tolerance(_dir))} tolerance. No alert.", temp: t);
                    _recentBlips.Add(new PriorExcursion(_excursionStart, r.Timestamp, _pendingPeak));
                    _phase = Phase.Normal;
                    if (high || low) StartPending(r, high ? ExcursionDirection.High : ExcursionDirection.Low, now);
                }
                else
                {
                    if (Worse(_pendingPeak, t, _dir) != _pendingPeak)
                    {
                        _pendingPeak = t;
                        _pendingPeakAt = r.Timestamp;
                        _pendingHumidityAtPeak = r.HumidityPct;
                    }
                    CheckConfirm(r, now);
                }
                break;

            case Phase.Alerting:
                var open = _incident!;
                if (IsBackInRange(t, _dir))
                {
                    _phase = Phase.Recovering;
                    _recoveringSince = r.Timestamp;
                    open.RecoveredAt = r.Timestamp;
                    open.Status = IncidentStatus.Recovering;
                    _store.Upsert(open);
                }
                else
                {
                    if (Worse(open.PeakC, t, _dir) != open.PeakC)
                    {
                        open.PeakC = t;
                        open.HumidityAtPeak = r.HumidityPct;
                    }
                    if (r.Timestamp - _lastNotifiedAt >= _p.ReminderInterval)
                    {
                        open.RemindersSent++;
                        _lastNotifiedAt = r.Timestamp;
                        Emit(EventType.Reminder, Severity.Warning, r.Timestamp, now,
                            $"Still {LowerFirst(Describe(_dir))}: {Fmt.Span(r.Timestamp - open.StartedAt)} so far, " +
                            $"peak {Fmt.Temp(open.PeakC)}, now {Fmt.Temp(t)}. Reminder, not a new alert.", open.Id, t);
                    }
                    _store.Upsert(open);
                }
                break;

            case Phase.Recovering:
                var recovering = _incident!;
                if (!IsBackInRange(t, _dir))
                {
                    _phase = Phase.Alerting;
                    recovering.Status = IncidentStatus.Open;
                    recovering.RecoveredAt = null;
                    recovering.PeakC = Worse(recovering.PeakC, t, _dir);
                    _store.Upsert(recovering);
                    Emit(EventType.ExcursionResumed, Severity.Info, r.Timestamp, now,
                        $"Out of range again ({Fmt.Temp(t)}) before recovery was confirmed. Same incident continues; no new alert.",
                        recovering.Id, t);
                }
                else if (r.Timestamp - _recoveringSince >= _p.ClearAfter)
                {
                    Resolve(r, now);
                }
                break;
        }
    }

    private void StartPending(Reading r, ExcursionDirection dir, DateTimeOffset now)
    {
        _phase = Phase.Pending;
        _dir = dir;
        _excursionStart = r.Timestamp;
        _pendingPeak = r.TemperatureC;
        _pendingPeakAt = r.Timestamp;
        _pendingHumidityStart = r.HumidityPct;
        _pendingHumidityAtPeak = r.HumidityPct;
        _pendingSawGap = false;
        _pendingMissed.Clear();
        _recentBlips.RemoveAll(b => r.Timestamp - b.End > _o.RecurrenceWindow);
        CheckConfirm(r, now);
    }

    private void CheckConfirm(Reading r, DateTimeOffset now)
    {
        if (r.Timestamp - _excursionStart < Tolerance(_dir)) return;

        var gapNote = _pendingSawGap ? " Part of this period had no sensor data, counted as out of range." : "";
        var missedNote = _pendingMissed.Count switch
        {
            0 => "",
            1 => $" 1 expected reading ({Fmt.Time(_pendingMissed[0])}) never arrived; that time is counted as out of range.",
            var n => $" {n} expected readings never arrived ({string.Join(", ", _pendingMissed.Select(Fmt.Time))}); that time is counted as out of range."
        };
        var priors = _recentBlips.Where(b => _excursionStart - b.End <= _o.RecurrenceWindow).ToList();
        var priorNote = priors.Count == 0 ? "" :
            $" This is excursion {priors.Count + 1} in the last {(_o.RecurrenceWindowMinutes == 60 ? "hour" : Fmt.Span(_o.RecurrenceWindow))}: " +
            string.Join("; ", priors.Select(b => $"{Fmt.Time(b.Start)} to {Fmt.Time(b.End)} ({Fmt.Span(b.End - b.Start)}, peak {Fmt.Temp(b.PeakC)})")) +
            $" stayed under the tolerance, which points to a recurring fault rather than a one-off.";

        if (_lastResolved is { ResolvedAt: { } resolvedAt } last
            && last.Direction == _dir
            && _excursionStart - resolvedAt <= _p.ReopenWindow)
        {
            _incident = last;
            last.ResolvedAt = null;
            last.RecoveredAt = null;
            last.Status = IncidentStatus.Open;
            last.ReopenCount++;
            last.PeakC = Worse(last.PeakC, _pendingPeak, _dir);
            if (_pendingSawGap) last.IncludesDataGap = true;
            last.MissedReadings.AddRange(_pendingMissed);
            _lastResolved = null;
            Emit(EventType.ExcursionReopened, Severity.Critical, r.Timestamp, now,
                $"{Describe(_dir)} again for {Fmt.Span(r.Timestamp - _excursionStart)}, only {Fmt.Span(_excursionStart - resolvedAt)} " +
                $"after {last.Id} was resolved. Reopened {last.Id} rather than opening a new incident.{gapNote}",
                last.Id, r.TemperatureC);
        }
        else
        {
            _incidentSeq++;
            _incident = new Incident
            {
                Id = $"{TruckId}-{_incidentSeq:00}",
                TruckId = TruckId,
                CargoProfile = _p.Name,
                Direction = _dir,
                StartedAt = _excursionStart,
                AlertedAt = r.Timestamp,
                PeakC = _pendingPeak,
                IncludesDataGap = _pendingSawGap,
                Status = IncidentStatus.Open,
                MissedReadings = _pendingMissed.ToList(),
                PriorExcursions = priors,
                HumidityAtStart = _pendingHumidityStart,
                HumidityAtPeak = _pendingHumidityAtPeak
            };
            Emit(EventType.ExcursionAlert, Severity.Critical, r.Timestamp, now,
                $"{Describe(_dir)} for {Fmt.Span(r.Timestamp - _excursionStart)}, past the {Fmt.Span(Tolerance(_dir))} tolerance " +
                $"(peak {Fmt.Temp(_pendingPeak)} at {Fmt.Time(_pendingPeakAt)}, now {Fmt.Temp(r.TemperatureC)})." +
                $"{missedNote}{priorNote}{gapNote}", _incident.Id, r.TemperatureC);
        }

        _store.Upsert(_incident);
        _phase = Phase.Alerting;
        _lastNotifiedAt = r.Timestamp;
    }

    private void Resolve(Reading r, DateTimeOffset now)
    {
        var inc = _incident!;
        inc.RecoveredAt = _recoveringSince;
        inc.ResolvedAt = r.Timestamp;
        inc.Status = IncidentStatus.Resolved;
        _store.Upsert(inc);

        var extras = new List<string>();
        if (inc.ReopenCount > 0) extras.Add($"reopened {inc.ReopenCount}x");
        if (inc.IncludesDataGap) extras.Add("includes a data gap");
        var extra = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";

        var span = Fmt.Span(inc.RecoveredAt.Value - inc.StartedAt);
        var duration = inc.ReopenCount > 0
            ? $"incident spanned {span} from first excursion to final recovery"
            : $"out of range for {span}";
        Emit(EventType.Resolved, Severity.Info, r.Timestamp, now,
            $"Back in range for {Fmt.Span(_p.ClearAfter)}. {inc.Id} closed: {duration}, peak {Fmt.Temp(inc.PeakC)}{extra}.",
            inc.Id, r.TemperatureC);

        _lastResolved = inc;
        _incident = null;
        _phase = Phase.Normal;
    }

    // ---------------------------------------------------------------- rules

    private bool IsOutOfRange(double t) => t > _p.MaxC || t < _p.MinC;

    /// <summary>Hysteresis: an excursion only ends once the reading is clearly back inside the range.</summary>
    private bool IsBackInRange(double t, ExcursionDirection dir) =>
        dir == ExcursionDirection.High ? t <= _p.MaxC - _p.HysteresisC : t >= _p.MinC + _p.HysteresisC;

    private TimeSpan Tolerance(ExcursionDirection dir) =>
        dir == ExcursionDirection.High ? _p.HighTolerance : _p.LowTolerance;

    private static double Worse(double current, double t, ExcursionDirection dir) =>
        dir == ExcursionDirection.High ? Math.Max(current, t) : Math.Min(current, t);

    private string Describe(ExcursionDirection dir) =>
        dir == ExcursionDirection.High ? $"Above {Fmt.Temp(_p.MaxC)}" : $"Below {Fmt.Temp(_p.MinC)}";

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private bool IsFault(double t, out string reason)
    {
        if (double.IsNaN(t) || double.IsInfinity(t)) { reason = "not a number"; return true; }
        if (_o.KnownFaultValues.Any(f => Math.Abs(f - t) < 0.001)) { reason = "a known probe fault code"; return true; }
        if (t < _o.PlausibleMinC || t > _o.PlausibleMaxC) { reason = "physically implausible"; return true; }
        reason = "";
        return false;
    }

    private void Emit(EventType type, Severity severity, DateTimeOffset at, DateTimeOffset detectedAt,
        string message, string? incidentId = null, double? temp = null) =>
        _emit(new AlertEvent(TruckId, type, severity, at, detectedAt, message, incidentId, temp));
}
