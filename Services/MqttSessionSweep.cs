using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Closes the broker sockets of MQTT connections that no longer exist or are switched off.
///
/// The pool that holds those sockets lives inside the provider and knows nothing about the
/// configuration — it is deliberately given nothing the container would have to supply, see
/// <see cref="MqttPool"/> — so it can open a session but never learn that one is no longer
/// wanted. The health monitor simply stops probing a deleted connection, which left its
/// subscription running, and receiving, until the process restarted.
///
/// Two triggers. A configuration change sweeps at once, so deleting or disabling a
/// connection closes its socket there and then. The interval catches what no change
/// announces: a connection tried from the editor's Test button and never saved holds a
/// session under an id the database has never seen.
/// </summary>
public sealed class MqttSessionSweep : IBackgroundJob
{
    private readonly ConfigStore _config;
    private readonly Registry _registry;
    private readonly ILogger<MqttSessionSweep> _log;

    public MqttSessionSweep(ConfigStore config, Registry registry, ILogger<MqttSessionSweep> log)
    {
        _config = config;
        _registry = registry;
        _log = log;

        // A job is a singleton built once at startup, so this subscription lasts exactly as
        // long as the thing it serves. Changed is a plain Action raised on the saver's own
        // thread, hence Task.Run: the sweep reloads the connections and may wait on the
        // pool's lock, and neither should hold up whoever pressed Save.
        _config.Changed += () => _ = Task.Run(SweepQuietlyAsync);
    }

    public string Name => "mqtt-sessions";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task RunAsync(CancellationToken ct) => SweepAsync(ct);

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var pools = _registry.Providers.OfType<MqttProvider>().ToList();
        if (pools.Count == 0)
            return 0;

        var keep = (await _config.ConnectionsAsync(ct))
            .Where(c => c.Enabled && _registry.Provider(c.Provider) is MqttProvider)
            .Select(c => c.Id)
            .ToHashSet();

        var closed = 0;
        foreach (var pool in pools)
            closed += await pool.CloseSessionsExceptAsync(keep, ct);

        if (closed > 0)
            _log.LogInformation("Closed {Count} MQTT session(s) for connections that are gone or switched off", closed);

        return closed;
    }

    private async Task SweepQuietlyAsync()
    {
        try
        {
            await SweepAsync();
        }
        catch (Exception ex)
        {
            // Nobody is awaiting this, so an exception here would go nowhere. The interval
            // run will try again.
            _log.LogWarning(ex, "Could not close unused MQTT sessions");
        }
    }
}
