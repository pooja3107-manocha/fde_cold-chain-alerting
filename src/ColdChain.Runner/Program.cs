using System.Text;
using ColdChain.Core.Domain;
using ColdChain.Core.Engine;
using ColdChain.Core.Scenarios;
using ColdChain.Core.Simulation;
using ColdChain.Core.Storage;
using ColdChain.Runner;

var command = (args.FirstOrDefault() ?? "all").ToLowerInvariant();
var root = FindRepoRoot();
var config = EngineConfig.Load(Path.Combine(root, "config", "cargo_profiles.json"));
var outDir = Path.Combine(root, "out");
var dataFile = Path.Combine(root, "data", "sensor_readings.json");
var trucksFile = Path.Combine(root, "data", "trucks.csv");

return command switch
{
    "seed" or "alerts" => RunSeed(),
    "scenarios" => RunScenarios(args.Skip(1).FirstOrDefault(), verbose: true),
    "verify" => RunScenarios(null, verbose: false),
    "chaos" => RunChaos(),
    "sensitivity" => RunSensitivity(print: true).ExitCode,
    "all" => RunAll(),
    _ => Help()
};

int RunAll()
{
    var verify = RunScenarios(null, verbose: false);
    var chaos = RunChaos();
    var seed = RunSeed();
    Console.WriteLine();
    ConsoleTimeline.Write($"Alert list : {Path.Combine(outDir, "alerts.md")}\n", ConsoleColor.Cyan);
    ConsoleTimeline.Write($"Reports    : {Path.Combine(outDir, "seed", "report.html")}\n", ConsoleColor.Cyan);
    ConsoleTimeline.Write($"             {Path.Combine(outDir, "scenarios", "report.html")}\n", ConsoleColor.Cyan);
    return verify | seed | chaos;
}

// ---------------------------------------------------------------------------------------------- seed data

