using System.Globalization;
using System.Text.Json;
using ColdChain.Core.Domain;
using ColdChain.Core.Engine;

namespace ColdChain.Core.Simulation;

/// <summary>A reading plus the moment it reached the platform (processing time).</summary>
public sealed record Delivery(Reading Reading, DateTimeOffset ArrivesAt);

/// <summary>
/// Stands in for the trucks and the IoT gateway (in production: IoT Edge buffering + IoT Hub or MQTT).
/// Replays deliveries in arrival order and advances a simulated clock in fixed steps, so the engine
/// sees the same Ingest/Tick calls it would see live.
/// </summary>
public static class DeviceSimulator
{
    public static DateTimeOffset Run(AlertEngine engine, IEnumerable<Delivery> deliveries, TimeSpan? step = null)
    {
        var tick = step ?? TimeSpan.FromMinutes(1);
        var ordered = deliveries.OrderBy(d => d.ArrivesAt).ToList();   // stable: ties keep input order
        if (ordered.Count == 0) return DateTimeOffset.MinValue;

        var clock = ordered[0].ArrivesAt;
        foreach (var d in ordered)
        {
            while (clock + tick <= d.ArrivesAt)
            {
                clock += tick;
                engine.Tick(clock);
            }
            engine.Ingest(d.Reading, d.ArrivesAt);
        }

        // Let the reorder buffer drain normally, then flush anything left. We stop the clock at the end
        // of the data instead of running on, otherwise every truck would look "offline" after the file ends.
        var drainUntil = clock + engine.Options.AllowedLateness;
        var last = ordered[^1].ArrivesAt;
        while (clock + tick <= drainUntil && clock + tick - last <= engine.Options.SilenceThreshold)
        {
            clock += tick;
            engine.Tick(clock);
        }
        engine.Flush(clock);
        return clock;
    }
}

/// <summary>Loads seed data: the provided JSON (<c>{"readings": [...]}</c>) or a CSV. Field names are matched loosely.</summary>
public static class SeedLoader
{
    private static readonly string[] TruckCols = ["truck_id", "truckid", "truck", "vehicle_id", "vehicleid", "device_id"];
    private static readonly string[] TimeCols = ["timestamp", "ts", "time", "reading_time", "recorded_at", "datetime"];
    private static readonly string[] TempCols = ["temperature_c", "temp_c", "temperature", "temp", "celsius", "value"];
    private static readonly string[] ProfileCols = ["cargo_profile", "profile", "cargo", "cargo_type"];

    private static readonly string[] HumidityCols = ["humidity_pct", "humidity", "rh", "relative_humidity"];

