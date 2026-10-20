using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ColdChain.Core.Domain;
using ColdChain.Core.Engine;

namespace ColdChain.Runner;

/// <summary>
/// The facts an ops person needs about one alert. Everything here was computed by the rules engine;
/// a summarizer may only rephrase these facts, never decide anything.
/// </summary>
public sealed record AlertFacts(
    string TruckId,
    string CargoProfile,
    string Problem,
    double LimitC,
    int MinutesOutOfRange,
    int ToleranceMinutes,
    double PeakC,
    double? LatestC,
    int MissedReadings,
    int PriorExcursions,
    double? HumidityAtStart,
    double? HumidityAtPeak,
    string StatusAtEndOfData)
{
    public static AlertFacts From(Incident i, CargoProfile p, double? latest, DateTimeOffset dataEnd)
    {
        var high = i.Direction == ExcursionDirection.High;
        var end = i.Status == IncidentStatus.Open ? dataEnd : i.RecoveredAt ?? dataEnd;
        var status = i.Status switch
        {
            IncidentStatus.Open => "still out of range when the data ends",
            IncidentStatus.Recovering => $"back in range since {Fmt.Time(i.RecoveredAt!.Value)}, not yet confirmed stable",
            _ => $"resolved at {Fmt.Time(i.ResolvedAt!.Value)}"
        };
        return new AlertFacts(i.TruckId, p.Name, high ? "too warm" : "too cold", high ? p.MaxC : p.MinC,
            (int)Math.Round((end - i.StartedAt).TotalMinutes), high ? p.HighToleranceMinutes : p.LowToleranceMinutes,
            i.PeakC, latest, i.MissedReadings.Count, i.PriorExcursions.Count, i.HumidityAtStart, i.HumidityAtPeak, status);
    }
}

public interface IAlertSummarizer
{
    string Source { get; }
    string Summarize(AlertFacts facts);
}

/// <summary>Deterministic plain-English summary. Always available; the fallback for the LLM.</summary>
public sealed class TemplateSummarizer : IAlertSummarizer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public string Source => "template";

    public string Summarize(AlertFacts f)
    {
        var dir = f.Problem == "too warm" ? "above" : "below";
        var ongoing = f.StatusAtEndOfData.StartsWith("still", StringComparison.Ordinal);
        var sb = new StringBuilder(
            $"Truck {f.TruckId} {(ongoing ? "has been" : "was")} {dir} {f.LimitC.ToString("0.#", Inv)} °C for {f.MinutesOutOfRange} minutes " +
            $"(peak {f.PeakC.ToString("0.0", Inv)} °C) and is {f.StatusAtEndOfData}.");

        if (f.PriorExcursions > 0)
            sb.Append($" It is excursion {f.PriorExcursions + 1} in the last hour, so this looks like a recurring fault rather than a one-off.");
        if (f.HumidityAtStart is { } h0 && f.HumidityAtPeak is { } h1 && h1 - h0 >= 3)
            sb.Append($" Humidity rose with it ({h0:0}% to {h1:0}%), which fits warm air getting in or a struggling reefer unit.");
        if (f.MissedReadings > 0)
            sb.Append($" {f.MissedReadings} reading{(f.MissedReadings == 1 ? " is" : "s are")} missing from this period.");
        sb.Append(f.Problem == "too warm"
            ? " Check the reefer unit and contact the driver."
            : " Check the reefer setpoint: the cargo may be freezing.");
        return sb.ToString();
    }
}

/// <summary>
/// Optional bonus: rephrases the facts with any OpenAI-compatible chat completions endpoint (OpenAI, Azure OpenAI,
/// a local model). Configured with LLM_ENDPOINT, LLM_API_KEY and LLM_MODEL. Any failure, or an answer that drops
/// the truck id or runs long, falls back to the template, so alerting never depends on the model.
/// </summary>
public sealed class LlmSummarizer : IAlertSummarizer
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IAlertSummarizer _fallback = new TemplateSummarizer();

    private LlmSummarizer(string endpoint, string apiKey, string model)
    {
        _endpoint = endpoint;
        _model = model;
        if (endpoint.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
            _http.DefaultRequestHeaders.Add("api-key", apiKey);
        else
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string Source { get; private set; } = "LLM";

    public static IAlertSummarizer FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("LLM_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key)) return new TemplateSummarizer();
        return new LlmSummarizer(endpoint, key, Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-4o-mini");
    }

    public string Summarize(AlertFacts facts)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _model,
                temperature = 0.2,
                max_tokens = 160,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You write one- or two-sentence alerts for a refrigerated-truck operations team. " +
                                  "Use only the facts given. Always name the truck, the duration and the peak temperature. " +
                                  "You may suggest a possible cause only as 'possible' or 'likely', and only from: reefer unit " +
                                  "failure, door left open, door seal leak. Do not invent numbers. No greeting, no markdown."
                    },
                    new { role = "user", content = JsonSerializer.Serialize(facts) }
                }
            });
            using var response = _http.PostAsync(_endpoint, new StringContent(body, Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();

            if (string.IsNullOrEmpty(text) || !text.Contains(facts.TruckId) || text.Length > 400)
                throw new InvalidDataException("summary failed validation");
            Source = "LLM";
            return text;
        }
        catch (Exception e)
        {
            Source = e is InvalidDataException ? "template; the LLM answer failed checks" : $"template; LLM unavailable ({e.GetType().Name})";
            return _fallback.Summarize(facts);
        }
    }
}