int RunSeed()
{
    var readings = SeedLoader.LoadReadings(dataFile);
    var truckProfiles = SeedLoader.LoadTruckProfiles(trucksFile);
    var quality = DataQuality.Inspect(readings, config.Engine.ExpectedInterval);

    var store = new JsonFileIncidentStore();
    var engine = new AlertEngine(config, truckProfiles, store);
    var capture = new RunCapture(engine);
    DeviceSimulator.Run(engine, SeedLoader.ToDeliveries(readings));
    store.Save(Path.Combine(outDir, "seed"));

    var monitors = engine.Monitors.OrderBy(m => m.TruckId, StringComparer.Ordinal).ToList();
    var dataStart = readings.Min(r => r.Timestamp);
    var dataEnd = capture.Processed.Values.SelectMany(v => v).Max(r => r.Timestamp);
    var summarizer = LlmSummarizer.FromEnvironment();

    // Data quality first: the reviewer should see what was wrong with the input before the decisions.
    ConsoleTimeline.Heading($"Input: {readings.Count} readings, {monitors.Count} trucks, {Fmt.Time(dataStart)} to {Fmt.Time(dataEnd)} UTC");
    var qualityNotes = QualityNotes(quality);
    foreach (var note in qualityNotes) ConsoleTimeline.Write($" {note}\n", ConsoleColor.Yellow);
    if (qualityNotes.Count == 0) Console.WriteLine(" No ordering, duplicate or gap problems found.");

    ConsoleTimeline.Heading("Timeline per truck");
    foreach (var m in monitors)
        ConsoleTimeline.PrintTruck(m.TruckId, m.Profile, store.Events.Where(e => e.TruckId == m.TruckId),
            m.NaiveThresholdBreaches, capture.Processed.GetValueOrDefault(m.TruckId)?.Count ?? 0);

    // The required output: what I'd alert on, and why.
    var alerts = new List<AlertDecision>();
    var quiet = new List<(string Truck, string Why)>();
    foreach (var m in monitors)
    {
        var events = store.Events.Where(e => e.TruckId == m.TruckId).ToList();
        var processed = capture.Processed.GetValueOrDefault(m.TruckId) ?? [];
        var incidents = store.Incidents.Where(i => i.TruckId == m.TruckId).ToList();
        if (incidents.Count == 0)
        {
            quiet.Add((m.TruckId, WhyNoAlert(m.Profile, events, processed)));
            continue;
        }
        foreach (var i in incidents)
        {
            var why = events.First(e => e.IncidentId == i.Id && e.Type is EventType.ExcursionAlert or EventType.ExcursionReopened).Message;
            var facts = AlertFacts.From(i, m.Profile, processed.LastOrDefault()?.TemperatureC, dataEnd);
            var summary = summarizer.Summarize(facts);
            alerts.Add(new AlertDecision(i, why, facts.StatusAtEndOfData, summary, summarizer.Source));
        }
    }

    var naive = monitors.Sum(m => m.NaiveThresholdBreaches);
    ConsoleTimeline.Heading($"What I would alert on, and why ({alerts.Count} alert{(alerts.Count == 1 ? "" : "s")})");
    var n = 0;
    foreach (var a in alerts.OrderBy(a => a.Incident.AlertedAt))
    {
        n++;
        ConsoleTimeline.Write($" {n}. {a.Incident.TruckId}  ALERT at {Fmt.Time(a.Incident.AlertedAt)}  ", ConsoleColor.Red);
        Console.WriteLine(a.Incident.Direction == ExcursionDirection.High ? "too warm" : "too cold");
        ConsoleTimeline.Write("    Why    : ", ConsoleColor.DarkGray); Console.WriteLine(a.Why);
        ConsoleTimeline.Write("    Status : ", ConsoleColor.DarkGray); Console.WriteLine(a.Status);
        ConsoleTimeline.Write($"    For ops ({a.SummarySource}): ", ConsoleColor.DarkGray); Console.WriteLine(a.Summary);
    }
    ConsoleTimeline.Heading("No alert");
    foreach (var (truck, why) in quiet)
    {
        ConsoleTimeline.Write($" {truck}  ", ConsoleColor.Green);
        Console.WriteLine(why);
    }
    Console.WriteLine();
    Console.WriteLine($" A rule that alerts on every reading above the limit would have sent {naive} alerts. This engine sends {alerts.Count}.");

    var sensitivity = RunSensitivity(print: true);

    WriteAlertsMarkdown(Path.Combine(outDir, "alerts.md"), readings.Count, monitors.Count, dataStart, dataEnd,
        alerts, quiet, qualityNotes, naive, sensitivity.Rows);

    var sections = monitors.Select(m =>
    {
        var events = store.Events.Where(e => e.TruckId == m.TruckId).ToList();
        var incidents = store.Incidents.Where(i => i.TruckId == m.TruckId).ToList();
        var verdict = incidents.Count > 0
            ? string.Join(" ", alerts.Where(a => a.Incident.TruckId == m.TruckId).Select(a => $"Alert at {Fmt.Time(a.Incident.AlertedAt)}: {a.Summary}"))
            : $"No alert. {quiet.First(q => q.Truck == m.TruckId).Why}";
        return new ReportSection(m.TruckId, verdict, null, null, null,
            new ChartData(m.TruckId, m.Profile, null, capture.Processed.GetValueOrDefault(m.TruckId) ?? [],
                capture.Faults.GetValueOrDefault(m.TruckId) ?? [], incidents, events, m.NaiveThresholdBreaches,
                readings.Where(r => string.Equals(r.TruckId, m.TruckId, StringComparison.OrdinalIgnoreCase)).ToList(),
                quality.FirstOrDefault(q => string.Equals(q.TruckId, m.TruckId, StringComparison.OrdinalIgnoreCase))?.MissingSlots));
    }).ToList();

    HtmlReport.Write(Path.Combine(outDir, "seed", "report.html"), new ReportModel(
        "Nimbus Freight: one hour of reefer data, and what the engine would alert on",
        $"{readings.Count} readings from {monitors.Count} trucks between {Fmt.Time(dataStart)} and {Fmt.Time(dataEnd)} UTC. " +
        $"Alerting on every reading above the limit would have sent {naive} alerts. This engine alerts {alerts.Count} times: " +
        $"{string.Join(", ", alerts.OrderBy(a => a.Incident.AlertedAt).Select(a => $"{a.Incident.TruckId} at {Fmt.Time(a.Incident.AlertedAt)}"))}.",
        qualityNotes.Count > 0 ? "Data issues found and handled: " + string.Join(" ", qualityNotes) : null,
        sections, config.Engine.SilenceThreshold));
    return 0;
}

