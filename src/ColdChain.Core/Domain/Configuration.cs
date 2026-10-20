using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChain.Core.Domain;

/// <summary>
/// What "too warm / too cold for too long" means for one kind of cargo.
/// These are configuration, not code: the customer owns these numbers.
/// </summary>
public sealed record CargoProfile
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required double MinC { get; init; }
    public required double MaxC { get; init; }

    /// <summary>How long cargo may stay above MaxC before it is an incident.</summary>
    public required int HighToleranceMinutes { get; init; }

    /// <summary>How long cargo may stay below MinC (freezing can be worse than warming for vaccines).</summary>
    public required int LowToleranceMinutes { get; init; }

    /// <summary>An excursion only ends once the temperature is this far back inside the range.</summary>
    public double HysteresisC { get; init; } = 0.5;

    /// <summary>Temperature must stay back in range this long before an incident is resolved.</summary>
    public int ClearAfterMinutes { get; init; } = 15;

    /// <summary>A new excursion within this window after resolution reopens the same incident.</summary>
    public int ReopenWindowMinutes { get; init; } = 60;

    /// <summary>While an incident is open, a reminder (not a new alert) is sent this often.</summary>
    public int ReminderIntervalMinutes { get; init; } = 60;

    [JsonIgnore] public TimeSpan HighTolerance => TimeSpan.FromMinutes(HighToleranceMinutes);
    [JsonIgnore] public TimeSpan LowTolerance => TimeSpan.FromMinutes(LowToleranceMinutes);
    [JsonIgnore] public TimeSpan ClearAfter => TimeSpan.FromMinutes(ClearAfterMinutes);
    [JsonIgnore] public TimeSpan ReopenWindow => TimeSpan.FromMinutes(ReopenWindowMinutes);
    [JsonIgnore] public TimeSpan ReminderInterval => TimeSpan.FromMinutes(ReminderIntervalMinutes);
}

/// <summary>Fleet-wide behaviour that does not depend on the cargo.</summary>
public sealed record EngineOptions
{
    /// <summary>No valid reading for this long means the device is treated as silent.</summary>
    public int SilenceThresholdMinutes { get; init; } = 15;

    /// <summary>The sensor's normal reporting interval; a longer step between readings means a missed reading.</summary>
    public int ExpectedIntervalMinutes { get; init; } = 5;

    /// <summary>Earlier short excursions within this window are mentioned when an alert fires.</summary>
    public int RecurrenceWindowMinutes { get; init; } = 60;

    /// <summary>Readings are held this long so late, out-of-order readings can be put back in order.</summary>
    public int AllowedLatenessMinutes { get; init; } = 10;

    /// <summary>Readings stamped further than this into the future are rejected (device clock skew).</summary>
    public int MaxFutureSkewMinutes { get; init; } = 5;

    public double PlausibleMinC { get; init; } = -50;
    public double PlausibleMaxC { get; init; } = 60;

    /// <summary>Values common probes report on fault: -127 (disconnected), 85 (power-on reset).</summary>
    public double[] KnownFaultValues { get; init; } = [-127.0, 85.0];

    [JsonIgnore] public TimeSpan ExpectedInterval => TimeSpan.FromMinutes(ExpectedIntervalMinutes);
    [JsonIgnore] public TimeSpan RecurrenceWindow => TimeSpan.FromMinutes(RecurrenceWindowMinutes);
    [JsonIgnore] public TimeSpan SilenceThreshold => TimeSpan.FromMinutes(SilenceThresholdMinutes);
    [JsonIgnore] public TimeSpan AllowedLateness => TimeSpan.FromMinutes(AllowedLatenessMinutes);
    [JsonIgnore] public TimeSpan MaxFutureSkew => TimeSpan.FromMinutes(MaxFutureSkewMinutes);
}

public sealed record EngineConfig
{
    public EngineOptions Engine { get; init; } = new();
    public required string DefaultProfile { get; init; }
    public required List<CargoProfile> Profiles { get; init; }

    public IReadOnlyDictionary<string, CargoProfile> ProfilesByName =>
        Profiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Same config with every profile's warm tolerance replaced: used by the sensitivity check.</summary>
    public EngineConfig WithHighTolerance(int minutes) =>
        this with { Profiles = Profiles.Select(p => p with { HighToleranceMinutes = minutes }).ToList() };

    public static EngineConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<EngineConfig>(json, JsonDefaults.Options)
                     ?? throw new InvalidDataException($"Could not read config from {path}");
        if (!config.ProfilesByName.ContainsKey(config.DefaultProfile))
            throw new InvalidDataException($"Default profile '{config.DefaultProfile}' is not defined in {path}");
        foreach (var p in config.Profiles)
            if (p.MinC >= p.MaxC)
                throw new InvalidDataException($"Profile '{p.Name}': MinC must be below MaxC");
        return config;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
