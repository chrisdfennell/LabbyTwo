using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.MoonrakerPlugin;

/// <summary>
/// A Klipper printer through <a href="https://moonraker.readthedocs.io">Moonraker</a>.
///
/// A print is the longest-running, least-attended, most-expensive-to-lose job in a house
/// full of services, and it is the one thing nothing else on the dashboard can see. Nine
/// hours in, the difference between "finished" and "the filament ran out at hour two" is
/// worth a notification.
///
/// This is also the worked example of <see cref="ActionsFor"/> depending on live state: the
/// pause button only appears while something is printing, because a button that cannot work
/// is indistinguishable from a broken one.
/// </summary>
public sealed class MoonrakerProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public const string ProviderType = "moonraker";

    public string Type => ProviderType;
    public string DisplayName => "Klipper / Moonraker";
    public string Icon => "🖨️";
    public string Category => "Infrastructure";

    public string Description =>
        "A 3D printer running Klipper: what it is printing, how far through, both temperatures, "
        + "and buttons to pause or cancel.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Moonraker URL", FieldKind.Url, "http://192.168.86.70:7125", Required: true,
            Help: "Moonraker's own port, not Fluidd's or Mainsail's — those are the web interfaces in front of it."),

        new("api_key", "API key", FieldKind.Password,
            Help: "Only if you have locked Moonraker down. A printer on a trusted LAN usually has "
                  + "no key at all, and this can stay empty.")
        {
            Advanced = true,
        },

        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("progress_percent", "Print progress", "%"),
        new("extruder_temp_c", "Hotend", "°C", 1),
        new("bed_temp_c", "Bed", "°C", 1),
        new("print_minutes", "Printing for", " min"),
        new("remaining_minutes", "Estimated left", " min"),
        new("printing", "Printing"),
        new("error", "In error"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Print failed", "error", Comparison.Above, 0, ForMinutes: 0,
            Why: "Klipper has stopped with an error — a failed home, a thermal runaway, a cancelled print. "
                 + "The machine is sitting there hot and idle until somebody looks at it."),
    ];

    /// <summary>
    /// The last state seen per connection. <see cref="ActionsFor"/> has only the connection
    /// to go on, and which buttons make sense is a fact about the printer rather than about
    /// its configuration — so the probe leaves it here.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _states = new();

    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("pause", "Pause", "⏸️")
        {
            Description = "Parks the head and stops. The bed and hotend stay hot.",
            Confirms = false,
        },
        new("resume", "Resume", "▶️") { Confirms = false },
        new("cancel", "Cancel", "🛑")
        {
            Description = "Ends the print. What is on the bed is scrap.",
            ConfirmMessage = "The print stops and cannot be resumed. Whatever is on the bed is waste plastic, "
                             + "and a long print starts again from nothing.",
            Dangerous = true,
        },
    ];

    /// <summary>
    /// Only the buttons that mean something right now. Pausing something that is not
    /// printing does nothing; resuming something that is not paused does nothing; and
    /// offering both at all times teaches people the buttons are unreliable.
    /// </summary>
    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection)
    {
        var state = _states.GetValueOrDefault(connection.Id, "");

        // Before the first probe there is nothing to go on. Everything is offered rather
        // than nothing, so a freshly added printer is not a dead card until the next sweep.
        if (state.Length == 0)
            return Actions;

        return
        [
            .. Actions.Where(action => action.Id switch
            {
                "pause" => state is "printing",
                "resume" => state is "paused",
                "cancel" => state is "printing" or "paused",
                _ => true,
            })
        ];
    }

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url").TrimEnd('/');
        if (url.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No Moonraker URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{url}/printer/objects/query?print_stats&extruder&heater_bed&display_status");
            Authorise(request, connection);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120)));

            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed,
                    (int)response.StatusCode == 401
                        ? "Moonraker refused the request. It wants an API key, or the one configured is wrong."
                        : $"HTTP {(int)response.StatusCode} from Moonraker.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            stopwatch.Stop();

            if (!document.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("status", out var status))
                return ProbeResult.Down(stopwatch.Elapsed, "Moonraker answered, but not with a printer status.");

            var printStats = status.TryGetProperty("print_stats", out var stats) ? stats : default;
            var state = printStats.ValueKind == JsonValueKind.Object && printStats.TryGetProperty("state", out var s)
                ? s.GetString() ?? ""
                : "";

            _states[connection.Id] = state;

            var progress = Number(status, "display_status", "progress") * 100;
            var elapsed = Number(printStats, "print_duration");
            var filename = printStats.ValueKind == JsonValueKind.Object
                           && printStats.TryGetProperty("filename", out var name)
                ? name.GetString() ?? ""
                : "";

            var metrics = new Dictionary<string, double>
            {
                ["extruder_temp_c"] = Number(status, "extruder", "temperature"),
                ["bed_temp_c"] = Number(status, "heater_bed", "temperature"),
                ["printing"] = state is "printing" ? 1 : 0,
                ["error"] = state is "error" ? 1 : 0,
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            // Progress and the time estimates only exist while there is a job. Reporting
            // them as zero the rest of the time would draw a chart that dives to nothing
            // every time a print finishes, which reads as a fault rather than a success.
            if (state is "printing" or "paused")
            {
                metrics["progress_percent"] = Math.Clamp(progress, 0, 100);
                metrics["print_minutes"] = elapsed / 60;

                // Moonraker does not publish a remaining time; the slicer's estimate lives
                // in the file. This is the crude one every interface falls back to, and it
                // is honest as long as nobody mistakes it for the slicer's.
                if (progress > 1)
                    metrics["remaining_minutes"] = (elapsed / (progress / 100) - elapsed) / 60;
            }

            var message = state switch
            {
                "printing" => $"Printing {Short(filename)} — {progress:0}%",
                "paused" => $"Paused at {progress:0}% — {Short(filename)}",
                "complete" => $"Finished {Short(filename)}",
                "error" => "Klipper is in an error state.",
                "cancelled" => "Last print was cancelled.",
                "standby" or "" => "Idle",
                _ => state,
            };

            // Up even in "error": Moonraker answered, which is what up means here. Whether
            // a stopped print is a problem is the user's call, made with the rule above —
            // a provider that returned Down for it would put a hole in the uptime history
            // of a printer that was reachable the whole time.
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics,
                filename.Length > 0 ? new Dictionary<string, string> { ["File"] = filename } : null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        var path = action.Id switch
        {
            "pause" => "/printer/print/pause",
            "resume" => "/printer/print/resume",
            "cancel" => "/printer/print/cancel",
            _ => null,
        };

        if (path is null)
            return ActionResult.Failed($"Moonraker does not know how to run “{action.Id}”.");

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{connection.Settings.Get("url").TrimEnd('/')}{path}");
            Authorise(request, connection);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return ActionResult.Failed(
                    $"Moonraker answered HTTP {(int)response.StatusCode}. {body[..Math.Min(body.Length, 200)]}".Trim());
            }
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ex.GetBaseException().Message);
        }

        // The state is now stale and the buttons drawn from it are wrong until the next
        // sweep. Clearing it means every button is offered for one probe rather than the
        // pause button lingering on a print that just stopped.
        _states.TryRemove(connection.Id, out _);

        return ActionResult.Done(action.Id switch
        {
            "pause" => "Paused. The hotend stays hot, so do not leave it too long.",
            "resume" => "Resumed.",
            _ => "Cancelled.",
        });
    }

    private static void Authorise(HttpRequestMessage request, Connection connection)
    {
        if (connection.Settings.Get("api_key") is { Length: > 0 } key)
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
    }

    /// <summary>One number out of Moonraker's nested status object, or zero if it is not there.</summary>
    private static double Number(JsonElement status, string objectName, string property)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty(objectName, out var node)
            || node.ValueKind != JsonValueKind.Object)
            return 0;

        return Number(node, property);
    }

    private static double Number(JsonElement node, string property) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : 0;

    /// <summary>Slicer filenames are long and end in the part that matters least.</summary>
    private static string Short(string filename)
    {
        var name = filename.Contains('/') ? filename[(filename.LastIndexOf('/') + 1)..] : filename;
        if (name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];

        return name.Length <= 40 ? name : name[..39] + "…";
    }
}