static string WhyNoAlert(CargoProfile p, List<AlertEvent> events, List<Reading> processed)
{
    var blips = events.Where(e => e.Type == EventType.BlipIgnored).ToList();
    if (blips.Count > 0)
        return string.Join(" ", blips.Select(b =>
            $"Brief excursion, back in range at {Fmt.Time(b.At)}: {char.ToLowerInvariant(b.Message[0])}{b.Message[1..].Replace(" No alert.", "")}"));
    if (processed.Count == 0) return "No valid readings.";
    var warmest = processed.MaxBy(r => r.TemperatureC)!;
    var coldest = processed.MinBy(r => r.TemperatureC)!;
    return $"Stayed within {Fmt.Range(p)} throughout. Warmest reading {Fmt.Temp(warmest.TemperatureC)} at {Fmt.Time(warmest.Timestamp)} " +
           $"({(p.MaxC - warmest.TemperatureC).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} °C below the limit), " +
           $"coldest {Fmt.Temp(coldest.TemperatureC)}.";
}

static List<string> QualityNotes(List<DataQualityReport> quality)
{
    var notes = new List<string>();
    foreach (var q in quality.OrderBy(q => q.TruckId, StringComparer.Ordinal))
    {
        if (q.OutOfOrderRows > 0)
            notes.Add($"{q.TruckId}: {q.OutOfOrderRows} of {q.Rows} rows are out of timestamp order in the file; the engine evaluates them in timestamp order.");
        if (q.DuplicateRows > 0)
            notes.Add($"{q.TruckId}: {q.DuplicateRows} duplicate row(s); counted once.");
        if (q.MissingSlots.Count > 0)
            notes.Add($"{q.TruckId}: no reading at {string.Join(", ", q.MissingSlots.Select(Fmt.Time))} (expected every 5 min).");
    }
    return notes;
}

void WriteAlertsMarkdown(string path, int rows, int trucks, DateTimeOffset start, DateTimeOffset end,
    List<AlertDecision> alerts, List<(string Truck, string Why)> quiet, List<string> quality, int naive, List<SensitivityRow> sensitivity)
{
    var sb = new StringBuilder();
    sb.AppendLine("# What I would alert on, and why").AppendLine();
    sb.AppendLine($"Input: `data/sensor_readings.json`, {rows} readings from {trucks} trucks on {start.UtcDateTime:yyyy-MM-dd}, {Fmt.Time(start)} to {Fmt.Time(end)} UTC. " +
                  "Rules: `config/cargo_profiles.json` (every truck assumed pharma, 2 to 8 °C, alert after 20 min above).").AppendLine();
    sb.AppendLine($"A rule that alerts on every reading above 8 °C would send **{naive}** alerts. This engine sends **{alerts.Count}**.").AppendLine();

    sb.AppendLine($"## Alerts ({alerts.Count})").AppendLine();
    var n = 0;
    foreach (var a in alerts.OrderBy(a => a.Incident.AlertedAt))
    {
        n++;
        sb.AppendLine($"### {n}. {a.Incident.TruckId}: {(a.Incident.Direction == ExcursionDirection.High ? "too warm" : "too cold")}, alert at {Fmt.Time(a.Incident.AlertedAt)}").AppendLine();
        sb.AppendLine($"**Why:** {a.Why}").AppendLine();
        sb.AppendLine($"**When the data ends:** {a.Status}.").AppendLine();
        sb.AppendLine($"**For ops** ({a.SummarySource}): {a.Summary}").AppendLine();
    }

    sb.AppendLine("## No alert").AppendLine();
    foreach (var (truck, why) in quiet) sb.AppendLine($"- **{truck}:** {why}");
    sb.AppendLine();

    if (quality.Count > 0)
    {
        sb.AppendLine("## Data issues found and handled").AppendLine();
        foreach (var q in quality) sb.AppendLine($"- {q}");
        sb.AppendLine();
    }

    sb.AppendLine("## How the answer depends on the tolerance").AppendLine();
    sb.AppendLine("The tolerance is a business decision for Nimbus and each shipper. Same data, different warm tolerance:").AppendLine();
    sb.AppendLine("| Tolerance | Trucks alerted | Alert times |").AppendLine("|---|---|---|");
    foreach (var r in sensitivity)
        sb.AppendLine($"| {r.Minutes} min{(r.Minutes == config.ProfilesByName[config.DefaultProfile].HighToleranceMinutes ? " (chosen)" : "")} | " +
                      $"{(r.Trucks.Count == 0 ? "none" : string.Join(", ", r.Trucks))} | {r.FirstAlert} |");
    sb.AppendLine();

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, sb.ToString());
}

