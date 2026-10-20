using System.Globalization;
using ColdChain.Core.Domain;

namespace ColdChain.Core.Engine;

/// <summary>Human-readable formatting shared by messages, console and report.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Temp(double? c) => c is { } v ? v.ToString("0.0", Inv) + " °C" : "unknown";

    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", Inv);

    public static string DateTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv);

    public static string Span(TimeSpan s)
    {
        var total = (int)Math.Round(s.TotalMinutes);
        if (total < 60) return $"{total} min";
        if (total >= 2880) return $"{total / 1440} d {total % 1440 / 60} h";
        var hours = total / 60;
        var mins = total % 60;
        return mins == 0 ? $"{hours} h" : $"{hours} h {mins:00} min";
    }

    public static string Range(CargoProfile p) =>
        $"{p.MinC.ToString("0.#", Inv)} to {p.MaxC.ToString("0.#", Inv)} °C";
}
