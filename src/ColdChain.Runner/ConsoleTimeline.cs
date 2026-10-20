using ColdChain.Core.Domain;
using ColdChain.Core.Engine;

namespace ColdChain.Runner;

/// <summary>Prints what the engine decided, per truck, in the order it happened.</summary>
public static class ConsoleTimeline
{
    public static string Label(EventType type) => type switch
    {
        EventType.ExcursionAlert => "ALERT",
        EventType.ExcursionReopened => "REOPENED",
        EventType.Reminder => "REMINDER",
        EventType.ExcursionResumed => "RESUMED",
        EventType.Resolved => "RESOLVED",
        EventType.BlipIgnored => "BLIP",
        EventType.SensorSilentDuringExcursion => "SILENT!",
        EventType.DeviceOffline => "OFFLINE",
        EventType.DeviceBackOnline => "ONLINE",
        EventType.SensorFault => "FAULT",
        EventType.LateReading => "LATE",
        EventType.DuplicateDropped => "DUPLICATE",
        EventType.ReadingRejected => "REJECTED",
        EventType.ProfileMissing => "NO PROFILE",
        _ => type.ToString().ToUpperInvariant()
    };

    public static void PrintTruck(string truckId, CargoProfile profile, IEnumerable<AlertEvent> events, int naive, int processed)
    {
        Write($"\n {truckId}", ConsoleColor.White);
        Write($"   {profile.Name}: {Fmt.Range(profile)}, alert after {Fmt.Span(profile.HighTolerance)} above / " +
              $"{Fmt.Span(profile.LowTolerance)} below\n", ConsoleColor.DarkGray);

        var list = events.ToList();
        if (list.Count == 0) Write("   (no events: cargo stayed in range)\n", ConsoleColor.DarkGray);
        foreach (var e in list) PrintEvent(e);

        var pages = list.Count(e => e.Severity == Severity.Critical);
        Write($"   {processed} readings evaluated. Naive per-reading threshold: {naive} alert(s). This engine: {pages} page(s).\n",
            ConsoleColor.DarkGray);
    }

    public static void PrintEvent(AlertEvent e)
    {
        var colour = e.Severity switch
        {
            Severity.Critical => ConsoleColor.Red,
            Severity.Warning => ConsoleColor.Yellow,
            _ => e.Type == EventType.Resolved ? ConsoleColor.Green : ConsoleColor.Gray
        };
        Write($"   {Fmt.Time(e.At)}  ", ConsoleColor.DarkGray);
        Write($"{Label(e.Type),-10} ", colour);
        Write(e.Message, e.Severity == Severity.Info && e.Type != EventType.Resolved ? ConsoleColor.Gray : ConsoleColor.White);
        if (e.IncidentId is not null) Write($"  [{e.IncidentId}]", ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    public static void Heading(string text)
    {
        Write($"\n{text}\n", ConsoleColor.Cyan);
        Write(new string('-', Math.Min(text.Length, 100)) + "\n", ConsoleColor.DarkCyan);
    }

    public static void Write(string text, ConsoleColor colour)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
