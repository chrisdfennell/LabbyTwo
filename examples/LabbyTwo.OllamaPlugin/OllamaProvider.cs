using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.OllamaPlugin;

/// <summary>
/// <a href="https://ollama.com">Ollama</a>: how many models are pulled, which are resident,
/// and how much VRAM they are holding.
///
/// The number that earns this its place on a dashboard is <c>vram_gb</c>. A model is loaded
/// on first use and evicted after an idle timeout, so the machine quietly swings between
/// nothing and twenty gigabytes — and the first thing anybody notices is that a request
/// which took a second yesterday takes forty today, because the model was evicted and has
/// to come off disk again. That is a chart, not a mystery.
/// </summary>
public sealed class OllamaProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public const string ProviderType = "ollama";

    public string Type => ProviderType;
    public string DisplayName => "Ollama";
    public string Icon => "🦙";
    public string Category => "Infrastructure";

    public string Description =>
        "Local models: how many are pulled, which are loaded right now, and the VRAM they hold.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.86.57:11434", Required: true,
            Help: "Ollama listens on 127.0.0.1 by default and will not answer another machine until "
                  + "OLLAMA_HOST=0.0.0.0 is set on it."),

        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("models", "Models pulled"),
        new("models_loaded", "Models loaded"),
        new("vram_gb", "VRAM held", " GB", 1),
        new("disk_gb", "Models on disk", " GB", 1),
    ];

    /// <summary>
    /// Nothing suggested. Every threshold worth setting here depends on the card in the
    /// machine — 20 GB held is idle on a 48 GB card and the edge of a stall on a 24 GB one —
    /// and a suggestion that fires on half the installs that accept it is worse than none.
    /// </summary>
    public IReadOnlyList<SuggestedRule> SuggestedRules => [];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url").TrimEnd('/');
        if (url.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120)));

            // Everything pulled, and everything resident. Two calls because Ollama keeps
            // them apart, and the difference between the two is the whole story.
            var tags = await ReadAsync(http, $"{url}/api/tags", cts.Token);
            var running = await ReadAsync(http, $"{url}/api/ps", cts.Token);
            stopwatch.Stop();

            var pulled = Models(tags, "models");
            var loaded = Models(running, "models");

            var vram = loaded.Sum(m => m.SizeVram);
            var disk = pulled.Sum(m => m.Size);

            var details = new Dictionary<string, string>();
            if (loaded.Count > 0)
                details["Loaded"] = string.Join(", ", loaded.Select(m => m.Name));

            return ProbeResult.Up(stopwatch.Elapsed,
                loaded.Count == 0
                    ? $"{pulled.Count} models pulled, none loaded"
                    : $"{loaded.Count} loaded ({string.Join(", ", loaded.Select(m => m.Name))}), {Gb(vram):0.#} GB VRAM",
                new Dictionary<string, double>
                {
                    ["models"] = pulled.Count,
                    ["models_loaded"] = loaded.Count,
                    ["vram_gb"] = Gb(vram),
                    ["disk_gb"] = Gb(disk),
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                },
                details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} from {url}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private sealed record Model(string Name, long Size, long SizeVram);

    private static List<Model> Models(JsonDocument document, string property)
    {
        using (document)
        {
            if (!document.RootElement.TryGetProperty(property, out var array)
                || array.ValueKind != JsonValueKind.Array)
                return [];

            var models = new List<Model>();
            foreach (var entry in array.EnumerateArray())
                models.Add(new Model(
                    entry.TryGetProperty("name", out var name) ? name.GetString() ?? "?" : "?",
                    entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    entry.TryGetProperty("size_vram", out var vram) && vram.TryGetInt64(out var held) ? held : 0));

            return models;
        }
    }

    /// <summary>Ollama reports bytes. Gigabytes as everyone means them, not gibibytes.</summary>
    private static double Gb(long bytes) => bytes / 1_000_000_000d;
}