    public static List<Reading> LoadReadings(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? LoadJson(path) : LoadCsv(path);

    /// <summary>
    /// Timestamps in the provided data carry no timezone; they are read as UTC. Rows that cannot be parsed
    /// are rejected loudly rather than skipped silently, because a silently dropped row is a hidden gap.
    /// </summary>
    private static List<Reading> LoadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("readings", out var r) ? r
            : throw new InvalidDataException($"{path}: expected an array or an object with a \"readings\" array");

        var readings = new List<Reading>();
        var i = 0;
        foreach (var row in rows.EnumerateArray())
        {
            i++;
            string? Field(string[] names)
            {
                foreach (var p in row.EnumerateObject())
                    if (names.Contains(p.Name.ToLowerInvariant()))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                return null;
            }

            var truck = Field(TruckCols) ?? throw new InvalidDataException($"{path} row {i}: no truck id");
            var tsText = Field(TimeCols) ?? throw new InvalidDataException($"{path} row {i}: no timestamp");
            if (!DateTimeOffset.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                throw new InvalidDataException($"{path} row {i}: cannot parse timestamp '{tsText}'");
            var temp = double.TryParse(Field(TempCols), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN;
            double? humidity = double.TryParse(Field(HumidityCols), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : null;
            readings.Add(new Reading(truck, ts.ToUniversalTime(), temp, humidity));
        }
        return readings;
    }

    private static List<Reading> LoadCsv(string csvPath)
    {
        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var header = Split(lines[0]);
        int truck = Col(header, TruckCols, csvPath), time = Col(header, TimeCols, csvPath), temp = Col(header, TempCols, csvPath);

        var readings = new List<Reading>(lines.Count);
        for (var i = 1; i < lines.Count; i++)
        {
            var f = Split(lines[i]);
            if (!DateTimeOffset.TryParse(f[time], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                throw new InvalidDataException($"{csvPath} line {i + 1}: cannot parse timestamp '{f[time]}'");
            var value = double.TryParse(f[temp], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            readings.Add(new Reading(f[truck], ts.ToUniversalTime(), value));
        }
        return readings;
    }

    public static Dictionary<string, string> LoadTruckProfiles(string csvPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(csvPath)) return map;
        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var header = Split(lines[0]);
        int truck = Col(header, TruckCols, csvPath), profile = Col(header, ProfileCols, csvPath);
        foreach (var line in lines.Skip(1))
        {
            var f = Split(line);
            map[f[truck]] = f[profile];
        }
        return map;
    }

    /// <summary>
    /// The seed file is a batch export with no arrival times, so its row order says nothing reliable about when
    /// readings reached the platform. For the replay each reading "arrives" at its own timestamp, which is the
    /// same as sorting by event time. Streaming disorder is exercised separately (scenario 08 and the chaos check).
    /// </summary>
    public static List<Delivery> ToDeliveries(IEnumerable<Reading> readings) =>
        readings.Select(r => new Delivery(r, r.Timestamp)).ToList();

    private static string[] Split(string line) => line.Split(',').Select(s => s.Trim().Trim('"')).ToArray();

    private static int Col(string[] header, string[] names, string path)
    {
        for (var i = 0; i < header.Length; i++)
            if (names.Contains(header[i].ToLowerInvariant().Replace(" ", "_"))) return i;
        throw new InvalidDataException($"{path}: none of the columns [{string.Join(", ", names)}] found in header '{string.Join(",", header)}'");
    }
}

/// <summary>
/// Fault injection for the "real trucks lose signal" story: delays some readings (within the reorder
/// window) and duplicates others, with a fixed seed so the run is repeatable.
/// </summary>
public static class Chaos
{
    public static List<Delivery> Disorder(IEnumerable<Delivery> deliveries, int seed = 42,
        int maxDelayMinutes = 8, double delayFraction = 0.3, double duplicateFraction = 0.05)
    {
        var rng = new Random(seed);
        var result = new List<Delivery>();
        foreach (var d in deliveries)
        {
            var delayed = rng.NextDouble() < delayFraction
                ? d with { ArrivesAt = d.ArrivesAt + TimeSpan.FromMinutes(rng.Next(1, maxDelayMinutes + 1)) }
                : d;
            result.Add(delayed);
            if (rng.NextDouble() < duplicateFraction)
                result.Add(d with { ArrivesAt = d.ArrivesAt + TimeSpan.FromMinutes(rng.Next(0, maxDelayMinutes + 1)) });
        }
        return result;
    }
}

/// <summary>Collects what the engine accepted and rejected per truck, for charts and summaries.</summary>
public sealed class RunCapture
{
    public RunCapture(AlertEngine engine)
    {
        engine.ReadingProcessed += r => Bucket(Processed, r.TruckId).Add(r);
        engine.ReadingFaulted += r => Bucket(Faults, r.TruckId).Add(r);
    }

    public Dictionary<string, List<Reading>> Processed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<Reading>> Faults { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static List<Reading> Bucket(Dictionary<string, List<Reading>> map, string truck)
    {
        if (!map.TryGetValue(truck, out var list)) map[truck] = list = new List<Reading>();
        return list;
    }
}

/// <summary>What is wrong with the source data, reported before the engine runs.</summary>
public sealed record DataQualityReport(
    string TruckId,
    int Rows,
    DateTimeOffset First,
    DateTimeOffset Last,
    int OutOfOrderRows,
    int DuplicateRows,
    IReadOnlyList<DateTimeOffset> MissingSlots);

public static class DataQuality
{
    /// <summary>
    /// Out of order = a row whose timestamp is earlier than a row before it in the file (same truck).
    /// Missing = an expected reading slot with no row, given the normal reporting interval.
    /// </summary>
    public static List<DataQualityReport> Inspect(IEnumerable<Reading> readings, TimeSpan interval)
    {
        var reports = new List<DataQualityReport>();
        foreach (var g in readings.GroupBy(r => r.TruckId, StringComparer.OrdinalIgnoreCase))
        {
            var rows = g.ToList();
            var outOfOrder = 0;
            var maxSoFar = DateTimeOffset.MinValue;
            foreach (var r in rows)
            {
                if (r.Timestamp < maxSoFar) outOfOrder++;
                else maxSoFar = r.Timestamp;
            }
            var distinct = rows.Select(r => r.Timestamp).Distinct().OrderBy(t => t).ToList();
            var missing = new List<DateTimeOffset>();
            for (var k = 1; k < distinct.Count; k++)
            {
                var step = distinct[k] - distinct[k - 1];
                if (step <= interval * 1.5) continue;
                var n = (int)Math.Round(step / interval) - 1;
                for (var j = 1; j <= Math.Max(n, 1); j++) missing.Add(distinct[k - 1] + interval * j);
            }
            reports.Add(new DataQualityReport(g.Key, rows.Count, distinct.First(), distinct.Last(),
                outOfOrder, rows.Count - distinct.Count, missing));
        }
        return reports;
    }
}