// ---------------------------------------------------------------------------------------------- sensitivity

(int ExitCode, List<SensitivityRow> Rows) RunSensitivity(bool print)
{
    var readings = SeedLoader.LoadReadings(dataFile);
    var truckProfiles = SeedLoader.LoadTruckProfiles(trucksFile);
    var rows = new List<SensitivityRow>();
    foreach (var minutes in new[] { 10, 15, 20, 25, 30, 35 })
    {
        var store = new InMemoryIncidentStore();
        DeviceSimulator.Run(new AlertEngine(config.WithHighTolerance(minutes), truckProfiles, store), SeedLoader.ToDeliveries(readings));
        var alerts = store.Events.Where(e => e.Type == EventType.ExcursionAlert).ToList();
        rows.Add(new SensitivityRow(minutes,
            alerts.Select(a => a.TruckId).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList(),
            alerts.Count == 0 ? "-" : string.Join(", ", alerts.OrderBy(a => a.TruckId, StringComparer.Ordinal).Select(a => $"{a.TruckId} {Fmt.Time(a.At)}"))));
    }

    if (print)
    {
        ConsoleTimeline.Heading("Sensitivity: same data, different warm tolerance");
        var chosen = config.ProfilesByName[config.DefaultProfile].HighToleranceMinutes;
        foreach (var r in rows)
            ConsoleTimeline.Write($" {r.Minutes,3} min{(r.Minutes == chosen ? " *" : "  ")}  {(r.Trucks.Count == 0 ? "no alerts" : r.FirstAlert)}\n",
                r.Minutes == chosen ? ConsoleColor.White : ConsoleColor.Gray);
        ConsoleTimeline.Write(" * the configured tolerance\n", ConsoleColor.DarkGray);
    }
    return (0, rows);
}

// ---------------------------------------------------------------------------------------------- scenarios

