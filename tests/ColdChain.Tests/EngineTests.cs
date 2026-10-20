using ColdChain.Core.Domain;
using ColdChain.Core.Engine;
using ColdChain.Core.Simulation;
using ColdChain.Core.Storage;
using Xunit;

namespace ColdChain.Tests;

/// <summary>Properties that must hold for any data, checked on the full seed replay.</summary>
public class EngineTests
{
    private static readonly List<Reading> Seed = SeedLoader.LoadReadings(Path.Combine(TestPaths.Data, "sensor_readings.json"));
    private static readonly Dictionary<string, string> Trucks = SeedLoader.LoadTruckProfiles(Path.Combine(TestPaths.Data, "trucks.csv"));

    private static InMemoryIncidentStore Replay(IEnumerable<Delivery> deliveries)
    {
        var store = new InMemoryIncidentStore();
        DeviceSimulator.Run(new AlertEngine(TestPaths.Config, Trucks, store), deliveries);
        return store;
    }

    private static string Key(Incident i) =>
        $"{i.Id}|{i.Direction}|{i.StartedAt:O}|{i.AlertedAt:O}|{i.ResolvedAt:O}|{i.PeakC}|{i.ReopenCount}";

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Shuffled_and_duplicated_delivery_gives_identical_incidents(int seed)
    {
        var baseline = SeedLoader.ToDeliveries(Seed);
        var inOrder = Replay(baseline).Incidents.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var shuffled = Replay(Chaos.Disorder(baseline, seed)).Incidents.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(inOrder.SequenceEqual(shuffled),
            $"in order:{Environment.NewLine}{string.Join(Environment.NewLine, inOrder)}{Environment.NewLine}" +
            $"shuffled:{Environment.NewLine}{string.Join(Environment.NewLine, shuffled)}");
    }

    [Fact]
    public void Seed_data_alerts_on_exactly_T01_T04_and_T05()
    {
        var store = Replay(SeedLoader.ToDeliveries(Seed));
        var alerted = store.Events.Where(e => e.Type == EventType.ExcursionAlert).Select(e => e.TruckId).OrderBy(t => t).ToList();
        Assert.True(alerted.SequenceEqual(new[] { "T-01", "T-04", "T-05" }), "alerted: " + string.Join(", ", alerted));
    }

    /// <summary>T-05 is T-01's data shuffled in the file. Shuffling must not change a single decision.</summary>
    [Fact]
    public void T05_shuffled_in_the_file_gets_the_same_decisions_as_T01()
    {
        var store = Replay(SeedLoader.ToDeliveries(Seed));
        string Shape(string truck) => string.Join(";", store.Events.Where(e => e.TruckId == truck)
            .Select(e => $"{e.Type}@{e.At:HH:mm}:{e.Message.Replace(truck, "T")}"));
        Assert.True(Shape("T-01") == Shape("T-05"), $"T-01: {Shape("T-01")}{Environment.NewLine}T-05: {Shape("T-05")}");
    }

    /// <summary>T-04 has no 08:35 reading. Duration must come from timestamps, so the alert still fires at 20 min.</summary>
    [Fact]
    public void T04_missing_reading_does_not_shorten_the_excursion()
    {
        var store = Replay(SeedLoader.ToDeliveries(Seed));
        var incident = store.Incidents.Single(i => i.TruckId == "T-04");
        Assert.Equal(20.0, (incident.AlertedAt - incident.StartedAt).TotalMinutes);
        Assert.Equal(1, incident.MissedReadings.Count);
        Assert.Equal(1, incident.PriorExcursions.Count);
    }

    [Fact]
    public void Each_incident_pages_once_and_never_re_alerts()
    {
        var store = Replay(SeedLoader.ToDeliveries(Seed));
        foreach (var incident in store.Incidents)
        {
            var alerts = store.Events.Count(e => e.IncidentId == incident.Id && e.Type == EventType.ExcursionAlert);
            Assert.Equal(1, alerts);
        }
    }

    [Fact]
    public void Engine_pages_far_less_than_a_naive_threshold()
    {
        var store = new InMemoryIncidentStore();
        var engine = new AlertEngine(TestPaths.Config, Trucks, store);
        DeviceSimulator.Run(engine, SeedLoader.ToDeliveries(Seed));

        var naive = engine.Monitors.Sum(m => m.NaiveThresholdBreaches);
        var pages = store.Events.Count(e => e.Severity == Severity.Critical);
        Assert.True(pages > 0, "the seed data contains real problems, so something must page");
        Assert.True(pages * 5 < naive, $"expected far fewer pages than naive alerts, got {pages} vs {naive}");
    }

    [Fact]
    public void Trucks_never_affect_each_other()
    {
        var all = Replay(SeedLoader.ToDeliveries(Seed));
        foreach (var truck in Trucks.Keys)
        {
            var alone = Replay(SeedLoader.ToDeliveries(Seed.Where(r => r.TruckId == truck)));
            Assert.Equal(
                all.Incidents.Where(i => i.TruckId == truck).Select(Key),
                alone.Incidents.Select(Key));
        }
    }

    [Fact]
    public void Unknown_truck_gets_the_strictest_default_profile_and_a_warning()
    {
        var store = new InMemoryIncidentStore();
        var engine = new AlertEngine(TestPaths.Config, new Dictionary<string, string>(), store);
        var t0 = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
        DeviceSimulator.Run(engine, [new Delivery(new Reading("TRK-NEW", t0, 5.0), t0)]);

        Assert.Contains(store.Events, e => e.Type == EventType.ProfileMissing && e.Severity == Severity.Warning);
        Assert.Equal(TestPaths.Config.DefaultProfile, engine.Monitors.Single().Profile.Name);
    }
}