int RunScenarios(string? filter, bool verbose)
{
    var defs = ScenarioRunner.LoadAll(Path.Combine(root, "scenarios"))
        .Where(d => filter is null || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    if (defs.Count == 0)
    {
        Console.Error.WriteLine($"No scenario matches '{filter}'.");
        return 1;
    }

    ConsoleTimeline.Heading($"Scenarios: {defs.Count}");
    var results = defs.Select(d => ScenarioRunner.Run(d, config)).ToList();
    foreach (var r in results)
    {
        ConsoleTimeline.Write(r.Passed ? " PASS " : " FAIL ", r.Passed ? ConsoleColor.Green : ConsoleColor.Red);
        Console.WriteLine($" {r.Definition.Name,-30} {r.Definition.Title}");
        if (!r.Passed)
            foreach (var f in r.Failures) ConsoleTimeline.Write($"        {f}\n", ConsoleColor.Red);
        if (verbose)
        {
            ConsoleTimeline.Write($"        Proves: {r.Definition.Proves}\n", ConsoleColor.DarkGray);
            foreach (var e in r.Events) ConsoleTimeline.PrintEvent(e);
            Console.WriteLine();
        }
    }

    var passed = results.Count(r => r.Passed);
    ConsoleTimeline.Write($"\n {passed} of {results.Count} scenarios behave as designed.\n",
        passed == results.Count ? ConsoleColor.Green : ConsoleColor.Red);

    if (filter is null)
    {
        HtmlReport.Write(Path.Combine(outDir, "scenarios", "report.html"), new ReportModel(
            "Edge cases the alert engine has to get right",
            $"{passed} of {results.Count} scenarios behave as designed. Each one is a small, named story about one truck, " +
            "and each is also an automated test.",
            "Scenario files live in scenarios/. Add one and rerun to test a new idea live.",
            results.Select(r => new ReportSection(
                r.Definition.Title, $"{r.Definition.Name}, profile {r.Profile.Name} ({Fmt.Range(r.Profile)})",
                r.Definition.Proves, r.Passed, r.Passed ? null : string.Join("; ", r.Failures),
                new ChartData(r.Definition.TruckId, r.Profile, null,
                    r.Capture.Processed.GetValueOrDefault(r.Definition.TruckId) ?? [],
                    r.Capture.Faults.GetValueOrDefault(r.Definition.TruckId) ?? [],
                    r.Incidents, r.Events, r.NaiveBreaches))).ToList(),
            config.Engine.SilenceThreshold));
    }
    return passed == results.Count ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------- chaos

int RunChaos()
{
    var readings = SeedLoader.LoadReadings(dataFile);
    var truckProfiles = SeedLoader.LoadTruckProfiles(trucksFile);
    var baseline = SeedLoader.ToDeliveries(readings);
    var disordered = Chaos.Disorder(baseline);

    var a = Replay(baseline);
    var b = Replay(disordered);
    var same = a.SequenceEqual(b);

    ConsoleTimeline.Heading("Determinism check: same data, delivered shuffled and with resends");
    Console.WriteLine($" In order: {baseline.Count} deliveries. Shuffled (up to 8 min late) with ~5% resends: {disordered.Count} deliveries.");
    ConsoleTimeline.Write(same ? $" Identical decisions either way ({a.Count} incidents).\n" : " Decisions differ:\n",
        same ? ConsoleColor.Green : ConsoleColor.Red);
    if (!same)
        foreach (var line in a.Except(b).Select(x => $"   only in order: {x}").Concat(b.Except(a).Select(x => $"   only shuffled: {x}")))
            Console.WriteLine(line);
    return same ? 0 : 1;

    List<string> Replay(List<Delivery> deliveries)
    {
        var store = new InMemoryIncidentStore();
        DeviceSimulator.Run(new AlertEngine(config, truckProfiles, store), deliveries);
        return store.Incidents.Select(i =>
            $"{i.Id} {i.Direction} start={Fmt.Time(i.StartedAt)} alert={Fmt.Time(i.AlertedAt)} " +
            $"resolved={(i.ResolvedAt is { } r ? Fmt.Time(r) : "-")} peak={Fmt.Temp(i.PeakC)} reopened={i.ReopenCount}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}

int Help()
{
    Console.WriteLine("""
Usage: dotnet run --project src/ColdChain.Runner -- <command>

  all                Scenarios, determinism check, then the alert list for the seed data (default)
  alerts             The alert list for data/sensor_readings.json, with reasons; writes out/alerts.md and out/seed/report.html
  sensitivity        Which trucks alert at warm tolerances from 10 to 35 minutes
  scenarios [name]   Run scenarios with a full timeline; writes out/scenarios/report.html
  verify             Run scenarios quietly; exit code 1 if any behaves unexpectedly
  chaos              Replay the seed data shuffled and with resends; decisions must not change

Optional AI summaries: set LLM_ENDPOINT, LLM_API_KEY (and LLM_MODEL) for any OpenAI-compatible endpoint.
Without them, summaries come from a fixed template.
""");
    return 0;
}

static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "config", "cargo_profiles.json"))) return dir.FullName;
    }
    throw new DirectoryNotFoundException("Could not find config/cargo_profiles.json. Run from the repository folder.");
}

record AlertDecision(Incident Incident, string Why, string Status, string Summary, string SummarySource);
record SensitivityRow(int Minutes, List<string> Trucks, string FirstAlert);
